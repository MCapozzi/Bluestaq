using System.Security.Claims;
using System.Text.Encodings.Web;
using Bluestaq.Notes.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class SessionAuthenticationHandlerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SessionAuthOptions _options = new();

    public void Dispose() => _db.Dispose();

    private sealed class SchemeOptionsMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private async Task<string> LoginAsync()
    {
        var user = await _db.AddUserAsync();
        var ctx = _db.CreateContext();
        var service = new SessionService(ctx, Options.Create(_options), _db.Clock);
        return (await service.CreateAsync(await ctx.Users.SingleAsync(u => u.Id == user.Id), default)).Token;
    }

    private async Task<(SessionAuthenticationHandler Handler, HttpContext Context)> CreateHandlerAsync(Action<HttpRequest> configure)
    {
        var context = new DefaultHttpContext();
        configure(context.Request);
        var handler = new SessionAuthenticationHandler(
            new SchemeOptionsMonitor(), NullLoggerFactory.Instance, UrlEncoder.Default,
            new SessionService(_db.CreateContext(), Options.Create(_options), _db.Clock), Options.Create(_options));
        await handler.InitializeAsync(
            new AuthenticationScheme(SessionAuthenticationDefaults.Scheme, null, typeof(SessionAuthenticationHandler)), context);
        return (handler, context);
    }

    [Fact]
    public async Task No_token_yields_no_result()
    {
        var (handler, _) = await CreateHandlerAsync(_ => { });
        Assert.True((await handler.AuthenticateAsync()).None);
    }

    [Fact]
    public async Task Valid_bearer_token_authenticates_with_user_and_session_claims()
    {
        var token = await LoginAsync();
        var (handler, context) = await CreateHandlerAsync(r => r.Headers.Authorization = $"bearer {token}");

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        var principal = result.Principal!;
        Assert.Equal("alice", principal.FindFirstValue(ClaimTypes.Name));
        Assert.True(principal.GetUserId() > 0);
        Assert.True(principal.GetSessionId() > 0);
        Assert.Empty(context.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task Invalid_token_fails()
    {
        var (handler, _) = await CreateHandlerAsync(r => r.Headers.Authorization = "Bearer nope");
        var result = await handler.AuthenticateAsync();
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Cookie_token_is_reissued_when_the_session_slides()
    {
        var token = await LoginAsync();
        _db.Clock.Advance(TimeSpan.FromMinutes(5));
        var (handler, context) = await CreateHandlerAsync(r => r.Headers.Cookie = $"{_options.CookieName}={token}");

        Assert.True((await handler.AuthenticateAsync()).Succeeded);

        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith($"{_options.CookieName}={token}", cookie);
        var expected = (TestDb.Start + TimeSpan.FromMinutes(5) + _options.IdleTimeout).ToString("R");
        Assert.Contains(expected, cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cookie_is_not_rewritten_when_the_session_did_not_slide()
    {
        var token = await LoginAsync();
        var (handler, context) = await CreateHandlerAsync(r => r.Headers.Cookie = $"{_options.CookieName}={token}");

        Assert.True((await handler.AuthenticateAsync()).Succeeded);
        Assert.Empty(context.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task Bearer_token_never_sets_a_cookie_even_when_sliding()
    {
        var token = await LoginAsync();
        _db.Clock.Advance(TimeSpan.FromMinutes(5));
        var (handler, context) = await CreateHandlerAsync(r => r.Headers.Authorization = $"Bearer {token}");

        Assert.True((await handler.AuthenticateAsync()).Succeeded);
        Assert.Empty(context.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task Challenge_returns_401_with_bearer_header()
    {
        var (handler, context) = await CreateHandlerAsync(_ => { });

        await handler.ChallengeAsync(null);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("Bearer", context.Response.Headers.WWWAuthenticate.ToString());
    }
}
