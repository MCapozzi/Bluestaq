namespace Bluestaq.Notes.Api.Auth;

public static class SessionCookie
{
    /// <summary>Sets the session cookie, replacing any session cookie already set on this response.</summary>
    public static void Append(HttpContext context, SessionAuthOptions options, string token, DateTime expiresAt)
    {
        ClearPending(context, options.CookieName);
        context.Response.Cookies.Append(options.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Expires = expiresAt,
            Path = "/",
        });
    }

    public static void Delete(HttpContext context, SessionAuthOptions options)
    {
        ClearPending(context, options.CookieName);
        context.Response.Cookies.Delete(options.CookieName, new CookieOptions { Path = "/" });
    }

    /// <summary>Drops session cookie values already queued on the response (e.g. a sliding refresh of the old session).</summary>
    private static void ClearPending(HttpContext context, string name)
    {
        var headers = context.Response.Headers;
        if (headers.SetCookie.Count > 0)
        {
            headers.SetCookie = headers.SetCookie.Where(c => c is not null && !c.StartsWith($"{name}=", StringComparison.Ordinal)).ToArray();
        }
    }
}
