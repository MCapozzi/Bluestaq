using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.Alerts;

/// <summary>Pushes a message right away (no aggregation or throttling) to every administrator alert socket on this instance.</summary>
public sealed class AdminAlertSender(AlertConnections connections, IOptions<AlertOptions> options, ILogger<AdminAlertSender> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <returns>The number of sockets the message was sent to.</returns>
    public async Task<int> SendAsync<T>(T message, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var sent = 0;
        foreach (var connection in connections.Administrators())
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Value.SendTimeout);
            try
            {
                await connection.SendAsync(payload, timeout.Token);
                sent++;
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Could not push an admin alert to user {UserId}.", connection.UserId);
            }
        }

        return sent;
    }
}
