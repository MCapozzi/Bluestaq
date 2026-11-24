using System.Net;
using System.Net.Http.Json;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Domain;

namespace Bluestaq.Notes.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class SharingTests(NotesApiFactory factory)
{
    [Fact]
    public async Task Private_share_grants_read_only_access_to_listed_users()
    {
        var (alice, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (bob, bobSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (carol, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var note = (await (await alice.PostAsJsonAsync("/api/notes", new NoteRequest("Plan", "secret"))).Content.ReadFromJsonAsync<NoteResponse>())!;

        var share = await alice.PostAsJsonAsync($"/api/notes/{note.Id}/shares", new ShareNoteRequest(bobSession.User.Username));
        Assert.Equal(HttpStatusCode.OK, share.StatusCode);
        Assert.Equal(bobSession.User.Id, Assert.Single((await share.Content.ReadFromJsonAsync<NoteSharingResponse>())!.SharedWith).UserId);

        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Equal(note.Id, Assert.Single((await bob.GetFromJsonAsync<PagedResponse<NoteResponse>>("/api/notes/shared"))!.Items).Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PutAsJsonAsync($"/api/notes/{note.Id}", new NoteRequest("x", "y"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await carol.GetAsync($"/api/notes/{note.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync($"/api/notes/{note.Id}/shares/{bobSession.User.Username}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/notes/{note.Id}")).StatusCode);
    }

    [Fact]
    public async Task Public_notes_are_readable_by_any_authenticated_user()
    {
        var (alice, aliceSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (bob, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var note = (await (await alice.PostAsJsonAsync("/api/notes", new NoteRequest("Announcement", "hi"))).Content.ReadFromJsonAsync<NoteResponse>())!;

        var visibility = await alice.PutAsJsonAsync($"/api/notes/{note.Id}/visibility", new SetVisibilityRequest(NoteVisibility.Public));
        Assert.Equal(HttpStatusCode.OK, visibility.StatusCode);

        var seen = (await bob.GetFromJsonAsync<NoteResponse>($"/api/notes/{note.Id}"))!;
        Assert.Equal(NoteVisibility.Public, seen.Visibility);
        var export = (await (await bob.PostAsJsonAsync("/api/notes/export", new NoteExportRequest(UserId: aliceSession.User.Id))).Content.ReadFromJsonAsync<NoteExportResponse>())!;
        Assert.Equal(note.Id, Assert.Single(export.Notes).Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/notes/{note.Id}")).StatusCode);

        var bad = await alice.PutAsync($"/api/notes/{note.Id}/visibility", new StringContent("""{"visibility":"friends"}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var numeric = await alice.PutAsync($"/api/notes/{note.Id}/visibility", new StringContent("""{"visibility":5}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, numeric.StatusCode);
        var format = await alice.PostAsync("/api/notes/export", new StringContent("""{"userId":1,"format":7}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, format.StatusCode);
    }

    [Fact]
    public async Task Editors_edit_and_request_shares_that_the_owner_approves_or_rejects()
    {
        var (alice, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (bob, bobSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (carol, carolSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (_, daveSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var note = (await (await alice.PostAsJsonAsync("/api/notes", new NoteRequest("Plan", "v1"))).Content.ReadFromJsonAsync<NoteResponse>())!;

        var share = await alice.PostAsJsonAsync($"/api/notes/{note.Id}/shares", new ShareNoteRequest(bobSession.User.Username, SharePermission.Edit));
        Assert.Equal(SharePermission.Edit, Assert.Single((await share.Content.ReadFromJsonAsync<NoteSharingResponse>())!.SharedWith).Permission);

        var edit = await bob.PutAsJsonAsync($"/api/notes/{note.Id}", new NoteRequest("Plan", "v2 by bob"));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(bobSession.User.Id, (await edit.Content.ReadFromJsonAsync<NoteResponse>())!.UpdatedByUserId);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/api/notes/{note.Id}/sharing")).StatusCode);

        var create = await bob.PostAsJsonAsync($"/api/notes/{note.Id}/share-requests", new CreateShareRequestRequest(carolSession.User.Username));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var request = (await create.Content.ReadFromJsonAsync<ShareRequestResponse>())!;
        Assert.Equal((ShareRequestStatus.Pending, SharePermission.Read), (request.Status, request.Permission));
        Assert.Equal(HttpStatusCode.NotFound, (await carol.GetAsync($"/api/notes/{note.Id}")).StatusCode);

        var pending = (await alice.GetFromJsonAsync<List<ShareRequestResponse>>("/api/notes/share-requests"))!;
        Assert.Equal(request.Id, Assert.Single(pending).Id);
        var forNote = (await alice.GetFromJsonAsync<List<ShareRequestResponse>>($"/api/notes/{note.Id}/share-requests?status=Pending"))!;
        Assert.Equal(bobSession.User.Username, Assert.Single(forNote).RequestedBy);

        var approve = await alice.PostAsync($"/api/notes/{note.Id}/share-requests/{request.Id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal(ShareRequestStatus.Approved, (await approve.Content.ReadFromJsonAsync<ShareRequestResponse>())!.Status);
        Assert.Equal(HttpStatusCode.OK, (await carol.GetAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await carol.PutAsJsonAsync($"/api/notes/{note.Id}", new NoteRequest("x", "y"))).StatusCode);

        var second = (await (await bob.PostAsJsonAsync($"/api/notes/{note.Id}/share-requests", new CreateShareRequestRequest(daveSession.User.Username))).Content.ReadFromJsonAsync<ShareRequestResponse>())!;
        var reject = await alice.PostAsync($"/api/notes/{note.Id}/share-requests/{second.Id}/reject", null);
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        Assert.Equal(ShareRequestStatus.Rejected, (await reject.Content.ReadFromJsonAsync<ShareRequestResponse>())!.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsync($"/api/notes/{note.Id}/share-requests/{second.Id}/approve", null)).StatusCode);
    }
}
