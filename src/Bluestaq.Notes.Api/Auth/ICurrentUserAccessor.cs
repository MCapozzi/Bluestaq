namespace Bluestaq.Notes.Api.Auth;

public interface ICurrentUserAccessor
{
    /// <summary>The authenticated user's ID, or null when there is no authenticated user.</summary>
    long? UserId { get; }
}

public sealed class HttpContextCurrentUserAccessor(IHttpContextAccessor httpContextAccessor) : ICurrentUserAccessor
{
    public long? UserId =>
        httpContextAccessor.HttpContext?.User is { Identity.IsAuthenticated: true } user ? user.GetUserId() : null;
}
