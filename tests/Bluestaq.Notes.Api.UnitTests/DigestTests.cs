using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Controllers;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Digest;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.UnitTests;

internal sealed class FakeEmailSender : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];
    public bool IsConfigured { get; set; } = true;
    public Exception? FailWith { get; set; }

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Sent.Add(message);
        return Task.CompletedTask;
    }
}

public sealed class DigestTests : IAsyncLifetime
{
    // TestDb starts at 2026-01-01 12:00 UTC, 07:00 in New York; midnight there is 05:00 UTC in winter.
    private static readonly DateTime Jan2Midnight = new(2026, 1, 2, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jan3Midnight = Jan2Midnight.AddDays(1);

    private readonly TestDb _db = new();
    private readonly FakeEmailSender _email = new();
    private long _alice;
    private long _bob;
    private Note _plan = null!;

    public async Task InitializeAsync()
    {
        _alice = (await _db.AddUserAsync("alice")).Id;
        _bob = (await _db.AddUserAsync("bob")).Id;
        _plan = await _db.AddNoteAsync(_alice, "Plan");
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private static DigestSchedule NewYork => new(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

    private DigestService Service(NotesDbContext ctx) =>
        new(ctx, _email, _db.Clock, Options.Create(new DigestOptions()), NullLogger<DigestService>.Instance);

    private DateTime Now => _db.Clock.GetUtcNow().UtcDateTime;

    private void At(DateTime utc) => _db.Clock.SetUtcNow(new DateTimeOffset(utc));

    private async Task<int> SendDueAsync()
    {
        await using var ctx = _db.CreateContext();
        return await Service(ctx).SendDueAsync(CancellationToken.None);
    }

    private async Task<NoteDigest> BuildAsync(bool sharedNotes, bool publicNotes)
    {
        await using var ctx = _db.CreateContext();
        return await Service(ctx).BuildAsync(_bob, "bob", sharedNotes, publicNotes, TestDb.Start.UtcDateTime.AddDays(-1), Now.AddSeconds(1), default);
    }

    private async Task ShareAsync(long noteId, long withUser, long sharedBy)
    {
        await using var ctx = _db.CreateContext();
        ctx.NoteShares.Add(new NoteShare { NoteId = noteId, UserId = withUser, SharedAt = Now, SharedByUserId = sharedBy });
        await ctx.SaveChangesAsync();
    }

    private async Task UpdateNoteAsync(long noteId, long actor, Action<Note> change)
    {
        _db.CurrentUser.UserId = actor;
        await using var ctx = _db.CreateContext();
        change(await ctx.Notes.SingleAsync(n => n.Id == noteId));
        await ctx.SaveChangesAsync();
    }

    private Task EditAsync(long noteId, long actor, string title) => UpdateNoteAsync(noteId, actor, n => n.Title = title);

    private async Task<Note> AddPublicNoteAsync(long ownerId, string title)
    {
        var note = await _db.AddNoteAsync(ownerId, title);
        await UpdateNoteAsync(note.Id, ownerId, n => n.Visibility = NoteVisibility.Public);
        return note;
    }

    private async Task OptInAsync(long userId, bool sharedNotes = true, bool publicNotes = false)
    {
        await using var ctx = _db.CreateContext();
        await ctx.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.DigestSharedNotes, sharedNotes)
            .SetProperty(u => u.DigestPublicNotes, publicNotes));
    }

    private async Task<User> UserAsync(long id)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.Users.AsNoTracking().SingleAsync(u => u.Id == id);
    }

    private DigestController Controller(NotesDbContext ctx, long userId) =>
        new(ctx, Service(ctx), _db.Clock) { ControllerContext = TestPrincipals.ControllerContext(TestPrincipals.For(userId)) };

    [Fact]
    public async Task Users_are_opted_out_by_default_and_get_no_email()
    {
        await ShareAsync(_plan.Id, _bob, _alice);
        At(Jan2Midnight.AddMinutes(1));

        Assert.Equal(0, await SendDueAsync());
        Assert.Empty(_email.Sent);
        var bob = await UserAsync(_bob);
        Assert.Equal((false, false, (DateTime?)null), (bob.DigestSharedNotes, bob.DigestPublicNotes, bob.DigestLastSentAt));
    }

    [Fact]
    public async Task Digest_goes_out_once_at_midnight_and_covers_only_the_previous_day()
    {
        await ShareAsync(_plan.Id, _bob, _alice);
        await OptInAsync(_bob);
        At(Jan2Midnight.AddMinutes(1));

        Assert.Equal(1, await SendDueAsync());
        Assert.Equal(0, await SendDueAsync());

        var email = Assert.Single(_email.Sent);
        Assert.Equal(("TEST@TEST.TEST", "Your daily notes digest for 2026-01-01 (bob)"), (email.To, email.Subject));
        Assert.Contains("Shared with you (1):", email.TextBody);
        Assert.Contains("\"Plan\"", email.TextBody);
        Assert.Contains("shared by alice at 2026-01-01 07:00", email.TextBody);
        Assert.Equal(Jan2Midnight, (await UserAsync(_bob)).DigestLastSentAt);

        _email.Sent.Clear();
        At(Jan2Midnight.AddHours(6));
        var other = await _db.AddNoteAsync(_alice, "Other");
        await ShareAsync(other.Id, _bob, _alice);
        await EditAsync(_plan.Id, _alice, "Plan v2");
        At(Jan3Midnight.AddMinutes(30));

        Assert.Equal(1, await SendDueAsync());

        var body = Assert.Single(_email.Sent).TextBody;
        Assert.Contains("digest for 2026-01-02", body);
        Assert.Contains("Shared with you (1):", body);
        Assert.Contains("\"Other\"", body);
        Assert.Contains("Changed since they were shared with you (1):", body);
        Assert.Contains("\"Plan v2\"", body);
    }

    [Fact]
    public async Task Nothing_is_sent_outside_the_window_after_midnight()
    {
        await ShareAsync(_plan.Id, _bob, _alice);
        await OptInAsync(_bob);

        Assert.Equal(0, await SendDueAsync());
        At(Jan2Midnight.AddHours(1));
        Assert.Equal(0, await SendDueAsync());

        Assert.Empty(_email.Sent);
        Assert.Null((await UserAsync(_bob)).DigestLastSentAt);
    }

    [Fact]
    public async Task Opted_in_users_with_nothing_new_get_no_email()
    {
        await OptInAsync(_bob, sharedNotes: true, publicNotes: true);
        At(Jan2Midnight.AddMinutes(1));

        Assert.Equal(0, await SendDueAsync());

        Assert.Empty(_email.Sent);
        Assert.Equal(Jan2Midnight, (await UserAsync(_bob)).DigestLastSentAt);
    }

    [Fact]
    public async Task Public_opt_in_reports_new_and_edited_public_notes_from_others()
    {
        var old = await AddPublicNoteAsync(_alice, "Old public");
        At(Jan2Midnight.AddHours(6));
        await EditAsync(old.Id, _alice, "Old public v2");
        await AddPublicNoteAsync(_alice, "Fresh");
        await _db.AddNoteAsync(_alice, "Secret");
        await AddPublicNoteAsync(_bob, "Bobs");
        var sharedPublic = await AddPublicNoteAsync(_alice, "Shared public");
        await ShareAsync(sharedPublic.Id, _bob, _alice);
        await OptInAsync(_bob, sharedNotes: true, publicNotes: true);
        At(Jan3Midnight.AddMinutes(1));

        Assert.Equal(1, await SendDueAsync());

        var body = Assert.Single(_email.Sent).TextBody;
        Assert.Contains("New public notes (1):", body);
        Assert.Contains("\"Fresh\" (note", body);
        Assert.Contains("Changed public notes (1):", body);
        Assert.Contains("\"Old public v2\"", body);
        Assert.Contains("Shared with you (1):", body);
        Assert.Contains("\"Shared public\"", body);
        Assert.DoesNotContain("Secret", body);
        Assert.DoesNotContain("Bobs", body);
    }

    [Fact]
    public async Task Each_section_needs_its_own_opt_in()
    {
        await ShareAsync(_plan.Id, _bob, _alice);
        var open = await AddPublicNoteAsync(_alice, "Open");
        await ShareAsync(open.Id, _bob, _alice);

        var publicOnly = await BuildAsync(sharedNotes: false, publicNotes: true);
        Assert.Empty(publicOnly.NewlyShared);
        Assert.Equal("Open", Assert.Single(publicOnly.NewPublic).Title);

        var sharedOnly = await BuildAsync(sharedNotes: true, publicNotes: false);
        Assert.Equal(2, sharedOnly.NewlyShared.Count);
        Assert.Empty(sharedOnly.NewPublic);

        Assert.True((await BuildAsync(false, false)).IsEmpty);
    }

    [Fact]
    public async Task Own_edits_edits_before_sharing_and_revoked_shares_are_left_out()
    {
        await EditAsync(_plan.Id, _alice, "Edited before sharing");
        _db.Clock.Advance(TimeSpan.FromMinutes(1));
        await ShareAsync(_plan.Id, _bob, _alice);
        _db.Clock.Advance(TimeSpan.FromMinutes(1));
        await EditAsync(_plan.Id, _bob, "Bob's own edit");

        var digest = await BuildAsync(true, true);
        Assert.Single(digest.NewlyShared);
        Assert.Empty(digest.Updated);

        await using var ctx = _db.CreateContext();
        await ctx.NoteShares.Where(s => s.UserId == _bob).ExecuteDeleteAsync();
        Assert.True((await BuildAsync(true, true)).IsEmpty);
    }

    [Fact]
    public async Task Visibility_changes_are_not_reported_as_edits()
    {
        await ShareAsync(_plan.Id, _bob, _alice);
        _db.Clock.Advance(TimeSpan.FromMinutes(1));
        await UpdateNoteAsync(_plan.Id, _alice, n => n.Visibility = NoteVisibility.Public);

        var digest = await BuildAsync(sharedNotes: true, publicNotes: false);
        Assert.Empty(digest.Updated);
        Assert.Equal(_plan.CreatedAt, Assert.Single(digest.NewlyShared).LastUpdatedAt);

        digest = await BuildAsync(sharedNotes: false, publicNotes: true);
        Assert.Empty(digest.UpdatedPublic);
        Assert.Equal(Now, Assert.Single(digest.NewPublic).PublicSince);

        await UpdateNoteAsync(_plan.Id, _alice, n => n.Visibility = NoteVisibility.Private);
        Assert.True((await BuildAsync(sharedNotes: false, publicNotes: true)).IsEmpty);
    }

    [Fact]
    public async Task A_failed_send_is_retried_on_the_next_pass()
    {
        await ShareAsync(_plan.Id, _bob, _alice);
        await OptInAsync(_bob);
        At(Jan2Midnight.AddMinutes(1));
        _email.FailWith = new InvalidOperationException("smtp down");

        Assert.Equal(0, await SendDueAsync());
        Assert.Null((await UserAsync(_bob)).DigestLastSentAt);

        _email.FailWith = null;
        Assert.Equal(1, await SendDueAsync());
    }

    [Fact]
    public async Task A_send_cancelled_by_shutdown_releases_the_claim()
    {
        await ShareAsync(_plan.Id, _bob, _alice);
        await OptInAsync(_bob);
        At(Jan2Midnight.AddMinutes(1));
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        _email.FailWith = new OperationCanceledException(shutdown.Token);

        await using (var ctx = _db.CreateContext())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(ctx).SendDueAsync(shutdown.Token));
        }

        Assert.Null((await UserAsync(_bob)).DigestLastSentAt);
    }

    [Fact]
    public async Task Controller_defaults_to_opted_out_and_updates_opt_ins()
    {
        await using var ctx = _db.CreateContext();

        var settings = Assert.IsType<DigestSettingsResponse>(Assert.IsType<OkObjectResult>(await Controller(ctx, _bob).Get(default)).Value);
        Assert.Equal(new DigestSettingsResponse(false, false, null, null, "America/New_York", "TEST@TEST.TEST"), settings);

        settings = Assert.IsType<DigestSettingsResponse>(Assert.IsType<OkObjectResult>(
            await Controller(ctx, _bob).Update(new UpdateDigestRequest(false, true), default)).Value);
        Assert.Equal((false, true, Jan2Midnight), (settings.SharedNotes, settings.PublicNotes, settings.NextDigestAt!.Value));

        settings = Assert.IsType<DigestSettingsResponse>(Assert.IsType<OkObjectResult>(
            await Controller(ctx, _bob).Update(new UpdateDigestRequest(false, false), default)).Value);
        Assert.Null(settings.NextDigestAt);
    }

    [Fact]
    public async Task Controller_preview_shows_todays_activity_without_sending()
    {
        await ShareAsync(_plan.Id, _bob, _alice);
        await OptInAsync(_bob);
        await using var ctx = _db.CreateContext();

        var digest = Assert.IsType<NoteDigest>(Assert.IsType<OkObjectResult>(await Controller(ctx, _bob).Preview(default)).Value);

        Assert.Equal(("bob", "Plan", "alice"), (digest.Username, Assert.Single(digest.NewlyShared).Title, digest.NewlyShared[0].SharedBy));
        Assert.Equal((Jan2Midnight.AddDays(-1), Jan2Midnight), (digest.From, digest.Until));
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public void Email_text_uses_local_times_and_strips_control_characters_from_titles()
    {
        var from = Jan2Midnight.AddDays(-1);
        var email = DigestService.ToEmail(new NoteDigest("bob", from, Jan2Midnight,
            [new DigestNote(1, "Line1\r\nBcc: x", "alice", TestDb.Start.UtcDateTime, "alice", TestDb.Start.UtcDateTime)], [],
            [], [new PublicDigestNote(2, "Pub", "carol", from, "dave", TestDb.Start.UtcDateTime)]), NewYork);

        Assert.Contains("\"Line1  Bcc: x\"", email.TextBody);
        Assert.Contains("shared by alice at 2026-01-01 07:00", email.TextBody);
        Assert.Contains("Times are America/New_York.", email.TextBody);
        Assert.Contains("\"Pub\" (note 2) by carol, updated 2026-01-01 07:00 by dave", email.TextBody);
        Assert.Equal("Your daily notes digest for 2026-01-01 (bob)", email.Subject);
    }

    [Fact]
    public void Schedule_follows_local_midnight_across_daylight_saving()
    {
        // US daylight saving starts 2026-03-08, so that day is 23 hours long.
        var (start, end) = NewYork.PreviousDay(new DateTime(2026, 3, 9, 4, 30, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 3, 9, 4, 0, 0, DateTimeKind.Utc), end);
        Assert.Equal(new DateTime(2026, 3, 10, 4, 0, 0, DateTimeKind.Utc), NewYork.NextMidnight(end));
        Assert.Equal(end, NewYork.StartOfDay(end.AddHours(23)));
    }

    [Fact]
    public void Schedule_starts_the_day_at_the_first_valid_time_where_clocks_skip_midnight()
    {
        var midnight = new DateTime(1, 1, 1, 0, 0, 0);
        var zone = TimeZoneInfo.CreateCustomTimeZone("Skips-midnight", TimeSpan.FromHours(-3), "Skips midnight", "Std", "Dst",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(midnight, 9, 6), TimeZoneInfo.TransitionTime.CreateFixedDateRule(midnight, 4, 5)),
        ]);
        var schedule = new DigestSchedule(zone);

        // 2026-09-06 local clocks jump from 00:00 (-03:00) to 01:00 (-02:00).
        Assert.Equal(new DateTime(2026, 9, 6, 3, 0, 0, DateTimeKind.Utc), schedule.StartOfDay(new DateTime(2026, 9, 6, 15, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task Dispatcher_sends_due_digests_and_stays_idle_without_a_mail_server()
    {
        await ShareAsync(_plan.Id, _bob, _alice);
        await OptInAsync(_bob);
        At(Jan2Midnight.AddMinutes(1));
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(_db.Clock)
            .AddSingleton<IEmailSender>(_email)
            .AddSingleton(Options.Create(new DigestOptions()))
            .AddLogging()
            .AddScoped(_ => _db.CreateContext())
            .AddScoped<DigestService>()
            .BuildServiceProvider();
        var dispatcher = new DigestDispatcher(services.GetRequiredService<IServiceScopeFactory>(), _email,
            Options.Create(new DigestOptions()), _db.Clock, NullLogger<DigestDispatcher>.Instance);

        Assert.Equal(1, await dispatcher.SendOnceAsync(default));
        Assert.Equal(TimeSpan.FromMinutes(1), dispatcher.NextDelay());
        At(Jan3Midnight.AddSeconds(-20));
        Assert.Equal(TimeSpan.FromSeconds(20), dispatcher.NextDelay());

        _email.IsConfigured = false;
        await dispatcher.StartAsync(default);
        await dispatcher.ExecuteTask!;
        await dispatcher.StopAsync(default);
    }

    [Fact]
    public void Smtp_sender_is_configured_only_with_a_host_and_builds_plain_text_mail()
    {
        Assert.False(new SmtpEmailSender(Options.Create(new DigestOptions())).IsConfigured);
        Assert.True(new SmtpEmailSender(Options.Create(new DigestOptions { Smtp = { Host = "mail" } })).IsConfigured);

        var mime = SmtpEmailSender.ToMimeMessage(new EmailMessage("TEST@TEST.TEST", "Subject", "Body"), "notes@bluestaq.local");

        Assert.Equal(("TEST@TEST.TEST", "notes@bluestaq.local", "Subject"), (mime.To.ToString(), mime.From.ToString(), mime.Subject));
        Assert.Equal("Body", mime.TextBody);
    }
}
