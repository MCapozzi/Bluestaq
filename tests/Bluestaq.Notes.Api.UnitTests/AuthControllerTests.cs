using Bluestaq.Notes.Api.Alerts;
using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Controllers;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class AuthControllerTests : IAsyncLifetime
{
    private const string Password = "correct-horse-battery";
    private readonly TestDb _db = new();
    private readonly SessionAuthOptions _options = new();
    private readonly RegistrationFailures _failures = new(new MemoryCache(new MemoryCacheOptions()), Options.Create(new RegistrationOptions()));
    private readonly AlertConnections _connections = new();
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
        new(_ctx, new SessionService(_ctx, Options.Create(_options), _db.Clock), hasher ?? new PasswordHasher<User>(), Options.Create(_options), _db.Clock,
            _failures, new AdminAlertSender(_connections, Options.Create(new AlertOptions()), NullLogger<AdminAlertSender>.Instance), NullLogger<AuthController>.Instance)
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
    public async Task Register_database_failure_alerts_admins_asks_to_retry_then_points_to_support()
    {
        var admin = new AlertTests.FakeSocket();
        var user = new AlertTests.FakeSocket();
        _connections.Add(1, 1, admin, isAdministrator: true);
        _connections.Add(2, 2, user);
        await _ctx.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_users BEFORE INSERT ON users BEGIN SELECT RAISE(ABORT, 'disk I/O error'); END;");

        async Task<ProblemDetails> FailAsync()
        {
            var result = Assert.IsType<ObjectResult>(await Controller().Register(new RegisterRequest("alice", Password), default));
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
            return Assert.IsType<ProblemDetails>(result.Value);
        }

        Assert.Contains("Please try registering again", (await FailAsync()).Title);
        Assert.Contains("Please try registering again", (await FailAsync()).Title);
        var third = await FailAsync();
        Assert.Equal("Registration failed 3 times in a row. Please email Support@BluestaqNotes.com for help.", third.Title);
        Assert.Equal(("Support@BluestaqNotes.com", 3), (third.Extensions["supportEmail"], third.Extensions["consecutiveFailures"]));

        Assert.Equal(3, admin.Sent.Count);
        var alert = System.Text.Encoding.UTF8.GetString(admin.Sent[2]);
        Assert.Contains("\"type\":\"registrationFailed\"", alert);
        Assert.Contains("\"username\":\"alice\"", alert);
        Assert.Contains("\"consecutiveFailures\":3", alert);
        Assert.Contains("disk I/O error", alert);
        Assert.Empty(user.Sent);

        await _ctx.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_users;");
        _ctx.ChangeTracker.Clear();
        await RegisterAsync("alice");
        Assert.Equal(1, _failures.Record("unknown"));
    }

    [Fact]
    public async Task Register_ends_the_callers_session_and_clears_its_cookie()
    {
        var alice = await RegisterAsync("alice");
        var session = (SessionResponse)((OkObjectResult)await Controller().Login(new LoginRequest("alice", Password), default)).Value!;
        var sessionId = (await _ctx.Sessions.AsNoTracking().SingleAsync()).Id;
        var controller = Controller(user: TestPrincipals.For(alice.Id, sessionId));
        controller.HttpContext.Request.Headers.Cookie = $"{_options.CookieName}={session.Token}";

        Assert.IsType<CreatedAtActionResult>(await controller.Register(new RegisterRequest("bob", Password), default));

        Assert.False(await _ctx.Sessions.AnyAsync());
        Assert.Contains($"{_options.CookieName}=;", controller.HttpContext.Response.Headers.SetCookie.ToString());
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

    [Fact]
    public async Task Login_ends_earlier_sessions_and_replaces_any_queued_session_cookie()
    {
        var alice = await RegisterAsync("alice");
        await RegisterAsync("bob");
        var first = (SessionResponse)((OkObjectResult)await Controller().Login(new LoginRequest("alice", Password), default)).Value!;
        var aliceSessionId = (await _ctx.Sessions.AsNoTracking().SingleAsync()).Id;

        // Bob logs in from a browser still holding Alice's session, whose sliding refresh was already queued.
        var controller = Controller(user: TestPrincipals.For(alice.Id, aliceSessionId));
        controller.HttpContext.Response.Cookies.Append(_options.CookieName, first.Token);
        var bob = (SessionResponse)((OkObjectResult)await controller.Login(new LoginRequest("bob", Password), default)).Value!;

        Assert.Equal($"{_options.CookieName}={bob.Token}", Assert.Single(controller.HttpContext.Response.Headers.SetCookie)!.Split(';')[0]);
        Assert.Equal(bob.User.Id, (await _ctx.Sessions.AsNoTracking().SingleAsync()).UserId);

        var again = (SessionResponse)((OkObjectResult)await Controller().Login(new LoginRequest("bob", Password), default)).Value!;
        var sessions = new SessionService(_db.CreateContext(), Options.Create(_options), _db.Clock);
        Assert.Null(await sessions.ValidateAsync(bob.Token, default));
        Assert.NotNull(await sessions.ValidateAsync(again.Token, default));
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
