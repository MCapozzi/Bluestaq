using System.Net;
using System.Text.Json;
using Bluestaq.Notes.Api.Admin;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.UnitTests;

internal sealed class RecordingAuditLog : IAdminAuditLog
{
    public List<AdminAuditEntry> Entries { get; } = [];

    public Task RecordAsync(AdminAuditEntry entry, CancellationToken ct)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

public sealed class AdminAuditMiddlewareTests
{
    private readonly TestDb _db = new();
    private readonly RecordingAuditLog _log = new();

    private static DefaultHttpContext Request(string method, string path, string controller, string action, object? id = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.TraceIdentifier = "trace-1";
        var descriptor = new ControllerActionDescriptor { ControllerName = controller, ActionName = action };
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(descriptor), "test"));
        if (id is not null)
        {
            context.Request.RouteValues["id"] = id;
        }

        return context;
    }

    private AdminAuditMiddleware Middleware(RequestDelegate next) => new(next, _db.Clock);

    [Fact]
    public async Task Admin_requests_are_recorded_with_target_note_and_status()
    {
        var context = Request("PUT", "/api/notes/42", "Notes", "Update", "42");
        context.User = TestPrincipals.Admin(7, "boss");

        await Middleware(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; }).InvokeAsync(context, _log);

        var entry = Assert.Single(_log.Entries);
        Assert.Equal((7L, "boss"), (entry.AdminUserId, entry.AdminUsername));
        Assert.Equal("Notes.Update", entry.Action);
        Assert.Equal(("PUT", "/api/notes/42"), (entry.HttpMethod, entry.Path));
        Assert.Equal(42L, entry.TargetNoteId);
        Assert.Null(entry.TargetUserId);
        Assert.Equal(200, entry.StatusCode);
        Assert.Equal("127.0.0.1", entry.ClientIp);
        Assert.Equal("trace-1", entry.TraceId);
        Assert.Equal(TestDb.Start.UtcDateTime, entry.OccurredAt);
    }

    [Fact]
    public async Task Admin_user_routes_record_target_user_and_query_string()
    {
        var context = Request("PUT", "/api/admin/users/5/role", "Admin", "SetRole", "5");
        context.User = TestPrincipals.Admin(7);
        var listContext = Request("GET", "/api/admin/notes", "Admin", "ListNotes");
        listContext.Request.QueryString = new QueryString("?userId=9");
        listContext.User = TestPrincipals.Admin(7);

        await Middleware(_ => Task.CompletedTask).InvokeAsync(context, _log);
        await Middleware(_ => Task.CompletedTask).InvokeAsync(listContext, _log);

        Assert.Equal((null, 5L), (_log.Entries[0].TargetNoteId, _log.Entries[0].TargetUserId));
        Assert.Equal((9L, "?userId=9"), (_log.Entries[1].TargetUserId!.Value, _log.Entries[1].QueryString));
    }

    [Fact]
    public async Task Non_admin_and_anonymous_requests_are_not_recorded()
    {
        var user = Request("GET", "/api/notes/1", "Notes", "Get", "1");
        user.User = TestPrincipals.For(3);

        await Middleware(_ => Task.CompletedTask).InvokeAsync(user, _log);
        await Middleware(_ => Task.CompletedTask).InvokeAsync(new DefaultHttpContext(), _log);

        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task Failed_admin_requests_are_recorded_as_500_and_rethrown()
    {
        var context = Request("DELETE", "/api/notes/1", "Notes", "Delete", "1");
        context.User = TestPrincipals.Admin(7);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Middleware(_ => throw new InvalidOperationException("boom")).InvokeAsync(context, _log));

        Assert.Equal(500, Assert.Single(_log.Entries).StatusCode);
    }

    [Fact]
    public async Task Admin_login_is_recorded_from_the_actor_item()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/auth/login";

        await Middleware(ctx => { ctx.Items[AdminAuditMiddleware.ActorItemKey] = (7L, "boss"); return Task.CompletedTask; })
            .InvokeAsync(context, _log);

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(("(none)", 7L, "boss"), (entry.Action, entry.AdminUserId, entry.AdminUsername));
    }
}

public sealed class AdminAuditLogTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"admin-audit-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private AdminAuditFileLog FileLog() =>
        new(Options.Create(new AdminAuditOptions { LogFilePath = Path.Combine(_dir, "admin-audit-.log") }));

    private static AdminAuditEntry Entry() => new()
    {
        OccurredAt = TestDb.Start.UtcDateTime,
        AdminUserId = 7,
        AdminUsername = "boss",
        Action = "Notes.Get",
        HttpMethod = "GET",
        Path = "/api/notes/1",
        TargetNoteId = 1,
        StatusCode = 200,
        ClientIp = "10.0.0.1",
    };

    [Fact]
    public async Task Records_to_both_the_database_and_the_file()
    {
        var services = new ServiceCollection().AddScoped(_ => _db.CreateContext()).BuildServiceProvider();
        using (var file = FileLog())
        {
            await new AdminAuditLog(file, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AdminAuditLog>.Instance)
                .RecordAsync(Entry(), default);
        }

        await using var ctx = _db.CreateContext();
        var stored = await ctx.AdminAuditLog.SingleAsync();
        Assert.Equal(("boss", "Notes.Get", 1L), (stored.AdminUsername, stored.Action, stored.TargetNoteId!.Value));

        var line = Assert.Single(File.ReadAllLines(Assert.Single(Directory.GetFiles(_dir))));
        using var json = JsonDocument.Parse(line);
        Assert.Equal("boss", json.RootElement.GetProperty("AdminUsername").GetString());
        Assert.Equal("/api/notes/1", json.RootElement.GetProperty("Path").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("TargetNoteId").GetInt64());
        Assert.Equal("10.0.0.1", json.RootElement.GetProperty("ClientIp").GetString());
    }

    [Fact]
    public async Task Database_failures_are_logged_and_the_file_entry_is_kept()
    {
        var services = new ServiceCollection()
            .AddScoped<NotesDbContext>(_ => throw new InvalidOperationException("db down"))
            .BuildServiceProvider();
        using (var file = FileLog())
        {
            await new AdminAuditLog(file, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AdminAuditLog>.Instance)
                .RecordAsync(Entry(), default);
        }

        Assert.Single(File.ReadAllLines(Assert.Single(Directory.GetFiles(_dir))));
    }
}

public sealed class AdminBootstrapperTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly PasswordHasher<User> _hasher = new();

    public void Dispose() => _db.Dispose();

    private async Task<User?> RunAsync(string? username, string? password)
    {
        await using var ctx = _db.CreateContext();
        return await AdminBootstrapper.EnsureAsync(
            ctx, _hasher, new AdminBootstrapOptions { BootstrapUsername = username, BootstrapPassword = password }, _db.Clock, default);
    }

    [Fact]
    public async Task Does_nothing_without_a_username()
    {
        Assert.Null(await RunAsync(null, "whatever-password"));
        Assert.Null(await RunAsync("  ", "whatever-password"));
    }

    [Fact]
    public async Task Creates_a_new_administrator_with_a_hashed_password()
    {
        var admin = await RunAsync(" Admin ", "admin-password");

        await using var ctx = _db.CreateContext();
        var stored = await ctx.Users.SingleAsync();
        Assert.Equal(("admin", UserRole.Administrator), (stored.Username, stored.Role));
        Assert.Equal(PasswordVerificationResult.Success, _hasher.VerifyHashedPassword(stored, stored.PasswordHash, "admin-password"));
        Assert.Equal(stored.Id, admin!.Id);
    }

    [Fact]
    public async Task Promotes_an_existing_user_only_when_the_configured_password_matches()
    {
        var alice = await _db.AddUserAsync("alice");
        await using (var setup = _db.CreateContext())
        {
            var user = await setup.Users.SingleAsync();
            user.PasswordHash = _hasher.HashPassword(user, "alice-password");
            await setup.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("alice", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("alice", "wrong-password"));
        await RunAsync("alice", "alice-password");

        await using var ctx = _db.CreateContext();
        var stored = await ctx.Users.SingleAsync();
        Assert.Equal((alice.Id, UserRole.Administrator), (stored.Id, stored.Role));
        Assert.Equal(PasswordVerificationResult.Success, _hasher.VerifyHashedPassword(stored, stored.PasswordHash, "alice-password"));
    }

    [Fact]
    public async Task An_existing_administrator_is_kept_without_rechecking_the_password()
    {
        await RunAsync("admin", "admin-password");

        var admin = await RunAsync("admin", "changed-in-config");

        Assert.Equal(UserRole.Administrator, admin!.Role);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task Creating_requires_a_valid_password(string? password)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("admin", password));
    }
}
