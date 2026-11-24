using System.Net.WebSockets;
using System.Text.Json;
using Bluestaq.Notes.Api.Alerts;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class AlertTests : IAsyncLifetime
{
    private readonly TestDb _db = new();
    private long _alice;
    private long _bob;
    private long _carol;
    private Note _note = null!;

    public async Task InitializeAsync()
    {
        _alice = (await _db.AddUserAsync("alice")).Id;
        _bob = (await _db.AddUserAsync("bob")).Id;
        _carol = (await _db.AddUserAsync("carol")).Id;
        _note = await _db.AddNoteAsync(_alice, "Plan");
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private async Task ShareAsync(long noteId, long withUser, long actor)
    {
        _db.CurrentUser.UserId = actor;
        await using var ctx = _db.CreateContext();
        ctx.NoteShares.Add(new NoteShare { NoteId = noteId, UserId = withUser, SharedAt = _db.Clock.GetUtcNow().UtcDateTime });
        await ctx.SaveChangesAsync();
    }

    private async Task EditAsync(long noteId, long actor, string title)
    {
        _db.CurrentUser.UserId = actor;
        await using var ctx = _db.CreateContext();
        var note = await ctx.Notes.SingleAsync(n => n.Id == noteId);
        note.Title = title;
        await ctx.SaveChangesAsync();
    }

    private async Task<List<NoteAlert>> AlertsAsync()
    {
        await using var ctx = _db.CreateContext();
        return await ctx.NoteAlerts.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
    }

    private AlertService Service(NotesDbContext ctx) =>
        new(ctx, _db.Clock, Options.Create(new AlertOptions()));

    [Fact]
    public async Task Sharing_alerts_only_the_new_recipient()
    {
        await ShareAsync(_note.Id, _bob, _alice);

        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal((_bob, _note.Id, NoteAlertKind.Shared, _alice), (alert.RecipientUserId, alert.NoteId, alert.Kind, alert.ActorUserId));
        Assert.Null(alert.DeliveredAt);
    }

    [Fact]
    public async Task Editing_a_shared_note_alerts_every_share_except_the_actor_and_repeats_collapse()
    {
        await ShareAsync(_note.Id, _bob, _alice);
        await ShareAsync(_note.Id, _carol, _alice);

        await EditAsync(_note.Id, _alice, "v2");
        _db.Clock.Advance(TimeSpan.FromMinutes(1));
        await EditAsync(_note.Id, _alice, "v3");

        var updates = (await AlertsAsync()).Where(a => a.Kind == NoteAlertKind.Updated).ToList();
        Assert.Equal([_bob, _carol], updates.Select(a => a.RecipientUserId).Order());
        Assert.All(updates, a => Assert.Equal(2, a.Occurrences));
        Assert.All(updates, a => Assert.True(a.LastOccurredAt > a.FirstOccurredAt));

        // An admin (or anyone) editing is the actor and isn't alerted, but the owner is not a share and isn't either.
        await EditAsync(_note.Id, _bob, "by bob");
        var bobUpdate = (await AlertsAsync()).Single(a => a.Kind == NoteAlertKind.Updated && a.RecipientUserId == _carol);
        Assert.Equal(_bob, bobUpdate.ActorUserId);
        Assert.DoesNotContain(await AlertsAsync(), a => a.RecipientUserId == _alice);
    }

    [Fact]
    public async Task Visibility_changes_and_unattributed_saves_raise_no_alerts()
    {
        await ShareAsync(_note.Id, _bob, _alice);
        await using (var ctx = _db.CreateContext())
        {
            _db.CurrentUser.UserId = _alice;
            (await ctx.Notes.SingleAsync()).Visibility = NoteVisibility.Public;
            await ctx.SaveChangesAsync();
        }

        Assert.Single(await AlertsAsync());

        _db.CurrentUser.UserId = null;
        await using (var ctx = _db.CreateContext())
        {
            ctx.NoteShares.Add(new NoteShare { NoteId = _note.Id, UserId = _carol });
            ctx.SaveChanges();
        }

        Assert.Single(await AlertsAsync());
    }

    [Fact]
    public async Task Resubmitting_identical_text_raises_no_update_alert()
    {
        await ShareAsync(_note.Id, _bob, _alice);
        _db.CurrentUser.UserId = _alice;
        await using (var ctx = _db.CreateContext())
        {
            var note = await ctx.Notes.SingleAsync();
            var entry = ctx.Entry(note);
            entry.Property(n => n.Title).IsModified = true;
            entry.Property(n => n.Content).IsModified = true;
            await ctx.SaveChangesAsync();
        }

        Assert.DoesNotContain(await AlertsAsync(), a => a.Kind == NoteAlertKind.Updated);
    }

    [Fact]
    public async Task Sync_save_queues_update_alerts_too()
    {
        await ShareAsync(_note.Id, _bob, _alice);
        _db.CurrentUser.UserId = _alice;
        await using var ctx = _db.CreateContext();
        (await ctx.Notes.SingleAsync()).Content = "sync";
        ctx.SaveChanges();

        Assert.Contains(await AlertsAsync(), a => a.Kind == NoteAlertKind.Updated && a.RecipientUserId == _bob);
    }

    [Fact]
    public async Task Pending_alerts_aggregate_per_note_and_push_at_most_once_per_interval()
    {
        await ShareAsync(_note.Id, _bob, _alice);
        _db.Clock.Advance(TimeSpan.FromMinutes(2));
        await EditAsync(_note.Id, _alice, "Plan v2");
        var other = await _db.AddNoteAsync(_carol, "Carol's");
        await ShareAsync(other.Id, _bob, _carol);

        await using var ctx = _db.CreateContext();
        var service = Service(ctx);
        var batch = await service.TakePendingAsync(_bob, default);

        Assert.NotNull(batch);
        Assert.Equal(3, batch.Alerts.Count);
        Assert.Equal("alerts", batch.Message.Type);
        Assert.Equal(2, batch.Message.Alerts.Count);
        var plan = batch.Message.Alerts.Single(a => a.NoteId == _note.Id);
        Assert.Equal(("Plan v2", "alice", "alice"), (plan.Title, plan.SharedBy, plan.UpdatedBy));
        Assert.Equal([NoteAlertKind.Shared, NoteAlertKind.Updated], plan.Events);
        Assert.Equal(2, plan.EventCount);
        Assert.Equal((await ctx.Notes.SingleAsync(n => n.Id == _note.Id)).UpdatedAt, plan.LastUpdatedAt);
        Assert.Equal("carol", batch.Message.Alerts.Single(a => a.NoteId == other.Id).SharedBy);

        await service.MarkDeliveredAsync(batch, default);

        // Within the interval: new activity is held back and aggregated.
        _db.Clock.Advance(TimeSpan.FromMinutes(10));
        await EditAsync(_note.Id, _alice, "Plan v3");
        _db.Clock.Advance(TimeSpan.FromMinutes(5));
        await EditAsync(_note.Id, _alice, "Plan v4");
        Assert.Null(await service.TakePendingAsync(_bob, default));

        _db.Clock.Advance(TimeSpan.FromMinutes(15));
        var next = await service.TakePendingAsync(_bob, default);
        var item = Assert.Single(next!.Message.Alerts);
        Assert.Equal(("Plan v4", 2), (item.Title, item.EventCount));
        Assert.Equal([NoteAlertKind.Updated], item.Events);

        // Delivered rows older than the interval are pruned.
        Assert.DoesNotContain(await AlertsAsync(), a => a.DeliveredAt != null);
    }

    [Fact]
    public async Task An_event_merged_after_the_batch_was_built_stays_pending()
    {
        await ShareAsync(_note.Id, _bob, _alice);
        await EditAsync(_note.Id, _alice, "v2");
        await using var ctx = _db.CreateContext();
        var service = Service(ctx);
        var batch = (await service.TakePendingAsync(_bob, default))!;

        await EditAsync(_note.Id, _alice, "v3");
        await service.MarkDeliveredAsync(batch, default);

        var alerts = await AlertsAsync();
        Assert.NotNull(alerts.Single(a => a.Kind == NoteAlertKind.Shared).DeliveredAt);
        var update = alerts.Single(a => a.Kind == NoteAlertKind.Updated);
        Assert.Equal((2, null), (update.Occurrences, update.DeliveredAt));
    }

    [Fact]
    public async Task Alerts_for_notes_the_user_can_no_longer_read_are_discarded()
    {
        await ShareAsync(_note.Id, _bob, _alice);
        await using var ctx = _db.CreateContext();
        await ctx.NoteShares.ExecuteDeleteAsync();

        Assert.Null(await Service(ctx).TakePendingAsync(_bob, default));
        Assert.Empty(await AlertsAsync());
        Assert.Null(await Service(ctx).TakePendingAsync(_carol, default));
    }

    [Fact]
    public async Task Dispatcher_pushes_to_live_sessions_and_closes_sockets_whose_session_ended()
    {
        long live, ended;
        await using (var ctx = _db.CreateContext())
        {
            var now = _db.Clock.GetUtcNow().UtcDateTime;
            var a = new Session { UserId = _bob, TokenHash = new string('a', 64), CreatedAt = now, LastSeenAt = now, ExpiresAt = now.AddHours(1) };
            var b = new Session { UserId = _bob, TokenHash = new string('b', 64), CreatedAt = now, LastSeenAt = now, ExpiresAt = now.AddMinutes(-1) };
            ctx.Sessions.AddRange(a, b);
            await ctx.SaveChangesAsync();
            (live, ended) = (a.Id, b.Id);
        }

        await ShareAsync(_note.Id, _bob, _alice);
        var services = new ServiceCollection()
            .AddScoped(_ => _db.CreateContext())
            .AddScoped<AlertService>()
            .AddSingleton<TimeProvider>(_db.Clock)
            .Configure<AlertOptions>(_ => { })
            .BuildServiceProvider();
        var connections = new AlertConnections();
        var liveSocket = new FakeSocket();
        var endedSocket = new FakeSocket();
        var brokenSocket = new FakeSocket { FailSends = true };
        connections.Add(_bob, live, liveSocket);
        connections.Add(_bob, ended, endedSocket);
        connections.Add(_bob, live, brokenSocket);
        connections.Add(_carol, live, new FakeSocket { CurrentState = WebSocketState.Closed });
        var dispatcher = new AlertDispatcher(
            services.GetRequiredService<IServiceScopeFactory>(), connections, Options.Create(new AlertOptions()),
            _db.Clock, NullLogger<AlertDispatcher>.Instance);

        await dispatcher.DispatchOnceAsync(default);

        var message = JsonDocument.Parse(Assert.Single(liveSocket.Sent)).RootElement;
        Assert.Equal("alerts", message.GetProperty("type").GetString());
        Assert.Equal("Plan", message.GetProperty("alerts")[0].GetProperty("title").GetString());
        Assert.Equal("Shared", message.GetProperty("alerts")[0].GetProperty("events")[0].GetString());
        Assert.Empty(endedSocket.Sent);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, endedSocket.ClosedWith);
        Assert.All(await AlertsAsync(), a => Assert.NotNull(a.DeliveredAt));
        Assert.Equal([liveSocket], connections.ByUser().Single(g => g.Key == _bob).Select(c => c.Socket));

        // Throttled: nothing more is sent on the next pass.
        await EditAsync(_note.Id, _alice, "v2");
        await dispatcher.DispatchOnceAsync(default);
        Assert.Single(liveSocket.Sent);

        dispatcher.Wake();
        dispatcher.Wake();
    }

    [Fact]
    public async Task Dispatcher_drops_a_socket_that_stops_accepting_sends_and_still_serves_others()
    {
        long sessionId;
        await using (var ctx = _db.CreateContext())
        {
            var now = _db.Clock.GetUtcNow().UtcDateTime;
            var session = new Session { UserId = _bob, TokenHash = new string('c', 64), CreatedAt = now, LastSeenAt = now, ExpiresAt = now.AddHours(1) };
            ctx.Sessions.Add(session);
            await ctx.SaveChangesAsync();
            sessionId = session.Id;
        }

        await ShareAsync(_note.Id, _bob, _alice);
        var services = new ServiceCollection()
            .AddScoped(_ => _db.CreateContext())
            .AddScoped<AlertService>()
            .AddSingleton<TimeProvider>(_db.Clock)
            .Configure<AlertOptions>(_ => { })
            .BuildServiceProvider();
        var connections = new AlertConnections();
        var stalled = new FakeSocket { HangSends = true };
        var healthy = new FakeSocket();
        connections.Add(_bob, sessionId, stalled);
        connections.Add(_bob, sessionId, healthy);
        var dispatcher = new AlertDispatcher(
            services.GetRequiredService<IServiceScopeFactory>(), connections,
            Options.Create(new AlertOptions { SendTimeout = TimeSpan.FromMilliseconds(50) }), _db.Clock, NullLogger<AlertDispatcher>.Instance);

        await dispatcher.DispatchOnceAsync(default).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WebSocketState.Aborted, stalled.State);
        Assert.Single(healthy.Sent);
        Assert.Equal([healthy], connections.ByUser().Single().Select(c => c.Socket));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("https://api.example.com", null, true)]
    [InlineData("https://API.example.com/", null, true)]
    [InlineData("https://app.example.com", null, true)]
    [InlineData("https://evil.example.com", null, false)]
    [InlineData("not a uri", null, false)]
    [InlineData("https://evil.example.com", "Bearer token", true)]
    public void Cookie_authenticated_sockets_must_come_from_an_allowed_origin(string? origin, string? authorization, bool allowed)
    {
        var request = new Microsoft.AspNetCore.Http.DefaultHttpContext().Request;
        request.Host = new Microsoft.AspNetCore.Http.HostString("api.example.com");
        if (origin is not null)
        {
            request.Headers.Origin = origin;
        }

        if (authorization is not null)
        {
            request.Headers.Authorization = authorization;
        }

        var options = new AlertOptions { AllowedOrigins = ["https://app.example.com/"] };
        Assert.Equal(allowed, AlertEndpoint.IsOriginAllowed(request, options));
    }

    private sealed class FakeSocket : WebSocket
    {
        public List<byte[]> Sent { get; } = [];
        public bool FailSends { get; init; }
        public bool HangSends { get; init; }
        public WebSocketState CurrentState { get; set; } = WebSocketState.Open;
        public WebSocketCloseStatus? ClosedWith { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => ClosedWith;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => CurrentState;
        public override string? SubProtocol => null;

        public override void Abort() => CurrentState = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            ClosedWith = closeStatus;
            CurrentState = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (FailSends)
            {
                throw new WebSocketException("gone");
            }

            if (HangSends)
            {
                return Task.Delay(Timeout.Infinite, cancellationToken);
            }

            Sent.Add(buffer.ToArray());
            return Task.CompletedTask;
        }
    }
}
