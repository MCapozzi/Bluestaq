using System.Net.WebSockets;
using System.Text.Json;
using Bluestaq.Notes.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.Alerts;

/// <summary>
/// Periodically pushes due alert batches to connected users. Runs on a single loop, so sends to a socket never overlap.
/// Sockets whose session has ended (logout or expiry) are closed instead of receiving alerts.
/// </summary>
public sealed class AlertDispatcher(
    IServiceScopeFactory scopes,
    AlertConnections connections,
    IOptions<AlertOptions> options,
    TimeProvider clock,
    ILogger<AlertDispatcher> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Requests an immediate dispatch pass (e.g. when a user connects).</summary>
    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pass is already pending.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(stoppingToken);
                await _wake.WaitAsync(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Alert dispatch pass failed.");
                await Task.Delay(options.Value.PollInterval, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
    }

    public async Task DispatchOnceAsync(CancellationToken ct)
    {
        foreach (var userConnections in connections.ByUser())
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NotesDbContext>();
            var now = clock.GetUtcNow().UtcDateTime;

            var sessionIds = userConnections.Select(c => c.SessionId).Distinct().ToList();
            var liveSessions = await db.Sessions.AsNoTracking()
                .Where(s => sessionIds.Contains(s.Id) && s.ExpiresAt > now)
                .Select(s => s.Id)
                .ToListAsync(ct);

            var open = new List<AlertConnection>();
            foreach (var connection in userConnections)
            {
                if (!liveSessions.Contains(connection.SessionId))
                {
                    connections.Remove(connection);
                    await CloseQuietlyAsync(connection.Socket, ct);
                }
                else if (connection.Socket.State == WebSocketState.Open)
                {
                    open.Add(connection);
                }
            }

            if (open.Count == 0)
            {
                continue;
            }

            // TODO: Add a Redis backplane (pub/sub) for multi-replica deployments. This replica only pushes to sockets
            // connected to it, so with several replicas and no sticky sessions, a batch can be taken here while the
            // user's sockets are on another replica, and that replica's sockets miss the push. Adding the backplane is
            // outside the scope of the current project, but it would be completed for a production release of this system.
            var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();
            var batch = await alerts.TakePendingAsync(userConnections.Key, ct);
            if (batch is null)
            {
                continue;
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(batch.Message, Json);
            var sent = 0;
            foreach (var connection in open)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(options.Value.SendTimeout);
                try
                {
                    await connection.Socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
                    sent++;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // A client that stops reading must not stall pushes to everyone else.
                    connections.Remove(connection);
                    connection.Socket.Abort();
                }
                catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
                {
                    connections.Remove(connection);
                }
            }

            if (sent > 0)
            {
                await alerts.MarkDeliveredAsync(batch, ct);
            }
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket, CancellationToken ct)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Session ended.", ct);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Already gone.
        }
    }
}
