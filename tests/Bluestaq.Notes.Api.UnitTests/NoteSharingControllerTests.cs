using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Controllers;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class NoteSharingControllerTests : IAsyncLifetime
{
    private readonly TestDb _db = new();
    private NotesDbContext _ctx = null!;
    private long _aliceId;
    private long _bobId;
    private Note _note = null!;

    public async Task InitializeAsync()
    {
        _aliceId = (await _db.AddUserAsync("alice")).Id;
        _bobId = (await _db.AddUserAsync("bob")).Id;
        await _db.AddUserAsync("carol");
        _note = await _db.AddNoteAsync(_aliceId);
        _ctx = _db.CreateContext();
    }

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        _db.Dispose();
    }

    private NoteSharingController ControllerFor(long userId)
    {
        _db.CurrentUser.UserId = userId;
        return new NoteSharingController(_ctx, _db.Clock)
        {
            ControllerContext = TestPrincipals.ControllerContext(TestPrincipals.For(userId)),
        };
    }

    private static NoteSharingResponse Body(IActionResult result) =>
        Assert.IsType<NoteSharingResponse>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task New_notes_are_private_and_unshared()
    {
        var sharing = Body(await ControllerFor(_aliceId).Get(_note.Id, default));
        Assert.Equal(NoteVisibility.Private, sharing.Visibility);
        Assert.Empty(sharing.SharedWith);
    }

    [Fact]
    public async Task Share_by_username_is_normalized_idempotent_and_sorted()
    {
        var alice = ControllerFor(_aliceId);
        await alice.Share(_note.Id, new ShareNoteRequest(" Carol "), default);
        await alice.Share(_note.Id, new ShareNoteRequest("bob"), default);
        var sharing = Body(await alice.Share(_note.Id, new ShareNoteRequest("BOB"), default));

        Assert.Equal(["bob", "carol"], sharing.SharedWith.Select(s => s.Username));
        Assert.Equal(_bobId, sharing.SharedWith[0].UserId);
        Assert.Equal(TestDb.Start.UtcDateTime, sharing.SharedWith[0].SharedAt);
        Assert.Equal(2, await _ctx.NoteShares.CountAsync());
    }

    [Theory]
    [InlineData("nobody", "Unknown username.")]
    [InlineData("alice", "Owners already have access to their notes.")]
    public async Task Share_rejects_unknown_users_and_the_owner(string username, string title)
    {
        var result = Assert.IsType<ObjectResult>(await ControllerFor(_aliceId).Share(_note.Id, new ShareNoteRequest(username), default));
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal(title, Assert.IsType<ProblemDetails>(result.Value).Title);
    }

    [Fact]
    public async Task Unshare_removes_access_and_is_idempotent()
    {
        var alice = ControllerFor(_aliceId);
        await alice.Share(_note.Id, new ShareNoteRequest("bob"), default);

        Assert.Empty(Body(await alice.Unshare(_note.Id, "Bob", default)).SharedWith);
        Assert.Empty(Body(await alice.Unshare(_note.Id, "bob", default)).SharedWith);
    }

    [Fact]
    public async Task SetVisibility_updates_note_and_audit_fields()
    {
        _db.Clock.Advance(TimeSpan.FromMinutes(1));

        var sharing = Body(await ControllerFor(_aliceId).SetVisibility(_note.Id, new SetVisibilityRequest(NoteVisibility.Public), default));

        Assert.Equal(NoteVisibility.Public, sharing.Visibility);
        await using var verify = _db.CreateContext();
        var stored = await verify.Notes.SingleAsync(n => n.Id == _note.Id);
        Assert.Equal(NoteVisibility.Public, stored.Visibility);
        Assert.Equal(TestDb.Start.UtcDateTime.AddMinutes(1), stored.UpdatedAt);
    }

    [Fact]
    public async Task Non_owners_get_404_or_403()
    {
        var bob = ControllerFor(_bobId);
        Assert.IsType<NotFoundResult>(await bob.Get(_note.Id, default));
        Assert.IsType<NotFoundResult>(await bob.Share(_note.Id, new ShareNoteRequest("carol"), default));
        Assert.IsType<NotFoundResult>(await bob.SetVisibility(_note.Id, new SetVisibilityRequest(NoteVisibility.Public), default));
        Assert.IsType<NotFoundResult>(await bob.Unshare(_note.Id, "carol", default));

        await ControllerFor(_aliceId).Share(_note.Id, new ShareNoteRequest("bob"), default);
        bob = ControllerFor(_bobId);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(await bob.Get(_note.Id, default)).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(await bob.Share(_note.Id, new ShareNoteRequest("carol"), default)).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(await bob.Unshare(_note.Id, "bob", default)).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_note_removes_its_shares()
    {
        await ControllerFor(_aliceId).Share(_note.Id, new ShareNoteRequest("bob"), default);
        await _ctx.Notes.Where(n => n.Id == _note.Id).ExecuteDeleteAsync();
        Assert.False(await _ctx.NoteShares.AnyAsync());
    }
}
