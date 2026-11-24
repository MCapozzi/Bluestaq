using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Bluestaq.Notes.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class OpenApiTests(NotesApiFactory factory)
{
    [Fact]
    public async Task Export_documents_both_json_and_csv_responses()
    {
        var client = factory.WithWebHostBuilder(b => b.UseEnvironment("Development")).CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var content = doc.RootElement.GetProperty("paths").GetProperty("/api/notes/export")
            .GetProperty("post").GetProperty("responses").GetProperty("200").GetProperty("content");

        Assert.EndsWith("NoteExportResponse", content.GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.True(content.TryGetProperty("text/csv", out _));
    }

    [Fact]
    public async Task Import_documents_both_json_and_csv_request_bodies()
    {
        var client = factory.WithWebHostBuilder(b => b.UseEnvironment("Development")).CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var content = doc.RootElement.GetProperty("paths").GetProperty("/api/notes/import")
            .GetProperty("post").GetProperty("requestBody").GetProperty("content");

        Assert.EndsWith("NoteImportRequest", content.GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.True(content.TryGetProperty("text/csv", out _));
    }
}
