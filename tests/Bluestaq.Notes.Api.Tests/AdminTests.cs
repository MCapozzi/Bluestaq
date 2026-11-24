using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Domain;

namespace Bluestaq.Notes.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class AdminTests(NotesApiFactory factory)
{
    private async Task<(HttpClient Client, SessionResponse Session)> LoginAdminAsync()
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(NotesApiFactory.AdminUsername, NotesApiFactory.AdminPassword));
        login.EnsureSuccessStatusCode();
        var session = (await login.Content.ReadFromJsonAsync<SessionResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        return (client, session);
    }

    [Fact]
    public async Task Bootstrap_admin_can_access_any_note_and_every_action_is_logged_to_db_and_file()
    {
        var (bob, bobSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var note = (await (await bob.PostAsJsonAsync("/api/notes", new NoteRequest("Private", "eyes only"))).Content.ReadFromJsonAsync<NoteResponse>())!;
        var (admin, adminSession) = await LoginAdminAsync();
        Assert.Equal(UserRole.Administrator, adminSession.User.Role);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/notes/{note.Id}")).StatusCode);
        var updated = (await (await admin.PutAsJsonAsync($"/api/notes/{note.Id}", new NoteRequest("Private", "redacted"))).Content.ReadFromJsonAsync<NoteResponse>())!;
        Assert.Equal((bobSession.User.Id, adminSession.User.Id), (updated.OwnerId, updated.UpdatedByUserId));
        var listed = (await admin.GetFromJsonAsync<PagedResponse<NoteResponse>>($"/api/admin/notes?userId={bobSession.User.Id}"))!;
        Assert.Equal(note.Id, Assert.Single(listed.Items).Id);

        var log = (await admin.GetFromJsonAsync<PagedResponse<AdminAuditEntryResponse>>($"/api/admin/audit-log?noteId={note.Id}"))!;
        Assert.Equal(["Notes.Update", "Notes.Get"], log.Items.Select(e => e.Action));
        Assert.All(log.Items, e => Assert.Equal(adminSession.User.Id, e.AdminUserId));

        // Bob's own actions are never admin-logged.
        var bobsEntries = (await admin.GetFromJsonAsync<PagedResponse<AdminAuditEntryResponse>>($"/api/admin/audit-log?adminUserId={bobSession.User.Id}"))!;
        Assert.Equal(0, bobsEntries.TotalCount);

        var fileLines = Directory.GetFiles(factory.AdminAuditDirectory)
            .SelectMany(f => ReadShared(f))
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("TraceId").ValueKind == JsonValueKind.String
                && e.TryGetProperty("TargetNoteId", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt64() == note.Id)
            .ToList();
        Assert.Equal(2, fileLines.Count);
        Assert.Contains(Directory.GetFiles(factory.AdminAuditDirectory).SelectMany(f => ReadShared(f)), l => l.Contains("\"/api/auth/login\""));
    }

    [Fact]
    public async Task Non_admins_cannot_use_admin_endpoints_or_get_elevated()
    {
        var (bob, bobSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/admin/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PutAsJsonAsync($"/api/admin/users/{bobSession.User.Id}/role", new SetRoleRequest(UserRole.Administrator))).StatusCode);
        Assert.Equal(UserRole.User, bobSession.User.Role);
    }

    [Fact]
    public async Task Promoted_user_gains_admin_access_on_next_request()
    {
        var (carol, carolSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (admin, _) = await LoginAdminAsync();

        var promote = await admin.PutAsJsonAsync($"/api/admin/users/{carolSession.User.Id}/role", new SetRoleRequest(UserRole.Administrator));
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await carol.GetAsync("/api/admin/notes")).StatusCode);
    }

    private static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
