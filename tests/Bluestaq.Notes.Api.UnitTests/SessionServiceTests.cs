using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class SessionServiceTests : IDisposable
{
    private static readonly SessionAuthOptions Options = new() { IdleTimeout = TimeSpan.FromHours(1) };
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SessionService CreateService() =>
        new(_db.CreateContext(), Microsoft.Extensions.Options.Options.Create(Options), _db.Clock);

    /// <summary>Creates a session the way login does: with the user tracked by the service's own context.</summary>
    private async Task<(Session Session, string Token)> CreateSessionAsync(long userId)
    {
        var ctx = _db.CreateContext();
        var service = new SessionService(ctx, Microsoft.Extensions.Options.Options.Create(Options), _db.Clock);
        return await service.CreateAsync(await ctx.Users.SingleAsync(u => u.Id == userId), default);
    }

    [Fact]
    public async Task Create_stores_only_the_token_hash()
    {
        var user = await _db.AddUserAsync();

        var (session, token) = await CreateSessionAsync(user.Id);

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.DoesNotContain('=', token);
        Assert.Equal(SessionService.HashToken(token), session.TokenHash);
        Assert.NotEqual(token, session.TokenHash);
        Assert.Equal(TestDb.Start.UtcDateTime.AddHours(1), session.ExpiresAt);
    }

    [Fact]
    public async Task Create_ends_the_users_earlier_sessions()
    {
        var user = await _db.AddUserAsync();
        var other = await _db.AddUserAsync("bob");
        var (_, first) = await CreateSessionAsync(user.Id);
        await CreateSessionAsync(other.Id);

        var (_, second) = await CreateSessionAsync(user.Id);

        await using var ctx = _db.CreateContext();
        var service = new SessionService(ctx, Microsoft.Extensions.Options.Options.Create(Options), _db.Clock);
        Assert.Null(await service.ValidateAsync(first, default));
        Assert.NotNull(await service.ValidateAsync(second, default));
        Assert.Equal(2, await ctx.Sessions.CountAsync());
    }

    [Fact]
    public async Task Validate_returns_null_for_unknown_or_expired_tokens()
    {
        var user = await _db.AddUserAsync();
        var (_, token) = await CreateSessionAsync(user.Id);

        Assert.Null(await CreateService().ValidateAsync("bogus", default));

        _db.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Null(await CreateService().ValidateAsync(token, default));
    }

    [Fact]
    public async Task Validate_slides_expiry_only_when_stale()
    {
        var user = await _db.AddUserAsync();
        var (_, token) = await CreateSessionAsync(user.Id);

        _db.Clock.Advance(TimeSpan.FromSeconds(30));
        var fresh = await CreateService().ValidateAsync(token, default);
        Assert.NotNull(fresh);
        Assert.False(fresh.Extended);
        Assert.Equal(user.Id, fresh.Session.User!.Id);
        Assert.Equal(TestDb.Start.UtcDateTime.AddHours(1), fresh.Session.ExpiresAt);

        _db.Clock.Advance(TimeSpan.FromMinutes(5));
        var stale = await CreateService().ValidateAsync(token, default);
        Assert.True(stale!.Extended);
        var now = _db.Clock.GetUtcNow().UtcDateTime;
        Assert.Equal(now.AddHours(1), stale.Session.ExpiresAt);
        Assert.Equal(now, stale.Session.LastSeenAt);
    }

    [Fact]
    public async Task Revoke_deletes_the_session()
    {
        var user = await _db.AddUserAsync();
        var (session, token) = await CreateSessionAsync(user.Id);

        await CreateService().RevokeAsync(session.Id, default);

        Assert.Null(await CreateService().ValidateAsync(token, default));
    }

    [Fact]
    public void HashToken_is_deterministic_lowercase_sha256_hex()
    {
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", SessionService.HashToken("hello"));
    }
}
