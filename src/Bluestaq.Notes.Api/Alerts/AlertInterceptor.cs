using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Bluestaq.Notes.Api.Alerts;

/// <summary>
/// Queues alerts in the same transaction as the change that causes them: a new share alerts its user, and a
/// title/content edit alerts every user the note is shared with. The actor is never alerted about their own change.
/// </summary>
public sealed class AlertInterceptor(ICurrentUserAccessor currentUser, TimeProvider clock) : SaveChangesInterceptor
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

    private async Task QueueAlertsAsync(DbContext? context, bool sync, CancellationToken ct)
    {
        if (context is not NotesDbContext db || currentUser.UserId is not { } actorId)
        {
            return;
        }

        db.ChangeTracker.DetectChanges();
        var wanted = new List<(long RecipientId, long NoteId, NoteAlertKind Kind)>();

        foreach (var share in db.ChangeTracker.Entries<NoteShare>().Where(e => e.State == EntityState.Added))
        {
            wanted.Add((share.Entity.UserId, share.Entity.NoteId, NoteAlertKind.Shared));
        }

        var editedNoteIds = db.ChangeTracker.Entries<Note>()
            .Where(e => e.State == EntityState.Modified
                && (e.Property(n => n.Title).IsModified || e.Property(n => n.Content).IsModified))
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
