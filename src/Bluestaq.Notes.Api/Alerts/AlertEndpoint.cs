using System.Net.WebSockets;
using Bluestaq.Notes.Api.Auth;

namespace Bluestaq.Notes.Api.Alerts;

public static class AlertEndpoint
{
    public const string Path = "/ws/alerts";

    /// <summary>Accepts an authenticated WebSocket and keeps it registered for alert pushes until the client closes it.</summary>
    public static async Task HandleAsync(HttpContext context, AlertConnections connections, AlertDispatcher dispatcher)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Expected a WebSocket request.")
                .ExecuteAsync(context);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = connections.Add(context.User.GetUserId(), context.User.GetSessionId(), socket);
        dispatcher.Wake();
        try
        {
            // Clients don't send anything meaningful; read only to observe the close handshake.
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Client went away.
        }
        finally
        {
            connections.Remove(connection);
        }
    }
}
