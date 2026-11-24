using System.Security.Claims;
using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class AuthHelpersTests
{
    [Fact]
    public void Claims_extensions_read_ids()
    {
        var principal = TestPrincipals.For(userId: 42, sessionId: 7);
        Assert.Equal(42, principal.GetUserId());
        Assert.Equal(7, principal.GetSessionId());
    }

    [Fact]
    public void Claims_extensions_throw_for_anonymous_users()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.Throws<InvalidOperationException>(() => anonymous.GetUserId());
        Assert.Throws<InvalidOperationException>(() => anonymous.GetSessionId());
    }

    [Fact]
    public void Current_user_accessor_reflects_the_http_context()
    {
        var accessor = new HttpContextAccessor();
        var currentUser = new HttpContextCurrentUserAccessor(accessor);
        Assert.Null(currentUser.UserId);

        accessor.HttpContext = new DefaultHttpContext();
        Assert.Null(currentUser.UserId);

        accessor.HttpContext.User = TestPrincipals.For(userId: 9);
        Assert.Equal(9, currentUser.UserId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Session_cookie_is_secure_only_over_https(bool https)
    {
        var context = new DefaultHttpContext();
        context.Request.IsHttps = https;
        var options = new SessionAuthOptions { CookieName = "sid" };

        SessionCookie.Append(context, options, "tok", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith("sid=tok", cookie);
        Assert.Contains("path=/", cookie);
        Assert.Equal(https, cookie.Contains("secure"));
    }

    [Fact]
    public void Session_cookie_delete_expires_the_cookie()
    {
        var context = new DefaultHttpContext();
        SessionCookie.Delete(context, new SessionAuthOptions { CookieName = "sid" });
        Assert.Contains("expires=Thu, 01 Jan 1970", context.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public void Option_defaults_are_sensible()
    {
        Assert.Equal(TimeSpan.FromHours(8), new SessionAuthOptions().IdleTimeout);
        Assert.Equal("notes_session", new SessionAuthOptions().CookieName);
        Assert.Equal(10, new AuthRateLimitOptions().PermitLimit);
        Assert.Equal(TimeSpan.FromMinutes(1), new AuthRateLimitOptions().Window);
    }
}
