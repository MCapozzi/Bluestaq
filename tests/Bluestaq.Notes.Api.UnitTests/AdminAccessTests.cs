using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Controllers;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Bluestaq.Notes.Api.Export;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.UnitTests;

/// <summary>Administrators bypass ownership and sharing on the regular note endpoints.</summary>
public sealed class AdminAccessTests : IAsyncLifetime
{
    private readonly TestDb _db = new();
    private NotesDbContext _ctx = null!;
    private long _adminId;
    private long _bobId;
    private Note _bobsPrivate = null!;

    public async Task InitializeAsync()
    {
        _adminId = (await _db.AddUserAsync("admin")).Id;
        _bobId = (await _db.AddUserAsync("bob")).Id;
        await _db.AddUserAsync("carol");
        _bobsPrivate = await _db.AddNoteAsync(_bobId, "secret", "body");
        _ctx = _db.CreateContext();
        _db.CurrentUser.UserId = _adminId;
    }

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        _db.Dispose();
    }

    private ControllerContext AdminContext() => TestPrincipals.ControllerContext(TestPrincipals.Admin(_adminId));

    private NotesController Notes() =>
        new(_ctx, new NoteExporter(_ctx, _db.Clock, Options.Create(new ExportOptions())), new Import.NoteImporter(_ctx, _db.Clock, Options.Create(new Import.ImportOptions()))) { ControllerContext = AdminContext() };

    [Fact]
    public async Task Admin_can_read_update_and_delete_any_note_and_is_recorded_as_updater()
    {
        var read = Assert.IsType<NoteResponse>(Assert.IsType<OkObjectResult>(await Notes().Get(_bobsPrivate.Id, default)).Value);
        Assert.Equal("secret", read.Title);

        _db.Clock.Advance(TimeSpan.FromMinutes(5));
        var updated = Assert.IsType<NoteResponse>(Assert.IsType<OkObjectResult>(
            await Notes().Update(_bobsPrivate.Id, new NoteRequest("redacted", "body"), default)).Value);
        Assert.Equal(_bobId, updated.OwnerId);
        Assert.Equal(_bobId, updated.CreatedByUserId);
        Assert.Equal(_adminId, updated.UpdatedByUserId);

        Assert.IsType<NoContentResult>(await Notes().Delete(_bobsPrivate.Id, default));
        Assert.False(await _ctx.Notes.AnyAsync());
    }

    [Fact]
    public async Task Admin_export_by_user_includes_private_notes()
    {
        var ok = Assert.IsType<OkObjectResult>(await Notes().Export(new NoteExportRequest(UserId: _bobId), default));
        Assert.Equal(_bobsPrivate.Id, Assert.Single(Assert.IsType<NoteExportResponse>(ok.Value).Notes).Id);
    }

    [Fact]
    public async Task Admin_can_manage_sharing_on_any_note()
    {
        var sharing = new NoteSharingController(_ctx, _db.Clock) { ControllerContext = AdminContext() };
        var result = Assert.IsType<NoteSharingResponse>(Assert.IsType<OkObjectResult>(
            await sharing.Share(_bobsPrivate.Id, new ShareNoteRequest("carol"), default)).Value);
        Assert.Equal("carol", Assert.Single(result.SharedWith).Username);
    }

    [Fact]
    public async Task Admin_does_not_affect_regular_users()
    {
        var carol = new NotesController(_ctx, new NoteExporter(_ctx, _db.Clock, Options.Create(new ExportOptions())), new Import.NoteImporter(_ctx, _db.Clock, Options.Create(new Import.ImportOptions())))
        {
            ControllerContext = TestPrincipals.ControllerContext(TestPrincipals.For(3)),
        };
        Assert.IsType<NotFoundResult>(await carol.Get(_bobsPrivate.Id, default));
    }
}
