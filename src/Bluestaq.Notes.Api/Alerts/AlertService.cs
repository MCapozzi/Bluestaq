using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.Alerts;

public sealed record AlertBatch(long RecipientUserId, IReadOnlyList<long> AlertIds, AlertMessage Message);

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

        var userIds = notes.Values.SelectMany(n => new[] { n.OwnerId, n.UpdatedByUserId })
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
                var share = g.FirstOrDefault(a => a.Kind == NoteAlertKind.Shared);
                return new NoteAlertItem(
                    note.Id,
                    note.Title,
                    NameOf(share?.ActorUserId ?? note.OwnerId),
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

        return new AlertBatch(userId, pending.Select(a => a.Id).ToList(), new AlertMessage(now, items));
    }

    public Task MarkDeliveredAsync(AlertBatch batch, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var ids = batch.AlertIds;
        return db.NoteAlerts
            .Where(a => ids.Contains(a.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.DeliveredAt, now), ct);
    }
}
