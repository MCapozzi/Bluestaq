using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.Auth;

/// <summary>
/// Authenticates requests by looking up the session token (from the session cookie or an
/// <c>Authorization: Bearer</c> header) in the sessions table.
/// </summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionService sessions,
    IOptions<SessionAuthOptions> sessionOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var (token, fromCookie) = ReadToken();
        if (string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        SessionValidationResult? result;
        try
        {
            result = await sessions.ValidateAsync(token, Context.RequestAborted);
        }
        catch (DbException ex) when (Context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            // Anonymous endpoints (e.g. register) still run, and handle the database failure themselves.
            Logger.LogWarning(ex, "Could not validate the session for an anonymous endpoint.");
            return AuthenticateResult.NoResult();
        }

        if (result?.Session.User is not { } user)
        {
            return AuthenticateResult.Fail("Invalid or expired session.");
        }

        var session = result.Session;
        if (fromCookie && result.Extended)
        {
            // Keep the browser cookie alive as long as the server-side session.
            SessionCookie.Append(Context, sessionOptions.Value, token, session.ExpiresAt);
        }

        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, session.UserId.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(SessionAuthenticationDefaults.SessionIdClaim, session.Id.ToString()),
            new(ClaimTypes.Role, user.Role.ToString()),
        ];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return base.HandleChallengeAsync(properties);
    }

    private (string? Token, bool FromCookie) ReadToken()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return (header["Bearer ".Length..].Trim(), false);
        }

        return (Request.Cookies[sessionOptions.Value.CookieName], true);
    }
}
