using System.Net.WebSockets;
using Bluestaq.Notes.Api.Auth;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.Alerts;

public static class AlertEndpoint
{
    public const string Path = "/ws/alerts";

    /// <summary>Accepts an authenticated WebSocket and keeps it registered for alert pushes until the client closes it.</summary>
    public static async Task HandleAsync(
        HttpContext context, AlertConnections connections, AlertDispatcher dispatcher, IOptions<AlertOptions> options)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Expected a WebSocket request.")
                .ExecuteAsync(context);
            return;
        }

        if (!IsOriginAllowed(context.Request, options.Value))
        {
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Origin not allowed.").ExecuteAsync(context);
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

    /// <summary>
    /// Browsers attach cookies to WebSocket handshakes from any page on the same site, so a cookie-authenticated
    /// handshake must come from the API's own origin or a configured one. Requests with an Authorization header
    /// can't be forged by a browser page and are always allowed, as are non-browser clients that send no Origin.
    /// </summary>
    public static bool IsOriginAllowed(HttpRequest request, AlertOptions options)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin) || request.Headers.Authorization.Count > 0)
        {
            return true;
        }

        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return options.AllowedOrigins.Any(o => string.Equals(o.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }
}
