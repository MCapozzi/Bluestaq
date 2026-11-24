using System.Net;
using System.Net.Http.Json;
using Bluestaq.Notes.Api.Contracts;

namespace Bluestaq.Notes.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class ExportAndAuditTests(NotesApiFactory factory)
{
    [Fact]
    public async Task Create_and_update_stamp_audit_fields()
    {
        var (client, session) = await ApiClient.CreateAuthenticatedClientAsync(factory);

        var created = (await (await client.PostAsJsonAsync("/api/notes", new NoteRequest("A", "1"))).Content.ReadFromJsonAsync<NoteResponse>())!;
        Assert.Equal(session.User.Id, created.OwnerId);
        Assert.Equal(session.User.Id, created.CreatedByUserId);
        Assert.Equal(session.User.Id, created.UpdatedByUserId);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);

        await Task.Delay(20);
        var updated = (await (await client.PutAsJsonAsync($"/api/notes/{created.Id}", new NoteRequest("A", "2"))).Content.ReadFromJsonAsync<NoteResponse>())!;

        var stored = (await client.GetFromJsonAsync<NoteResponse>($"/api/notes/{created.Id}"))!;
        Assert.Equal(created.CreatedAt, stored.CreatedAt);
        Assert.Equal(session.User.Id, stored.CreatedByUserId);
        Assert.Equal(session.User.Id, stored.UpdatedByUserId);
        Assert.True(stored.UpdatedAt > stored.CreatedAt);
        Assert.Equal(updated.UpdatedAt, stored.UpdatedAt);
    }

    [Fact]
    public async Task Export_by_ids_returns_only_own_notes_and_reports_missing()
    {
        var (alice, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (bob, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var a1 = await CreateAsync(alice, "a1");
        var a2 = await CreateAsync(alice, "a2");
        var b1 = await CreateAsync(bob, "b1");

        var response = await alice.PostAsJsonAsync("/api/notes/export", new NoteExportRequest(NoteIds: [a1.Id, a2.Id, b1.Id, a1.Id]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var export = (await response.Content.ReadFromJsonAsync<NoteExportResponse>())!;
        Assert.Equal(2, export.Count);
        Assert.Equal([a1.Id, a2.Id], export.Notes.Select(n => n.Id));
        Assert.Equal([b1.Id], export.NotFoundIds);
    }

    [Fact]
    public async Task Export_by_user_id_returns_all_own_notes_and_only_readable_notes_of_others()
    {
        var (alice, aliceSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (_, bobSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        await CreateAsync(alice, "a1");
        await CreateAsync(alice, "a2");

        var export = (await (await alice.PostAsJsonAsync("/api/notes/export", new NoteExportRequest(UserId: aliceSession.User.Id)))
            .Content.ReadFromJsonAsync<NoteExportResponse>())!;
        Assert.Equal(2, export.Count);
        Assert.All(export.Notes, n => Assert.Equal(aliceSession.User.Id, n.OwnerId));

        var bobsPrivate = await ExportAsync(alice, new NoteExportRequest(UserId: bobSession.User.Id));
        Assert.Empty(bobsPrivate.Notes);
    }

    [Fact]
    public async Task Export_by_date_range_filters_on_created_at()
    {
        var (client, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var note = await CreateAsync(client, "in range");

        var inRange = await ExportAsync(client, new NoteExportRequest(From: note.CreatedAt.AddMinutes(-1), To: note.CreatedAt.AddMinutes(1)));
        // Other tests' public notes may also fall in range; only this user's notes are deterministic.
        Assert.Equal(note.Id, Assert.Single(inRange.Notes, n => n.OwnerId == note.OwnerId).Id);

        var before = await ExportAsync(client, new NoteExportRequest(From: note.CreatedAt.AddDays(-2), To: note.CreatedAt.AddDays(-1)));
        Assert.Empty(before.Notes);
    }

    [Fact]
    public async Task Export_as_csv_returns_file()
    {
        var (client, session) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        await CreateAsync(client, "Hello, \"world\"");

        var response = await client.PostAsJsonAsync("/api/notes/export", new NoteExportRequest(UserId: session.User.Id, Format: ExportFormat.Csv));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("notes-export-", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var body = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("Id,OwnerId,Title,Content,CreatedByUserId,CreatedAt,UpdatedByUserId,UpdatedAt,Visibility\r\n", body);
        Assert.Contains("\"Hello, \"\"world\"\"\"", body);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"noteIds":[1],"userId":1}""")]
    [InlineData("""{"noteIds":[]}""")]
    [InlineData("""{"from":"2026-01-01T00:00:00Z"}""")]
    [InlineData("""{"from":"2026-02-01T00:00:00Z","to":"2026-01-01T00:00:00Z"}""")]
    [InlineData("""{"userId":1,"format":"xml"}""")]
    public async Task Export_rejects_invalid_selectors(string json)
    {
        var (client, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var response = await client.PostAsync("/api/notes/export", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Export_requires_authentication()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/notes/export", new NoteExportRequest(UserId: 1));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<NoteResponse> CreateAsync(HttpClient client, string title) =>
        (await (await client.PostAsJsonAsync("/api/notes", new NoteRequest(title, "body"))).Content.ReadFromJsonAsync<NoteResponse>())!;

    private static async Task<NoteExportResponse> ExportAsync(HttpClient client, NoteExportRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/notes/export", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NoteExportResponse>())!;
    }
}
