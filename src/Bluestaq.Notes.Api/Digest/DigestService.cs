using System.Globalization;
using System.Text;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Digest;

/// <summary>Builds note digests and emails the ones that are due.</summary>
public sealed class DigestService(NotesDbContext db, IEmailSender sender, TimeProvider clock, ILogger<DigestService> logger)
{
    public static TimeSpan? PeriodOf(DigestFrequency frequency) => frequency switch
    {
        DigestFrequency.Daily => TimeSpan.FromDays(1),
        DigestFrequency.Weekly => TimeSpan.FromDays(7),
        _ => null,
    };

    /// <summary>
    /// Notes explicitly shared with the user during (<paramref name="since"/>, <paramref name="until"/>], and notes
    /// someone else changed during the window after sharing them. A null <paramref name="since"/> covers all time.
    /// </summary>
    public async Task<NoteDigest> BuildAsync(
        long userId, string username, DigestFrequency frequency, DateTime? since, DateTime until, CancellationToken ct)
    {
        var shares = await db.NoteShares.AsNoTracking()
            .Where(s => s.UserId == userId && s.SharedAt <= until)
            .Where(s => since == null
                || s.SharedAt > since
                || (s.Note!.UpdatedAt > since && s.Note.UpdatedAt > s.SharedAt && s.Note.UpdatedByUserId != userId))
            .Select(s => new
            {
                s.NoteId,
                s.Note!.Title,
                SharedBy = s.SharedByUserId ?? s.Note.OwnerId,
                s.SharedAt,
                s.Note.UpdatedByUserId,
                s.Note.UpdatedAt,
            })
            .ToListAsync(ct);

        var userIds = shares.SelectMany(s => new[] { s.SharedBy, s.UpdatedByUserId }).Distinct().ToList();
        var usernames = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);
        string NameOf(long id) => usernames.GetValueOrDefault(id, $"user {id}");

        var newlyShared = shares
            .Where(s => since == null || s.SharedAt > since)
            .OrderByDescending(s => s.SharedAt).ThenBy(s => s.NoteId)
            .Select(s => new DigestNote(s.NoteId, s.Title, NameOf(s.SharedBy), s.SharedAt, NameOf(s.UpdatedByUserId), s.UpdatedAt))
            .ToList();
        var updated = shares
            .Where(s => s.UpdatedAt > s.SharedAt && s.UpdatedByUserId != userId && (since == null || s.UpdatedAt > since))
            .OrderByDescending(s => s.UpdatedAt).ThenBy(s => s.NoteId)
            .Select(s => new DigestNote(s.NoteId, s.Title, NameOf(s.SharedBy), s.SharedAt, NameOf(s.UpdatedByUserId), s.UpdatedAt))
            .ToList();

        return new NoteDigest(username, frequency, since, until, newlyShared, updated);
    }

    /// <summary>
    /// Emails every opted-in user whose period has elapsed since their last digest (or who has never had one). Each user
    /// is claimed with a conditional update first, so concurrent instances don't send twice; a failed send releases the
    /// claim so the digest is retried. Users with nothing new get no email but their window still advances.
    /// </summary>
    /// <returns>The number of emails sent.</returns>
    public async Task<int> SendDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var dailyCutoff = now - PeriodOf(DigestFrequency.Daily)!.Value;
        var weeklyCutoff = now - PeriodOf(DigestFrequency.Weekly)!.Value;

        var due = await db.Users.AsNoTracking()
            .Where(u => (u.DigestFrequency == DigestFrequency.Daily && (u.DigestLastSentAt == null || u.DigestLastSentAt <= dailyCutoff))
                || (u.DigestFrequency == DigestFrequency.Weekly && (u.DigestLastSentAt == null || u.DigestLastSentAt <= weeklyCutoff)))
            .OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.Username, u.DigestFrequency, u.DigestLastSentAt })
            .ToListAsync(ct);

        var sent = 0;
        foreach (var user in due)
        {
            var claimed = await db.Users
                .Where(u => u.Id == user.Id && u.DigestFrequency == user.DigestFrequency && u.DigestLastSentAt == user.DigestLastSentAt)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DigestLastSentAt, now), ct);
            if (claimed == 0)
            {
                continue;
            }

            try
            {
                var digest = await BuildAsync(user.Id, user.Username, user.DigestFrequency, user.DigestLastSentAt, now, ct);
                if (!digest.IsEmpty)
                {
                    await sender.SendAsync(ToEmail(digest), ct);
                    sent++;
                }
            }
            catch (Exception ex)
            {
                // Release the claim on any failure, shutdown included, so the window's activity isn't skipped.
                await db.Users
                    .Where(u => u.Id == user.Id && u.DigestLastSentAt == now)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.DigestLastSentAt, user.DigestLastSentAt), CancellationToken.None);
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                {
                    throw;
                }

                logger.LogError(ex, "Sending the note digest for user {UserId} failed; it will be retried.", user.Id);
            }
        }

        return sent;
    }

    public static EmailMessage ToEmail(NoteDigest digest)
    {
        var period = digest.Frequency == DigestFrequency.Weekly ? "weekly" : "daily";
        var body = new StringBuilder()
            .AppendLine($"Hi {digest.Username},")
            .AppendLine()
            .AppendLine(digest.Since is { } since
                ? $"Here is your {period} notes digest for {Format(since)} to {Format(digest.Until)}."
                : $"Here is your first {period} notes digest: every note shared with you so far.");

        if (digest.NewlyShared.Count > 0)
        {
            body.AppendLine().AppendLine($"Shared with you ({digest.NewlyShared.Count}):");
            foreach (var n in digest.NewlyShared)
            {
                body.AppendLine($"- \"{Clean(n.Title)}\" (note {n.NoteId}), shared by {n.SharedBy} on {Format(n.SharedAt)}; last updated {Format(n.LastUpdatedAt)} by {n.UpdatedBy}");
            }
        }

        if (digest.Updated.Count > 0)
        {
            body.AppendLine().AppendLine($"Changed since they were shared with you ({digest.Updated.Count}):");
            foreach (var n in digest.Updated)
            {
                body.AppendLine($"- \"{Clean(n.Title)}\" (note {n.NoteId}), updated {Format(n.LastUpdatedAt)} by {n.UpdatedBy}; shared by {n.SharedBy} on {Format(n.SharedAt)}");
            }
        }

        body.AppendLine().AppendLine("To stop these emails, set your digest frequency to None (PUT /api/digest).");
        return new EmailMessage(DigestOptions.Recipient, $"Your {period} notes digest ({digest.Username})", body.ToString());
    }

    private static string Format(DateTime value) => value.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Clean(string title) => new(title.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
