using System.Net;
using System.Net.Http.Json;
using System.Text;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Domain;

namespace Bluestaq.Notes.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class ImportTests(NotesApiFactory factory)
{
    [Fact]
    public async Task A_json_export_imports_as_new_notes_owned_by_the_importer()
    {
        var (alice, aliceSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (bob, bobSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var note = (await (await alice.PostAsJsonAsync("/api/notes", new NoteRequest("Exported", "Body"))).Content.ReadFromJsonAsync<NoteResponse>())!;
        await alice.PutAsJsonAsync($"/api/notes/{note.Id}/visibility", new { visibility = "public" });
        var exportJson = await (await alice.PostAsJsonAsync("/api/notes/export", new NoteExportRequest(UserId: aliceSession.User.Id)))
            .Content.ReadAsStringAsync();

        var response = await bob.PostAsync("/api/notes/import", new StringContent(exportJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var imported = Assert.Single((await response.Content.ReadFromJsonAsync<NoteImportResponse>())!.Notes);
        Assert.Equal(note.Id, imported.SourceId);
        Assert.NotEqual(note.Id, imported.Note.Id);
        Assert.Equal((bobSession.User.Id, bobSession.User.Id, "Exported", "Body", NoteVisibility.Public),
            (imported.Note.OwnerId, imported.Note.CreatedByUserId, imported.Note.Title, imported.Note.Content, imported.Note.Visibility));
        Assert.Equal("Exported", (await bob.GetFromJsonAsync<NoteResponse>($"/api/notes/{imported.Note.Id}"))!.Title);
    }

    [Fact]
    public async Task A_csv_export_imports_with_text_csv()
    {
        var (client, session) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        await client.PostAsJsonAsync("/api/notes", new NoteRequest("=Tricky, \"title\"", "line 1\nline 2"));
        var csv = await (await client.PostAsJsonAsync("/api/notes/export", new NoteExportRequest(UserId: session.User.Id, Format: ExportFormat.Csv)))
            .Content.ReadAsStringAsync();

        var response = await client.PostAsync("/api/notes/import", new StringContent(csv, Encoding.UTF8, "text/csv"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var imported = Assert.Single((await response.Content.ReadFromJsonAsync<NoteImportResponse>())!.Notes);
        Assert.Equal(("=Tricky, \"title\"", "line 1\nline 2"), (imported.Note.Title, imported.Note.Content));
    }

    [Fact]
    public async Task Invalid_imports_return_400_and_save_nothing()
    {
        var (client, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);

        var response = await client.PostAsJsonAsync("/api/notes/import", new { notes = new object[] { new { title = "ok", content = "" }, new { title = "", content = "x" } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Notes[1].Title", await response.Content.ReadAsStringAsync());
        Assert.Empty((await client.GetFromJsonAsync<PagedResponse<NoteResponse>>("/api/notes"))!.Items);
    }

    [Fact]
    public async Task Import_requires_a_session() =>
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().PostAsJsonAsync("/api/notes/import", new { notes = Array.Empty<object>() })).StatusCode);
}
