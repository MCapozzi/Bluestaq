using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Bluestaq.Notes.Api.Alerts;

public sealed record AlertConnection(Guid Id, long UserId, long SessionId, WebSocket Socket, bool IsAdministrator = false)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Sends one text message. WebSockets allow one send at a time, and both alert dispatch and admin alerts send.</summary>
    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            await Socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}

/// <summary>Open alert WebSockets on this instance, by user.</summary>
public sealed class AlertConnections
{
    private readonly ConcurrentDictionary<Guid, AlertConnection> _connections = new();

    public AlertConnection Add(long userId, long sessionId, WebSocket socket, bool isAdministrator = false)
    {
        var connection = new AlertConnection(Guid.NewGuid(), userId, sessionId, socket, isAdministrator);
        _connections[connection.Id] = connection;
        return connection;
    }

    public void Remove(AlertConnection connection) => _connections.TryRemove(connection.Id, out _);

    public IReadOnlyList<AlertConnection> Administrators() =>
        _connections.Values.Where(c => c.IsAdministrator && c.Socket.State == WebSocketState.Open).ToList();

    public IReadOnlyList<IGrouping<long, AlertConnection>> ByUser() =>
        _connections.Values.GroupBy(c => c.UserId).ToList();
}
