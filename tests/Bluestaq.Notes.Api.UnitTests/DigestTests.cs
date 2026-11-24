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

    private DigestService Service(NotesDbContext ctx) => new(ctx, _email, _db.Clock, NullLogger<DigestService>.Instance);

    private async Task<int> SendDueAsync()
    {
        await using var ctx = _db.CreateContext();
        return await Service(ctx).SendDueAsync(CancellationToken.None);
    }

    private async Task ShareAsync(long noteId, long withUser, long sharedBy)
    {
        await using var ctx = _db.CreateContext();
        ctx.NoteShares.Add(new NoteShare { NoteId = noteId, UserId = withUser, SharedAt = _db.Clock.GetUtcNow().UtcDateTime, SharedByUserId = sharedBy });
        await ctx.SaveChangesAsync();
    }

    private async Task EditAsync(long noteId, long actor, string title)
    {
        _db.CurrentUser.UserId = actor;
        await using var ctx = _db.CreateContext();
        (await ctx.Notes.SingleAsync(n => n.Id == noteId)).Title = title;
        await ctx.SaveChangesAsync();
    }

    private async Task SetFrequencyAsync(long userId, DigestFrequency frequency)
    {
        await using var ctx = _db.CreateContext();
        (await ctx.Users.SingleAsync(u => u.Id == userId)).DigestFrequency = frequency;
        await ctx.SaveChangesAsync();
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

        Assert.Equal(0, await SendDueAsync());
        Assert.Empty(_email.Sent);
        Assert.Equal(DigestFrequency.None, (await UserAsync(_bob)).DigestFrequency);
    }

    [Fact]
    public async Task First_digest_lists_every_shared_note_and_goes_to_the_test_address()
    {
        await ShareAsync(_plan.Id, _bob, _alice);
        _db.Clock.Advance(TimeSpan.FromHours(1));
        await EditAsync(_plan.Id, _alice, "Plan v2");
        await SetFrequencyAsync(_bob, DigestFrequency.Daily);

        Assert.Equal(1, await SendDueAsync());

        var email = Assert.Single(_email.Sent);
        Assert.Equal(("TEST@TEST.TEST", "Your daily notes digest (bob)"), (email.To, email.Subject));
        Assert.Contains("first daily notes digest", email.TextBody);
        Assert.Contains("Shared with you (1):", email.TextBody);
        Assert.Contains("Changed since they were shared with you (1):", email.TextBody);
        Assert.Contains("\"Plan v2\"", email.TextBody);
        Assert.Contains("shared by alice", email.TextBody);
        Assert.Equal(_db.Clock.GetUtcNow().UtcDateTime, (await UserAsync(_bob)).DigestLastSentAt);
    }

    [Fact]
    public async Task Later_digests_only_cover_the_period_and_wait_for_it_to_elapse()
    {
        await ShareAsync(_plan.Id, _bob, _alice);
        await SetFrequencyAsync(_bob, DigestFrequency.Weekly);
        await SendDueAsync();
        _email.Sent.Clear();

        var other = await _db.AddNoteAsync(_alice, "Other");
        _db.Clock.Advance(TimeSpan.FromDays(1));
        await ShareAsync(other.Id, _bob, _alice);
        await EditAsync(_plan.Id, _alice, "Plan v2");

        Assert.Equal(0, await SendDueAsync());

        _db.Clock.Advance(TimeSpan.FromDays(6));
        Assert.Equal(1, await SendDueAsync());

        var body = Assert.Single(_email.Sent).TextBody;
        Assert.Contains("weekly notes digest for", body);
        Assert.Contains("Shared with you (1):", body);
        Assert.Contains("\"Other\"", body);
        Assert.Contains("Changed since they were shared with you (1):", body);
        Assert.Contains("\"Plan v2\"", body);
    }

    [Fact]
    public async Task Nothing_new_sends_nothing_but_advances_the_window()
    {
        await SetFrequencyAsync(_bob, DigestFrequency.Daily);

        Assert.Equal(0, await SendDueAsync());

        Assert.Empty(_email.Sent);
        Assert.NotNull((await UserAsync(_bob)).DigestLastSentAt);
    }

    [Fact]
    public async Task Own_edits_edits_before_sharing_and_revoked_shares_are_left_out()
    {
        await EditAsync(_plan.Id, _alice, "Edited before sharing");
        _db.Clock.Advance(TimeSpan.FromMinutes(1));
        await ShareAsync(_plan.Id, _bob, _alice);
        var bobsNote = await _db.AddNoteAsync(_bob, "Mine");

        await using var ctx = _db.CreateContext();
        var digest = await Service(ctx).BuildAsync(_bob, "bob", DigestFrequency.Daily, null, _db.Clock.GetUtcNow().UtcDateTime, default);
        Assert.Single(digest.NewlyShared);
        Assert.Empty(digest.Updated);

        await ctx.NoteShares.Where(s => s.UserId == _bob).ExecuteDeleteAsync();
        digest = await Service(ctx).BuildAsync(_bob, "bob", DigestFrequency.Daily, null, _db.Clock.GetUtcNow().UtcDateTime, default);
        Assert.True(digest.IsEmpty);
        Assert.NotEqual(0, bobsNote.Id);
    }

    [Fact]
    public async Task A_failed_send_is_retried_on_the_next_pass()
    {
        await ShareAsync(_plan.Id, _bob, _alice);
        await SetFrequencyAsync(_bob, DigestFrequency.Daily);
        _email.FailWith = new InvalidOperationException("smtp down");

        Assert.Equal(0, await SendDueAsync());
        Assert.Null((await UserAsync(_bob)).DigestLastSentAt);

        _email.FailWith = null;
        Assert.Equal(1, await SendDueAsync());
    }

    [Fact]
    public async Task An_overdue_digest_is_sent_once_and_restarts_the_period()
    {
        await ShareAsync(_plan.Id, _bob, _alice);
        await SetFrequencyAsync(_bob, DigestFrequency.Daily);
        await using (var ctx = _db.CreateContext())
        {
            await ctx.Users.Where(u => u.Id == _bob).ExecuteUpdateAsync(s => s.SetProperty(u => u.DigestLastSentAt, _db.Clock.GetUtcNow().UtcDateTime.AddDays(-2)));
        }

        await EditAsync(_plan.Id, _alice, "Plan v2");

        Assert.Equal(1, await SendDueAsync());
        Assert.Equal(0, await SendDueAsync());
        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task Controller_defaults_to_opted_out_and_opting_in_and_out_updates_settings()
    {
        await using var ctx = _db.CreateContext();

        var settings = Assert.IsType<DigestSettingsResponse>(Assert.IsType<OkObjectResult>(await Controller(ctx, _bob).Get(default)).Value);
        Assert.Equal((DigestFrequency.None, (DateTime?)null, "TEST@TEST.TEST"), (settings.Frequency, settings.NextDigestAt, settings.Recipient));

        settings = Assert.IsType<DigestSettingsResponse>(Assert.IsType<OkObjectResult>(
            await Controller(ctx, _bob).Update(new UpdateDigestRequest(DigestFrequency.Weekly), default)).Value);
        Assert.Equal((DigestFrequency.Weekly, _db.Clock.GetUtcNow().UtcDateTime), (settings.Frequency, settings.NextDigestAt!.Value));

        await ShareAsync(_plan.Id, _bob, _alice);
        await SendDueAsync();
        ctx.ChangeTracker.Clear();
        settings = Assert.IsType<DigestSettingsResponse>(Assert.IsType<OkObjectResult>(await Controller(ctx, _bob).Get(default)).Value);
        Assert.Equal(settings.LastSentAt!.Value.AddDays(7), settings.NextDigestAt);

        settings = Assert.IsType<DigestSettingsResponse>(Assert.IsType<OkObjectResult>(
            await Controller(ctx, _bob).Update(new UpdateDigestRequest(DigestFrequency.None), default)).Value);
        Assert.Equal((DigestFrequency.None, (DateTime?)null), (settings.Frequency, settings.LastSentAt));
    }

    [Fact]
    public async Task Controller_preview_shows_the_next_digest_without_sending()
    {
        await ShareAsync(_plan.Id, _bob, _alice);
        await using var ctx = _db.CreateContext();

        var digest = Assert.IsType<NoteDigest>(Assert.IsType<OkObjectResult>(await Controller(ctx, _bob).Preview(default)).Value);

        Assert.Equal(("bob", "Plan", "alice"), (digest.Username, Assert.Single(digest.NewlyShared).Title, digest.NewlyShared[0].SharedBy));
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public void Email_text_strips_control_characters_from_titles()
    {
        var at = TestDb.Start.UtcDateTime;
        var email = DigestService.ToEmail(new NoteDigest("bob", DigestFrequency.Weekly, at, at.AddDays(7),
            [new DigestNote(1, "Line1\r\nBcc: x", "alice", at, "alice", at)], []));

        Assert.Contains("\"Line1  Bcc: x\"", email.TextBody);
        Assert.Contains("2026-01-01 12:00 UTC to 2026-01-08 12:00 UTC", email.TextBody);
        Assert.Equal("Your weekly notes digest (bob)", email.Subject);
    }

    [Fact]
    public void Period_of_none_is_null()
    {
        Assert.Null(DigestService.PeriodOf(DigestFrequency.None));
        Assert.Equal(TimeSpan.FromDays(1), DigestService.PeriodOf(DigestFrequency.Daily));
    }

    [Fact]
    public async Task Dispatcher_sends_due_digests_and_stays_idle_without_a_mail_server()
    {
        await ShareAsync(_plan.Id, _bob, _alice);
        await SetFrequencyAsync(_bob, DigestFrequency.Daily);
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(_db.Clock)
            .AddSingleton<IEmailSender>(_email)
            .AddLogging()
            .AddScoped(_ => _db.CreateContext())
            .AddScoped<DigestService>()
            .BuildServiceProvider();
        var dispatcher = new DigestDispatcher(services.GetRequiredService<IServiceScopeFactory>(), _email,
            Options.Create(new DigestOptions()), _db.Clock, NullLogger<DigestDispatcher>.Instance);

        Assert.Equal(1, await dispatcher.SendOnceAsync(default));

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
