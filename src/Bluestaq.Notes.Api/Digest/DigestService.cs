using System.Globalization;
using System.Text;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.Digest;

/// <summary>Builds note digests and emails each opted-in user's digest once a day, just after midnight.</summary>
public sealed class DigestService(
    NotesDbContext db, IEmailSender sender, TimeProvider clock, IOptions<DigestOptions> options, ILogger<DigestService> logger)
{
    public DigestSchedule Schedule { get; } = new(TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone));

    /// <summary>
    /// Activity in [<paramref name="from"/>, <paramref name="until"/>) for the sections the user opted in to. Edits mean
    /// title or content changes by someone else; only a note's latest edit is known, so a later edit hides earlier ones.
    /// Public notes the user owns, or that are also shared with them while they get shared notes, are left out.
    /// </summary>
    public async Task<NoteDigest> BuildAsync(
        long userId, string username, bool sharedNotes, bool publicNotes, DateTime from, DateTime until, CancellationToken ct)
    {
        var shares = !sharedNotes ? [] : await db.NoteShares.AsNoTracking()
            .Where(s => s.UserId == userId)
            .Where(s => (s.SharedAt >= from && s.SharedAt < until)
                || (s.Note!.ContentUpdatedAt >= from && s.Note.ContentUpdatedAt < until
                    && s.Note.ContentUpdatedAt > s.SharedAt && s.Note.ContentUpdatedByUserId != userId))
            .Select(s => new
            {
                s.NoteId,
                s.Note!.Title,
                SharedBy = s.SharedByUserId ?? s.Note.OwnerId,
                s.SharedAt,
                UpdatedByUserId = s.Note.ContentUpdatedByUserId,
                UpdatedAt = s.Note.ContentUpdatedAt,
            })
            .ToListAsync(ct);

        var publicNotesList = !publicNotes ? [] : await db.Notes.AsNoTracking()
            .Where(n => n.Visibility == NoteVisibility.Public && n.OwnerId != userId)
            .Where(n => !sharedNotes || !n.Shares.Any(s => s.UserId == userId))
            .Where(n => (n.PublicSince >= from && n.PublicSince < until)
                || (n.ContentUpdatedAt >= from && n.ContentUpdatedAt < until
                    && n.ContentUpdatedAt > n.PublicSince && n.ContentUpdatedByUserId != userId))
            .Select(n => new { n.Id, n.Title, n.OwnerId, PublicSince = n.PublicSince!.Value, n.ContentUpdatedByUserId, n.ContentUpdatedAt })
            .ToListAsync(ct);

        var userIds = shares.SelectMany(s => new[] { s.SharedBy, s.UpdatedByUserId })
            .Concat(publicNotesList.SelectMany(n => new[] { n.OwnerId, n.ContentUpdatedByUserId }))
            .Distinct().ToList();
        var usernames = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);
        string NameOf(long id) => usernames.GetValueOrDefault(id, $"user {id}");
        bool InWindow(DateTime at) => at >= from && at < until;

        var newlyShared = shares
            .Where(s => InWindow(s.SharedAt))
            .OrderByDescending(s => s.SharedAt).ThenBy(s => s.NoteId)
            .Select(s => new DigestNote(s.NoteId, s.Title, NameOf(s.SharedBy), s.SharedAt, NameOf(s.UpdatedByUserId), s.UpdatedAt))
            .ToList();
        var updated = shares
            .Where(s => InWindow(s.UpdatedAt) && s.UpdatedAt > s.SharedAt && s.UpdatedByUserId != userId)
            .OrderByDescending(s => s.UpdatedAt).ThenBy(s => s.NoteId)
            .Select(s => new DigestNote(s.NoteId, s.Title, NameOf(s.SharedBy), s.SharedAt, NameOf(s.UpdatedByUserId), s.UpdatedAt))
            .ToList();
        var newPublic = publicNotesList
            .Where(n => InWindow(n.PublicSince))
            .OrderByDescending(n => n.PublicSince).ThenBy(n => n.Id)
            .Select(n => new PublicDigestNote(n.Id, n.Title, NameOf(n.OwnerId), n.PublicSince, NameOf(n.ContentUpdatedByUserId), n.ContentUpdatedAt))
            .ToList();
        var updatedPublic = publicNotesList
            .Where(n => InWindow(n.ContentUpdatedAt) && n.ContentUpdatedAt > n.PublicSince && n.ContentUpdatedByUserId != userId)
            .OrderByDescending(n => n.ContentUpdatedAt).ThenBy(n => n.Id)
            .Select(n => new PublicDigestNote(n.Id, n.Title, NameOf(n.OwnerId), n.PublicSince, NameOf(n.ContentUpdatedByUserId), n.ContentUpdatedAt))
            .ToList();

        return new NoteDigest(username, from, until, newlyShared, updated, newPublic, updatedPublic);
    }

    /// <summary>
    /// Within <see cref="DigestOptions.SendWindow"/> after midnight, emails every opted-in user the digest for the day
    /// that just ended, once. Each user is claimed with a conditional update first, so concurrent instances don't send
    /// twice; a failed send releases the claim so the next pass retries it. Users with nothing new get no email.
    /// </summary>
    /// <returns>The number of emails sent.</returns>
    public async Task<int> SendDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var (from, until) = Schedule.PreviousDay(now);
        if (now - until >= options.Value.SendWindow)
        {
            return 0;
        }

        var due = await db.Users.AsNoTracking()
            .Where(u => (u.DigestSharedNotes || u.DigestPublicNotes) && (u.DigestLastSentAt == null || u.DigestLastSentAt < until))
            .OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.Username, u.DigestSharedNotes, u.DigestPublicNotes, u.DigestLastSentAt })
            .ToListAsync(ct);

        var sent = 0;
        foreach (var user in due)
        {
            var claimed = await db.Users
                .Where(u => u.Id == user.Id && u.DigestLastSentAt == user.DigestLastSentAt)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DigestLastSentAt, until), ct);
            if (claimed == 0)
            {
                continue;
            }

            try
            {
                var digest = await BuildAsync(user.Id, user.Username, user.DigestSharedNotes, user.DigestPublicNotes, from, until, ct);
                if (digest.IsEmpty)
                {
                    logger.LogDebug("No digest email for user {UserId}: nothing new.", user.Id);
                    continue;
                }

                await sender.SendAsync(ToEmail(digest, Schedule), ct);
                sent++;
            }
            catch (Exception ex)
            {
                // Release the claim on any failure, shutdown included, so the next pass can retry it.
                await db.Users
                    .Where(u => u.Id == user.Id && u.DigestLastSentAt == until)
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

    public static EmailMessage ToEmail(NoteDigest digest, DigestSchedule schedule)
    {
        string Format(DateTime utc) => schedule.ToLocal(utc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var day = schedule.ToLocal(digest.From).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var body = new StringBuilder()
            .AppendLine($"Hi {digest.Username},")
            .AppendLine()
            .AppendLine($"Here is your daily notes digest for {day}. Times are {schedule.Zone.Id}.");

        void Section<T>(string heading, IReadOnlyList<T> notes, Func<T, string> line)
        {
            if (notes.Count == 0)
            {
                return;
            }

            body.AppendLine().AppendLine($"{heading} ({notes.Count}):");
            foreach (var n in notes)
            {
                body.AppendLine($"- {line(n)}");
            }
        }

        Section("Shared with you", digest.NewlyShared, n =>
            $"\"{Clean(n.Title)}\" (note {n.NoteId}), shared by {n.SharedBy} at {Format(n.SharedAt)}; last updated {Format(n.LastUpdatedAt)} by {n.UpdatedBy}");
        Section("Changed since they were shared with you", digest.Updated, n =>
            $"\"{Clean(n.Title)}\" (note {n.NoteId}), updated {Format(n.LastUpdatedAt)} by {n.UpdatedBy}; shared by {n.SharedBy} at {Format(n.SharedAt)}");
        Section("New public notes", digest.NewPublic, n =>
            $"\"{Clean(n.Title)}\" (note {n.NoteId}) by {n.Owner}, public since {Format(n.PublicSince)}; last updated {Format(n.LastUpdatedAt)} by {n.UpdatedBy}");
        Section("Changed public notes", digest.UpdatedPublic, n =>
            $"\"{Clean(n.Title)}\" (note {n.NoteId}) by {n.Owner}, updated {Format(n.LastUpdatedAt)} by {n.UpdatedBy}");

        body.AppendLine().AppendLine("To stop these emails, set sharedNotes and publicNotes to false (PUT /api/digest).");
        return new EmailMessage(DigestOptions.Recipient, $"Your daily notes digest for {day} ({digest.Username})", body.ToString());
    }

    private static string Clean(string title) => new(title.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
