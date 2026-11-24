using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.Alerts;

public sealed record AlertBatch(long RecipientUserId, IReadOnlyList<BatchedAlert> Alerts, AlertMessage Message);

/// <summary>An alert row as it was when the batch was built; <see cref="Occurrences"/> identifies that version.</summary>
public sealed record BatchedAlert(long Id, int Occurrences);

/// <summary>Builds aggregated alert batches and enforces the per-user push interval.</summary>
public sealed class AlertService(NotesDbContext db, TimeProvider clock, IOptions<AlertOptions> options)
{
    /// <summary>
    /// Returns the user's pending alerts as one message, or null when there are none or the user was already pushed to
    /// within <see cref="AlertOptions.PushInterval"/>. Alerts for notes the user can no longer read are discarded.
    /// </summary>
    public async Task<AlertBatch?> TakePendingAsync(long userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var windowStart = now - options.Value.PushInterval;

        // Delivered rows only matter for throttling, so anything older than one interval can go.
        await db.NoteAlerts.Where(a => a.RecipientUserId == userId && a.DeliveredAt <= windowStart).ExecuteDeleteAsync(ct);
        if (await db.NoteAlerts.AnyAsync(a => a.RecipientUserId == userId && a.DeliveredAt != null, ct))
        {
            return null;
        }

        var pending = await db.NoteAlerts.AsNoTracking()
            .Where(a => a.RecipientUserId == userId && a.DeliveredAt == null)
            .ToListAsync(ct);
        if (pending.Count == 0)
        {
            return null;
        }

        var noteIds = pending.Select(a => a.NoteId).Distinct().ToList();
        var notes = await db.Notes.AsNoTracking()
            .ReadableBy(userId)
            .Where(n => noteIds.Contains(n.Id))
            .Select(n => new { n.Id, n.Title, n.OwnerId, n.UpdatedByUserId, n.UpdatedAt })
            .ToDictionaryAsync(n => n.Id, ct);

        var unreadable = pending.Where(a => !notes.ContainsKey(a.NoteId)).Select(a => a.Id).ToList();
        if (unreadable.Count > 0)
        {
            await db.NoteAlerts.Where(a => unreadable.Contains(a.Id)).ExecuteDeleteAsync(ct);
            pending.RemoveAll(a => !notes.ContainsKey(a.NoteId));
            if (pending.Count == 0)
            {
                return null;
            }
        }

        // Who shared each note with this user, for batches that only contain edits.
        var sharers = await db.NoteShares.AsNoTracking()
            .Where(s => s.UserId == userId && noteIds.Contains(s.NoteId))
            .ToDictionaryAsync(s => s.NoteId, s => s.SharedByUserId, ct);
        long SharerOf(long noteId, long ownerId) => sharers.GetValueOrDefault(noteId) ?? ownerId;

        var userIds = notes.Values.SelectMany(n => new[] { SharerOf(n.Id, n.OwnerId), n.UpdatedByUserId })
            .Concat(pending.Select(a => a.ActorUserId))
            .Distinct()
            .ToList();
        var usernames = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);
        string NameOf(long id) => usernames.GetValueOrDefault(id, $"user {id}");

        var items = pending
            .GroupBy(a => a.NoteId)
            .Select(g =>
            {
                var note = notes[g.Key];
                // For a share request (sent to the owner), the requesting editor is reported as the sharer.
                var share = g.FirstOrDefault(a => a.Kind is NoteAlertKind.Shared or NoteAlertKind.ShareRequested);
                return new NoteAlertItem(
                    note.Id,
                    note.Title,
                    NameOf(share?.ActorUserId ?? SharerOf(note.Id, note.OwnerId)),
                    NameOf(note.UpdatedByUserId),
                    note.UpdatedAt,
                    g.Select(a => a.Kind).Distinct().Order().ToList(),
                    g.Sum(a => a.Occurrences),
                    g.Min(a => a.FirstOccurredAt),
                    g.Max(a => a.LastOccurredAt));
            })
            .OrderByDescending(i => i.LastOccurredAt)
            .ThenBy(i => i.NoteId)
            .ToList();

        return new AlertBatch(userId, pending.Select(a => new BatchedAlert(a.Id, a.Occurrences)).ToList(), new AlertMessage(now, items));
    }

    /// <summary>
    /// Marks the batch's rows delivered, but only those unchanged since the batch was built: an event merged into a row
    /// in the meantime wasn't in the message, so that row stays pending for the next push.
    /// </summary>
    public async Task MarkDeliveredAsync(AlertBatch batch, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var (id, occurrences) in batch.Alerts)
        {
            await db.NoteAlerts
                .Where(a => a.Id == id && a.Occurrences == occurrences)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.DeliveredAt, now), ct);
        }
    }
}
