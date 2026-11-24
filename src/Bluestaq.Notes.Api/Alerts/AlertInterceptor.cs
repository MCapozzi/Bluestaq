using System.Data.Common;
using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Bluestaq.Notes.Api.Alerts;

/// <summary>
/// Queues alerts in the same transaction as the change that causes them: a new share alerts its user, and a
/// title/content edit alerts every user the note is shared with. The actor is never alerted about their own change.
/// </summary>
public sealed class AlertInterceptor(ICurrentUserAccessor currentUser, TimeProvider clock, ILogger<AlertInterceptor> logger)
    : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        QueueAlertsAsync(eventData.Context, sync: true, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await QueueAlertsAsync(eventData.Context, sync: false, cancellationToken);
        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        try
        {
            PendingAlertsOfDeletedNotes(eventData.Context)?.ExecuteDelete();
        }
        catch (DbException ex)
        {
            LogCleanupFailure(ex);
        }

        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        try
        {
            if (PendingAlertsOfDeletedNotes(eventData.Context) is { } pending)
            {
                await pending.ExecuteDeleteAsync(cancellationToken);
            }
        }
        catch (DbException ex)
        {
            LogCleanupFailure(ex);
        }

        return result;
    }

    /// <summary>
    /// The note is already deleted, so a cleanup failure must not fail the request; AlertService drops the leftover
    /// alerts at dispatch because their note is no longer readable.
    /// </summary>
    private void LogCleanupFailure(DbException ex) =>
        logger.LogWarning(ex, "Could not remove pending alerts of deleted notes; they will be dropped at dispatch");

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => _deletedNoteIds = null;

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _deletedNoteIds = null;
        return Task.CompletedTask;
    }

    private List<long>? _deletedNoteIds;

    /// <summary>
    /// Once a note deletion is saved, its undelivered alerts are deleted. The delete re-checks <c>DeliveredAt</c> when it
    /// runs, so an alert the dispatcher delivers meanwhile stays and keeps throttling pushes.
    /// </summary>
    private IQueryable<NoteAlert>? PendingAlertsOfDeletedNotes(DbContext? context)
    {
        var ids = _deletedNoteIds;
        _deletedNoteIds = null;
        return context is NotesDbContext db && ids is { Count: > 0 }
            ? db.NoteAlerts.Where(a => a.DeliveredAt == null && ids.Contains(a.NoteId))
            : null;
    }

    /// <summary>PUT marks every field modified, so compare values to skip resubmissions of identical text.</summary>
    private static bool Changed(PropertyEntry<Note, string> property) =>
        property.IsModified && !string.Equals(property.OriginalValue, property.CurrentValue, StringComparison.Ordinal);

    private async Task QueueAlertsAsync(DbContext? context, bool sync, CancellationToken ct)
    {
        if (context is not NotesDbContext db)
        {
            return;
        }

        db.ChangeTracker.DetectChanges();
        _deletedNoteIds = db.ChangeTracker.Entries<Note>().Where(e => e.State == EntityState.Deleted).Select(e => e.Entity.Id).ToList();
        if (currentUser.UserId is not { } actorId)
        {
            return;
        }

        var wanted = new List<(long RecipientId, long NoteId, NoteAlertKind Kind)>();

        foreach (var share in db.ChangeTracker.Entries<NoteShare>().Where(e => e.State == EntityState.Added))
        {
            wanted.Add((share.Entity.UserId, share.Entity.NoteId, NoteAlertKind.Shared));
        }

        var editedNoteIds = db.ChangeTracker.Entries<Note>()
            .Where(e => e.State == EntityState.Modified
                && (Changed(e.Property(n => n.Title)) || Changed(e.Property(n => n.Content))))
            .Select(e => e.Entity.Id)
            .ToList();
        if (editedNoteIds.Count > 0)
        {
            var sharesQuery = db.NoteShares.AsNoTracking().Where(s => editedNoteIds.Contains(s.NoteId)).Select(s => new { s.UserId, s.NoteId });
            var shares = sync ? sharesQuery.ToList() : await sharesQuery.ToListAsync(ct);
            wanted.AddRange(shares.Select(s => (s.UserId, s.NoteId, NoteAlertKind.Updated)));
        }

        wanted = wanted.Where(w => w.RecipientId != actorId).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        var noteIds = wanted.Select(w => w.NoteId).Distinct().ToList();
        var pendingQuery = db.NoteAlerts.Where(a => a.DeliveredAt == null && noteIds.Contains(a.NoteId));
        var pending = sync ? pendingQuery.ToList() : await pendingQuery.ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));

        foreach (var (recipientId, noteId, kind) in wanted)
        {
            var existing = pending.FirstOrDefault(a => a.RecipientUserId == recipientId && a.NoteId == noteId && a.Kind == kind);
            if (existing is not null)
            {
                existing.ActorUserId = actorId;
                existing.LastOccurredAt = now;
                existing.Occurrences++;
            }
            else
            {
                db.NoteAlerts.Add(new NoteAlert
                {
                    RecipientUserId = recipientId,
                    NoteId = noteId,
                    Kind = kind,
                    ActorUserId = actorId,
                    FirstOccurredAt = now,
                    LastOccurredAt = now,
                });
            }
        }
    }
}
