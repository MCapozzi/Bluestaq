using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class AlertsTests(NotesApiFactory factory)
{
    private async Task<WebSocket> ConnectAsync(string token)
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers.Authorization = $"Bearer {token}";
        return await client.ConnectAsync(new Uri(factory.Server.BaseAddress, "ws/alerts"), CancellationToken.None);
    }

    private static async Task<(WebSocketMessageType Type, string Text)> ReceiveAsync(WebSocket socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cts.Token);
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return (result.MessageType, Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task Shared_user_gets_one_aggregated_push_and_later_updates_wait_for_the_interval()
    {
        var (alice, aliceSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (_, bobSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        using var socket = await ConnectAsync(bobSession.Token);

        var note = (await (await alice.PostAsJsonAsync("/api/notes", new NoteRequest("Roadmap", "q1"))).Content.ReadFromJsonAsync<NoteResponse>())!;
        (await alice.PostAsJsonAsync($"/api/notes/{note.Id}/shares", new ShareNoteRequest(bobSession.User.Username))).EnsureSuccessStatusCode();

        var (type, text) = await ReceiveAsync(socket, TimeSpan.FromSeconds(15));
        Assert.Equal(WebSocketMessageType.Text, type);
        var message = JsonDocument.Parse(text).RootElement;
        Assert.Equal("alerts", message.GetProperty("type").GetString());
        var alert = Assert.Single(message.GetProperty("alerts").EnumerateArray());
        Assert.Equal(note.Id, alert.GetProperty("noteId").GetInt64());
        Assert.Equal("Roadmap", alert.GetProperty("title").GetString());
        Assert.Equal(aliceSession.User.Username, alert.GetProperty("sharedBy").GetString());
        Assert.Equal(note.UpdatedAt, alert.GetProperty("lastUpdatedAt").GetDateTime().ToUniversalTime());
        Assert.Equal("Shared", alert.GetProperty("events")[0].GetString());

        (await alice.PutAsJsonAsync($"/api/notes/{note.Id}", new NoteRequest("Roadmap v2", "q2"))).EnsureSuccessStatusCode();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveAsync(socket, TimeSpan.FromSeconds(2)));

        await using var db = factory.CreateDbContext();
        var held = await db.NoteAlerts.SingleAsync(a => a.RecipientUserId == bobSession.User.Id && a.DeliveredAt == null);
        Assert.Equal((note.Id, NoteAlertKind.Updated, aliceSession.User.Id), (held.NoteId, held.Kind, held.ActorUserId));
    }

    [Fact]
    public async Task Socket_requires_a_session_and_is_closed_after_logout()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync("not-a-real-token"));

        var (bob, bobSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        using var socket = await ConnectAsync(bobSession.Token);
        (await bob.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();

        var (type, _) = await ReceiveAsync(socket, TimeSpan.FromSeconds(15));
        Assert.Equal(WebSocketMessageType.Close, type);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }

    [Fact]
    public async Task Plain_http_request_to_the_socket_endpoint_is_rejected()
    {
        var (client, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.GetAsync("/ws/alerts")).StatusCode);
    }
}
