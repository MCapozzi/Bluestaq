using System.Security.Cryptography;
using System.Text;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.Auth;

public sealed class SessionService(NotesDbContext db, IOptions<SessionAuthOptions> options, TimeProvider clock)
{
    private readonly SessionAuthOptions _options = options.Value;

    public async Task<(Session Session, string Token)> CreateAsync(User user, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        await db.Sessions.Where(s => s.UserId == user.Id && s.ExpiresAt <= now).ExecuteDeleteAsync(ct);

        var session = new Session
        {
            UserId = user.Id,
            User = user,
            TokenHash = HashToken(token),
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + _options.IdleTimeout,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        return (session, token);
    }

    /// <summary>Returns the active session for a token, sliding its expiry forward, or null if invalid/expired.</summary>
    public async Task<Session?> ValidateAsync(string token, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = HashToken(token);
        var session = await db.Sessions.Include(s => s.User).SingleOrDefaultAsync(s => s.TokenHash == hash, ct);
        if (session is null || session.ExpiresAt <= now)
        {
            return null;
        }

        // Only write when meaningfully stale to avoid a DB write on every request.
        if (now - session.LastSeenAt > TimeSpan.FromMinutes(1))
        {
            session.LastSeenAt = now;
            session.ExpiresAt = now + _options.IdleTimeout;
            await db.SaveChangesAsync(ct);
        }

        return session;
    }

    public Task RevokeAsync(long sessionId, CancellationToken ct) =>
        db.Sessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(ct);

    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
