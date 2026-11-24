using System.Security.Claims;

namespace Bluestaq.Notes.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    public static long GetUserId(this ClaimsPrincipal user) =>
        long.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("User is not authenticated."));

    public static long GetSessionId(this ClaimsPrincipal user) =>
        long.Parse(user.FindFirstValue(SessionAuthenticationDefaults.SessionIdClaim)
            ?? throw new InvalidOperationException("User is not authenticated."));
}
