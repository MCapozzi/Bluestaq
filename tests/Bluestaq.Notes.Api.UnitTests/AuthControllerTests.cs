using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Controllers;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class AuthControllerTests : IAsyncLifetime
{
    private const string Password = "correct-horse-battery";
    private readonly TestDb _db = new();
    private readonly SessionAuthOptions _options = new();
    private NotesDbContext _ctx = null!;

    public Task InitializeAsync()
    {
        _ctx = _db.CreateContext();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        _db.Dispose();
    }

    private AuthController Controller(IPasswordHasher<User>? hasher = null, System.Security.Claims.ClaimsPrincipal? user = null) =>
        new(_ctx, new SessionService(_ctx, Options.Create(_options), _db.Clock), hasher ?? new PasswordHasher<User>(), Options.Create(_options), _db.Clock)
        {
            ControllerContext = TestPrincipals.ControllerContext(user ?? new System.Security.Claims.ClaimsPrincipal()),
        };

    private async Task<UserResponse> RegisterAsync(string username = "Alice")
    {
        var created = Assert.IsType<CreatedAtActionResult>(await Controller().Register(new RegisterRequest(username, Password), default));
        return Assert.IsType<UserResponse>(created.Value);
    }

    [Fact]
    public async Task Register_normalizes_username_and_hashes_password()
    {
        var user = await RegisterAsync("  Alice ");

        Assert.Equal("alice", user.Username);
        var stored = await _ctx.Users.AsNoTracking().SingleAsync();
        Assert.NotEqual(Password, stored.PasswordHash);
        Assert.Equal(TestDb.Start.UtcDateTime, stored.CreatedAt);
    }

    [Fact]
    public async Task Register_duplicate_username_is_409()
    {
        await RegisterAsync("alice");

        var result = Assert.IsType<ObjectResult>(await Controller().Register(new RegisterRequest("ALICE", Password), default));
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
    }

    [Fact]
    public async Task Login_returns_token_and_sets_http_only_cookie()
    {
        await RegisterAsync();
        var controller = Controller();

        var ok = Assert.IsType<OkObjectResult>(await controller.Login(new LoginRequest("ALICE", Password), default));

        var session = Assert.IsType<SessionResponse>(ok.Value);
        Assert.Equal("alice", session.User.Username);
        Assert.Equal(TestDb.Start.UtcDateTime + _options.IdleTimeout, session.ExpiresAt);
        var cookie = controller.HttpContext.Response.Headers.SetCookie.ToString();
        Assert.StartsWith($"{_options.CookieName}={session.Token}", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
    }

    [Theory]
    [InlineData("alice", "wrong-password")]
    [InlineData("nobody", Password)]
    public async Task Login_with_bad_credentials_is_401(string username, string password)
    {
        await RegisterAsync();

        var result = Assert.IsType<ObjectResult>(await Controller().Login(new LoginRequest(username, password), default));
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task Login_upgrades_outdated_password_hashes()
    {
        var legacy = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2 }));
        var user = new User { Username = "alice", PasswordHash = "", CreatedAt = TestDb.Start.UtcDateTime };
        user.PasswordHash = legacy.HashPassword(user, Password);
        _ctx.Users.Add(user);
        await _ctx.SaveChangesAsync();
        var oldHash = user.PasswordHash;

        Assert.IsType<OkObjectResult>(await Controller().Login(new LoginRequest("alice", Password), default));

        var stored = await _ctx.Users.AsNoTracking().SingleAsync();
        Assert.NotEqual(oldHash, stored.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<User>().VerifyHashedPassword(stored, stored.PasswordHash, Password));
    }

    [Fact]
    public async Task Me_returns_the_current_user()
    {
        var registered = await RegisterAsync();

        var ok = Assert.IsType<OkObjectResult>(await Controller(user: TestPrincipals.For(registered.Id)).Me(default));
        Assert.Equal(registered, ok.Value);
    }

    [Fact]
    public async Task Logout_revokes_session_and_clears_cookie()
    {
        var registered = await RegisterAsync();
        var session = (SessionResponse)((OkObjectResult)await Controller().Login(new LoginRequest("alice", Password), default)).Value!;
        var sessionId = (await _ctx.Sessions.AsNoTracking().SingleAsync()).Id;
        var controller = Controller(user: TestPrincipals.For(registered.Id, sessionId));

        Assert.IsType<NoContentResult>(await controller.Logout(default));

        Assert.False(await _ctx.Sessions.AnyAsync());
        Assert.Contains($"{_options.CookieName}=;", controller.HttpContext.Response.Headers.SetCookie.ToString());
        Assert.Null(await new SessionService(_db.CreateContext(), Options.Create(_options), _db.Clock).ValidateAsync(session.Token, default));
    }
}
