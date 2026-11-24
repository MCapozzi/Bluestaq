using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Bluestaq.Notes.Api.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    NotesDbContext db,
    SessionService sessions,
    IPasswordHasher<User> passwordHasher,
    IOptions<SessionAuthOptions> sessionOptions,
    TimeProvider clock) : ControllerBase
{
    // Used to equalize login timing when the username does not exist.
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(null!, "not-a-real-password");

    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitOptions.PolicyName)]
    [HttpPost("register")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var username = NormalizeUsername(request.Username);
        if (await db.Users.AnyAsync(u => u.Username == username, ct))
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Username is already taken.");
        }

        var user = new User { Username = username, PasswordHash = "", CreatedAt = clock.GetUtcNow().UtcDateTime };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent registration for the same username.
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Username is already taken.");
        }

        return CreatedAtAction(nameof(Me), null, ToResponse(user));
    }

    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitOptions.PolicyName)]
    [HttpPost("login")]
    [ProducesResponseType<SessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var username = NormalizeUsername(request.Username);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == username, ct);

        var result = passwordHasher.VerifyHashedPassword(user!, user?.PasswordHash ?? DummyHash, request.Password);
        if (user is null || result == PasswordVerificationResult.Failed)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid username or password.");
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        }

        var (session, token) = await sessions.CreateAsync(user, ct);

        SessionCookie.Append(HttpContext, sessionOptions.Value, token, session.ExpiresAt);

        return Ok(new SessionResponse(token, session.ExpiresAt, ToResponse(user)));
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await sessions.RevokeAsync(User.GetSessionId(), ct);
        SessionCookie.Delete(HttpContext, sessionOptions.Value);
        return NoContent();
    }

    [HttpGet("me")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        return Ok(ToResponse(user));
    }

    private static string NormalizeUsername(string username) => username.Trim().ToLowerInvariant();

    private static UserResponse ToResponse(User user) => new(user.Id, user.Username, user.CreatedAt);
}
