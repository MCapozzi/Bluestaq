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
    }
}
