using System.Data.Common;
using Bluestaq.Notes.Api.Alerts;
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
    TimeProvider clock,
    RegistrationFailures registrationFailures,
    AdminAlertSender adminAlerts,
    ILogger<AuthController> logger) : ControllerBase
{
    // Used to equalize login timing when the username does not exist.
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(null!, "not-a-real-password");

    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitOptions.PolicyName)]
    [HttpPost("register")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var username = NormalizeUsername(request.Username);
        if (Request.Cookies.ContainsKey(sessionOptions.Value.CookieName))
        {
            SessionCookie.Delete(HttpContext, sessionOptions.Value);
        }

        try
        {
            // Registering ends the caller's current session, so they must log in to get a fresh session ID.
            if (User.Identity?.IsAuthenticated == true)
            {
                await sessions.RevokeAsync(User.GetSessionId(), ct);
            }

            if (await db.Users.AnyAsync(u => u.Username == username, ct))
            {
                return UsernameTaken();
            }

            var user = new User { Username = username, PasswordHash = "", CreatedAt = clock.GetUtcNow().UtcDateTime };
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            registrationFailures.Reset(ClientKey);
            return CreatedAtAction(nameof(Me), null, ToResponse(user));
        }
        catch (Exception ex) when (IsDatabaseError(ex) && !ct.IsCancellationRequested)
        {
            if (ex is DbUpdateException && await IsTakenAsync(username, ct))
            {
                // Lost a race with a concurrent registration for the same username.
                return UsernameTaken();
            }

            return await RegistrationFailedAsync(username, ex);
        }
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
        if (user.Role == UserRole.Administrator)
        {
            HttpContext.Items[Admin.AdminAuditMiddleware.ActorItemKey] = (user.Id, user.Username);
        }

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

    private string ClientKey => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private IActionResult UsernameTaken() => Problem(statusCode: StatusCodes.Status409Conflict, title: "Username is already taken.");

    private static bool IsDatabaseError(Exception ex) => ex is DbException or DbUpdateException || ex.InnerException is DbException;

    private async Task<bool> IsTakenAsync(string username, CancellationToken ct)
    {
        try
        {
            return await db.Users.AnyAsync(u => u.Username == username, ct);
        }
        catch (DbException)
        {
            return false;
        }
    }

    /// <summary>Alerts online admins, then asks the user to retry, or to email support after repeated failures.</summary>
    private async Task<IActionResult> RegistrationFailedAsync(string username, Exception ex)
    {
        var failures = registrationFailures.Record(ClientKey);
        logger.LogError(ex, "Registering {Username} failed ({Failures} in a row from {Client}).", username, failures, ClientKey);
        await adminAlerts.SendAsync(
            new RegistrationFailedAlert(clock.GetUtcNow().UtcDateTime, username, HttpContext.Connection.RemoteIpAddress?.ToString(), failures, ex.GetBaseException().Message),
            CancellationToken.None);

        var needsSupport = registrationFailures.NeedsSupport(failures);
        var result = (ObjectResult)Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: needsSupport
                ? $"Registration failed {failures} times in a row. Please email {RegistrationOptions.SupportEmail} for help."
                : "Registration failed because of a temporary problem. Please try registering again.");
        var problem = (ProblemDetails)result.Value!;
        problem.Extensions["consecutiveFailures"] = failures;
        if (needsSupport)
        {
            problem.Extensions["supportEmail"] = RegistrationOptions.SupportEmail;
        }

        return result;
    }

    private static string NormalizeUsername(string username) => username.Trim().ToLowerInvariant();

    private static UserResponse ToResponse(User user) => new(user.Id, user.Username, user.CreatedAt, user.Role);
}
