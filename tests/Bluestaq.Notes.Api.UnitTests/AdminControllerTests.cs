using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Controllers;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class AdminControllerTests : IAsyncLifetime
{
    private readonly TestDb _db = new();
    private NotesDbContext _ctx = null!;
    private long _adminId;
    private long _bobId;

    public async Task InitializeAsync()
    {
        _adminId = (await _db.AddUserAsync("admin")).Id;
        _bobId = (await _db.AddUserAsync("bob")).Id;
        _ctx = _db.CreateContext();
    }

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        _db.Dispose();
    }

    private AdminController Controller() => new(_ctx)
    {
        ControllerContext = TestPrincipals.ControllerContext(TestPrincipals.Admin(_adminId)),
    };

    private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task ListUsers_returns_users_with_roles()
    {
        var page = Body<PagedResponse<UserResponse>>(await Controller().ListUsers());
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(["admin", "bob"], page.Items.Select(u => u.Username));
        Assert.All(page.Items, u => Assert.Equal(UserRole.User, u.Role));
    }

    [Fact]
    public async Task SetRole_promotes_and_demotes_other_users()
    {
        var promoted = Body<UserResponse>(await Controller().SetRole(_bobId, new SetRoleRequest(UserRole.Administrator), default));
        Assert.Equal(UserRole.Administrator, promoted.Role);
        await using (var verify = _db.CreateContext())
        {
            Assert.Equal(UserRole.Administrator, verify.Users.Single(u => u.Id == _bobId).Role);
        }

        var demoted = Body<UserResponse>(await Controller().SetRole(_bobId, new SetRoleRequest(UserRole.User), default));
        Assert.Equal(UserRole.User, demoted.Role);
    }

    [Fact]
    public async Task SetRole_rejects_self_and_unknown_users()
    {
        var self = Assert.IsType<ObjectResult>(await Controller().SetRole(_adminId, new SetRoleRequest(UserRole.User), default));
        Assert.Equal(StatusCodes.Status400BadRequest, self.StatusCode);
        Assert.IsType<NotFoundResult>(await Controller().SetRole(9999, new SetRoleRequest(UserRole.User), default));
    }

    [Fact]
    public async Task ListNotes_returns_every_users_notes_optionally_filtered_by_owner()
    {
        await _db.AddNoteAsync(_adminId);
        var bobs = await _db.AddNoteAsync(_bobId, "private");

        Assert.Equal(2, Body<PagedResponse<NoteResponse>>(await Controller().ListNotes()).TotalCount);
        var filtered = Body<PagedResponse<NoteResponse>>(await Controller().ListNotes(userId: _bobId));
        Assert.Equal(bobs.Id, Assert.Single(filtered.Items).Id);
    }

    [Fact]
    public async Task AuditLog_is_newest_first_and_filterable()
    {
        _ctx.AdminAuditLog.AddRange(
            Entry(_adminId, noteId: 1),
            Entry(_adminId, noteId: 2),
            Entry(_bobId, noteId: 1));
        await _ctx.SaveChangesAsync();

        var all = Body<PagedResponse<AdminAuditEntryResponse>>(await Controller().AuditLog());
        Assert.Equal(3, all.TotalCount);
        Assert.True(all.Items[0].Id > all.Items[1].Id);
        Assert.Equal(2, Body<PagedResponse<AdminAuditEntryResponse>>(await Controller().AuditLog(adminUserId: _adminId)).TotalCount);
        var byNote = Body<PagedResponse<AdminAuditEntryResponse>>(await Controller().AuditLog(noteId: 1));
        Assert.Equal(2, byNote.TotalCount);
        Assert.All(byNote.Items, e => Assert.Equal("Notes.Get", e.Action));
    }

    private static AdminAuditEntry Entry(long adminId, long noteId) => new()
    {
        OccurredAt = TestDb.Start.UtcDateTime,
        AdminUserId = adminId,
        AdminUsername = "a",
        Action = "Notes.Get",
        HttpMethod = "GET",
        Path = $"/api/notes/{noteId}",
        TargetNoteId = noteId,
        StatusCode = 200,
    };
}
