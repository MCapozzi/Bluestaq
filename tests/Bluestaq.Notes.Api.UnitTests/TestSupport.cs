using System.Security.Claims;
using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bluestaq.Notes.Api.UnitTests;

internal sealed class FakeCurrentUser : ICurrentUserAccessor
{
    public long? UserId { get; set; }
}

/// <summary>An isolated in-memory SQLite database using the real <see cref="NotesDbContext"/> model and audit interceptor.</summary>
internal sealed class TestDb : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public TestDb()
    {
        _connection.Open();
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();
    }

    public FakeTimeProvider Clock { get; } = new(Start);
    public FakeCurrentUser CurrentUser { get; } = new();

    public NotesDbContext CreateContext() => new(new DbContextOptionsBuilder<NotesDbContext>()
        .UseSqlite(_connection)
        .AddInterceptors(new AuditInterceptor(CurrentUser, Clock))
        .Options);

    public async Task<User> AddUserAsync(string username = "alice")
    {
        await using var ctx = CreateContext();
        var user = new User { Username = username, PasswordHash = "x", CreatedAt = Clock.GetUtcNow().UtcDateTime };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    public async Task<Note> AddNoteAsync(long ownerId, string title = "t", string content = "c")
    {
        var previous = CurrentUser.UserId;
        CurrentUser.UserId = ownerId;
        await using var ctx = CreateContext();
        var note = new Note { OwnerId = ownerId, Title = title, Content = content };
        ctx.Notes.Add(note);
        await ctx.SaveChangesAsync();
        CurrentUser.UserId = previous;
        return note;
    }

    public void Dispose() => _connection.Dispose();
}

internal static class TestPrincipals
{
    public static ClaimsPrincipal For(long userId, long sessionId = 1, string username = "user") =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, username),
            new Claim(SessionAuthenticationDefaults.SessionIdClaim, sessionId.ToString()),
        ], "Test"));

    /// <summary>A controller context whose request services can build ProblemDetails like the real pipeline.</summary>
    public static ControllerContext ControllerContext(ClaimsPrincipal user) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = user,
            RequestServices = new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider(),
        },
    };
}
