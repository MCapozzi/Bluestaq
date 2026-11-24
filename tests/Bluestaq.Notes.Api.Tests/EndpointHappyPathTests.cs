using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Domain;

namespace Bluestaq.Notes.Api.Tests;

/// <summary>Happy-path checks for endpoints not already exercised end to end by the feature test classes.</summary>
[Collection(ApiCollection.Name)]
public sealed class EndpointHappyPathTests(NotesApiFactory factory)
{
    [Fact]
    public async Task Health_is_anonymous_and_ok()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Owner_can_view_a_notes_sharing_settings()
    {
        var (alice, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (_, bobSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var note = (await (await alice.PostAsJsonAsync("/api/notes", new NoteRequest("Shared", "body"))).Content.ReadFromJsonAsync<NoteResponse>())!;
        (await alice.PostAsJsonAsync($"/api/notes/{note.Id}/shares", new ShareNoteRequest(bobSession.User.Username))).EnsureSuccessStatusCode();

        var sharing = (await alice.GetFromJsonAsync<NoteSharingResponse>($"/api/notes/{note.Id}/sharing"))!;

        Assert.Equal((note.Id, NoteVisibility.Private), (sharing.NoteId, sharing.Visibility));
        Assert.Equal(bobSession.User.Username, Assert.Single(sharing.SharedWith).Username);
    }

    [Fact]
    public async Task Admin_can_list_users_with_roles()
    {
        var (_, userSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var login = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(NotesApiFactory.AdminUsername, NotesApiFactory.AdminPassword));
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<SessionResponse>())!.Token);

        var users = new List<UserResponse>();
        PagedResponse<UserResponse> page;
        var number = 1;
        do
        {
            page = (await admin.GetFromJsonAsync<PagedResponse<UserResponse>>($"/api/admin/users?page={number++}&pageSize=100"))!;
            users.AddRange(page.Items);
        }
        while (users.Count < page.TotalCount && page.Items.Count > 0);

        Assert.Equal(UserRole.Administrator, users.Single(u => u.Username == NotesApiFactory.AdminUsername).Role);
        Assert.Equal(UserRole.User, users.Single(u => u.Id == userSession.User.Id).Role);
    }
}
