using Bluestaq.Notes.Api.Alerts;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Controllers;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Bluestaq.Notes.Api.Export;
using Bluestaq.Notes.Api.Import;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class ShareEditorsTests : IAsyncLifetime
{
    private readonly TestDb _db = new();
    private NotesDbContext _ctx = null!;
    private long _alice;
    private long _bob;
    private long _carol;
    private long _dave;
    private Note _note = null!;

    public async Task InitializeAsync()
    {
        _alice = (await _db.AddUserAsync("alice")).Id;
        _bob = (await _db.AddUserAsync("bob")).Id;
        _carol = (await _db.AddUserAsync("carol")).Id;
        _dave = (await _db.AddUserAsync("dave")).Id;
        _note = await _db.AddNoteAsync(_alice, "Plan");
        _ctx = _db.CreateContext();
        await Sharing(_alice).Share(_note.Id, new ShareNoteRequest("bob", SharePermission.Edit), default);
        await Sharing(_alice).Share(_note.Id, new ShareNoteRequest("carol"), default);
        await _ctx.NoteAlerts.ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        _db.Dispose();
    }

    private T As<T>(T controller, long userId) where T : ControllerBase
    {
        _db.CurrentUser.UserId = userId;
        controller.ControllerContext = TestPrincipals.ControllerContext(TestPrincipals.For(userId));
        return controller;
    }

    private NoteSharingController Sharing(long userId) => As(new NoteSharingController(_ctx, _db.Clock), userId);

    private ShareRequestsController Requests(long userId) => As(new ShareRequestsController(_ctx, _db.Clock), userId);

    private NotesController Notes(long userId) => As(new NotesController(_ctx,
        new NoteExporter(_ctx, _db.Clock, Options.Create(new ExportOptions())),
        new NoteImporter(_ctx, _db.Clock, Options.Create(new ImportOptions()))), userId);

    private static int? StatusOf(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode,
        StatusCodeResult s => s.StatusCode,
        _ => null,
    };

    private static T Value<T>(IActionResult result) => Assert.IsType<T>(Assert.IsAssignableFrom<ObjectResult>(result).Value);

    private async Task<ShareRequestResponse> RequestAsync(long requester, string username, SharePermission? permission = null)
    {
        var result = await Requests(requester).Create(_note.Id, new CreateShareRequestRequest(username, permission), default);
        Assert.Equal(StatusCodes.Status201Created, StatusOf(result));
        return Value<ShareRequestResponse>(result);
    }

    private Task<NoteShare?> ShareOfAsync(long userId) =>
        _ctx.NoteShares.AsNoTracking().SingleOrDefaultAsync(s => s.NoteId == _note.Id && s.UserId == userId);

    [Fact]
    public async Task Share_sets_and_changes_the_permission()
    {
        var sharing = Value<NoteSharingResponse>(await Sharing(_alice).Get(_note.Id, default));
        Assert.Equal([SharePermission.Edit, SharePermission.Read], sharing.SharedWith.Select(s => s.Permission));

        await Sharing(_alice).Share(_note.Id, new ShareNoteRequest("bob"), default);
        Assert.Equal(SharePermission.Edit, (await ShareOfAsync(_bob))!.Permission);

        var downgraded = Value<NoteSharingResponse>(await Sharing(_alice).Share(_note.Id, new ShareNoteRequest("bob", SharePermission.Read), default));
        Assert.Equal(SharePermission.Read, downgraded.SharedWith.Single(s => s.UserId == _bob).Permission);
    }

    [Fact]
    public async Task Editors_can_edit_and_view_sharing_but_not_delete_or_manage_it()
    {
        var updated = Value<NoteResponse>(await Notes(_bob).Update(_note.Id, new NoteRequest("Plan v2", "edited"), default));
        Assert.Equal(("Plan v2", _bob), (updated.Title, updated.UpdatedByUserId));
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await Sharing(_bob).Get(_note.Id, default)));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await Notes(_bob).Delete(_note.Id, default)));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await Sharing(_bob).SetVisibility(_note.Id, new SetVisibilityRequest(NoteVisibility.Public), default)));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await Sharing(_bob).Share(_note.Id, new ShareNoteRequest("dave"), default)));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await Sharing(_bob).Unshare(_note.Id, "carol", default)));
    }

    [Fact]
    public async Task Readers_cannot_edit_view_sharing_or_request_shares()
    {
        var update = Assert.IsType<ObjectResult>(await Notes(_carol).Update(_note.Id, new NoteRequest("x", "y"), default));
        Assert.Equal(StatusCodes.Status403Forbidden, update.StatusCode);
        Assert.Equal("Only the note's owner or an editor can do this.", Assert.IsType<ProblemDetails>(update.Value).Title);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await Sharing(_carol).Get(_note.Id, default)));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await Requests(_carol).Create(_note.Id, new CreateShareRequestRequest("dave"), default)));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await Requests(_carol).ListForNote(_note.Id, null, default)));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await Requests(_dave).Create(_note.Id, new CreateShareRequestRequest("carol"), default)));
    }

    [Fact]
    public async Task Approving_a_request_creates_the_share_as_shared_by_the_editor()
    {
        var created = await RequestAsync(_bob, " Dave ");
        Assert.Equal((ShareRequestStatus.Pending, SharePermission.Read, "bob", "dave", "Plan"),
            (created.Status, created.Permission, created.RequestedBy, created.Username, created.NoteTitle));

        var repeat = await Requests(_bob).Create(_note.Id, new CreateShareRequestRequest("dave"), default);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(repeat));
        Assert.Equal(created.Id, Value<ShareRequestResponse>(repeat).Id);

        Assert.Equal(created.Id, Assert.Single(Value<List<ShareRequestResponse>>(await Requests(_alice).PendingForMyNotes(default))).Id);
        Assert.Empty(Value<List<ShareRequestResponse>>(await Requests(_bob).PendingForMyNotes(default)));
        Assert.Single(Value<List<ShareRequestResponse>>(await Requests(_bob).ListForNote(_note.Id, null, default)));
        Assert.Null(await ShareOfAsync(_dave));

        _db.Clock.Advance(TimeSpan.FromMinutes(5));
        var approved = Value<ShareRequestResponse>(await Requests(_alice).Approve(_note.Id, created.Id, default));
        Assert.Equal((ShareRequestStatus.Approved, _alice, _db.Clock.GetUtcNow().UtcDateTime), (approved.Status, approved.DecidedByUserId, approved.DecidedAt));
        var share = (await ShareOfAsync(_dave))!;
        Assert.Equal((SharePermission.Read, _bob), (share.Permission, share.SharedByUserId));
        Assert.Empty(Value<List<ShareRequestResponse>>(await Requests(_alice).PendingForMyNotes(default)));

        var again = Assert.IsType<ObjectResult>(await Requests(_alice).Approve(_note.Id, created.Id, default));
        Assert.Equal(StatusCodes.Status409Conflict, again.StatusCode);
        Assert.Equal("This request was already approved.", Assert.IsType<ProblemDetails>(again.Value).Title);
    }

    [Fact]
    public async Task Editors_can_request_an_upgrade_to_edit()
    {
        var request = await RequestAsync(_bob, "carol", SharePermission.Edit);
        await Requests(_alice).Approve(_note.Id, request.Id, default);
        Assert.Equal(SharePermission.Edit, (await ShareOfAsync(_carol))!.Permission);
    }

    [Theory]
    [InlineData("nobody", null, "Unknown username.")]
    [InlineData("alice", null, "That user already has this access.")]
    [InlineData("carol", SharePermission.Read, "That user already has this access.")]
    public async Task Create_rejects_users_that_are_unknown_or_already_have_access(string username, SharePermission? permission, string title)
    {
        var result = Assert.IsType<ObjectResult>(await Requests(_bob).Create(_note.Id, new CreateShareRequestRequest(username, permission), default));
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal(title, Assert.IsType<ProblemDetails>(result.Value).Title);
    }

    [Fact]
    public async Task Owners_and_admins_share_directly_instead_of_requesting()
    {
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(await Requests(_alice).Create(_note.Id, new CreateShareRequestRequest("dave"), default)));
        var admin = As(new ShareRequestsController(_ctx, _db.Clock), _dave);
        admin.ControllerContext = TestPrincipals.ControllerContext(TestPrincipals.Admin(_dave));
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(await admin.Create(_note.Id, new CreateShareRequestRequest("carol", SharePermission.Edit), default)));
    }

    [Fact]
    public async Task Rejecting_a_request_creates_no_share()
    {
        var request = await RequestAsync(_bob, "dave");
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await Requests(_bob).Reject(_note.Id, request.Id, default)));

        var rejected = Value<ShareRequestResponse>(await Requests(_alice).Reject(_note.Id, request.Id, default));
        Assert.Equal(ShareRequestStatus.Rejected, rejected.Status);
        Assert.Null(await ShareOfAsync(_dave));
        Assert.Single(Value<List<ShareRequestResponse>>(await Requests(_alice).ListForNote(_note.Id, ShareRequestStatus.Rejected, default)));
        Assert.Empty(Value<List<ShareRequestResponse>>(await Requests(_alice).ListForNote(_note.Id, ShareRequestStatus.Pending, default)));
        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(await Requests(_alice).Approve(_note.Id, request.Id, default)));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await Requests(_alice).Approve(_note.Id, request.Id + 100, default)));
    }

    [Fact]
    public async Task Requests_from_users_who_are_no_longer_editors_cannot_be_approved()
    {
        var request = await RequestAsync(_bob, "dave");
        await Sharing(_alice).Share(_note.Id, new ShareNoteRequest("bob", SharePermission.Read), default);

        var result = Assert.IsType<ObjectResult>(await Requests(_alice).Approve(_note.Id, request.Id, default));
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Null(await ShareOfAsync(_dave));
        Assert.Equal(ShareRequestStatus.Pending, (await _ctx.ShareRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Two_decisions_on_one_request_cannot_both_be_saved()
    {
        var request = await RequestAsync(_bob, "dave");
        await using var other = _db.CreateContext();
        var stale = await other.ShareRequests.SingleAsync(r => r.Id == request.Id);

        await Requests(_alice).Reject(_note.Id, request.Id, default);
        stale.Status = ShareRequestStatus.Approved;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.SaveChangesAsync());
    }

    [Fact]
    public async Task Deleting_the_note_deletes_its_requests()
    {
        await RequestAsync(_bob, "dave");
        await Notes(_alice).Delete(_note.Id, default);
        Assert.Empty(await _ctx.ShareRequests.ToListAsync());
    }

    [Fact]
    public async Task Editor_activity_alerts_the_owner_and_approval_alerts_the_new_user()
    {
        await Notes(_bob).Update(_note.Id, new NoteRequest("Plan v2", "edited"), default);
        var request = await RequestAsync(_bob, "dave");
        await Requests(_alice).Approve(_note.Id, request.Id, default);

        var alerts = await _ctx.NoteAlerts.AsNoTracking().ToListAsync();
        Assert.Equal(
            [(_alice, NoteAlertKind.Updated, _bob), (_alice, NoteAlertKind.ShareRequested, _bob), (_carol, NoteAlertKind.Updated, _bob), (_dave, NoteAlertKind.Shared, _bob)],
            alerts.Select(a => (a.RecipientUserId, a.Kind, a.ActorUserId)).Order());

        var batch = (await new AlertService(_ctx, _db.Clock, Options.Create(new AlertOptions())).TakePendingAsync(_alice, default))!;
        var item = Assert.Single(batch.Message.Alerts);
        Assert.Equal(("bob", "bob"), (item.SharedBy, item.UpdatedBy));
        Assert.Equal([NoteAlertKind.Updated, NoteAlertKind.ShareRequested], item.Events);
    }
}
