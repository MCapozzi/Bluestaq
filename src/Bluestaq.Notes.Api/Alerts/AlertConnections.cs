using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Bluestaq.Notes.Api.Alerts;

public sealed record AlertConnection(Guid Id, long UserId, long SessionId, WebSocket Socket);

/// <summary>Open alert WebSockets on this instance, by user.</summary>
public sealed class AlertConnections
{
    private readonly ConcurrentDictionary<Guid, AlertConnection> _connections = new();

    public AlertConnection Add(long userId, long sessionId, WebSocket socket)
    {
        var connection = new AlertConnection(Guid.NewGuid(), userId, sessionId, socket);
        _connections[connection.Id] = connection;
        return connection;
    }

    public void Remove(AlertConnection connection) => _connections.TryRemove(connection.Id, out _);

    public IReadOnlyList<IGrouping<long, AlertConnection>> ByUser() =>
        _connections.Values.GroupBy(c => c.UserId).ToList();
}
