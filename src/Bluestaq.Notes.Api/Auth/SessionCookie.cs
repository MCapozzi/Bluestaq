namespace Bluestaq.Notes.Api.Auth;

public static class SessionCookie
{
    public static void Append(HttpContext context, SessionAuthOptions options, string token, DateTime expiresAt) =>
        context.Response.Cookies.Append(options.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Expires = expiresAt,
            Path = "/",
        });

    public static void Delete(HttpContext context, SessionAuthOptions options) =>
        context.Response.Cookies.Delete(options.CookieName, new CookieOptions { Path = "/" });
}
