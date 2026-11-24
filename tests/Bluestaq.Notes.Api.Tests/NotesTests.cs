using System.Net;
using System.Net.Http.Json;
using Bluestaq.Notes.Api.Contracts;

namespace Bluestaq.Notes.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class NotesTests(NotesApiFactory factory)
{
    [Fact]
    public async Task Crud_lifecycle()
    {
        var (client, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);

        var create = await client.PostAsJsonAsync("/api/notes", new NoteRequest("Groceries", "Milk"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<NoteResponse>())!;
        Assert.Equal($"/api/notes/{created.Id}", create.Headers.Location!.AbsolutePath);

        var fetched = await client.GetFromJsonAsync<NoteResponse>($"/api/notes/{created.Id}");
        Assert.Equal("Milk", fetched!.Content);
        Assert.Equal(DateTimeKind.Utc, fetched.CreatedAt.Kind);

        var update = await client.PutAsJsonAsync($"/api/notes/{created.Id}", new NoteRequest("Groceries", "Milk, eggs"));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<NoteResponse>())!;
        Assert.Equal("Milk, eggs", updated.Content);
        Assert.True(updated.UpdatedAt >= updated.CreatedAt);

        var list = await client.GetFromJsonAsync<PagedResponse<NoteResponse>>("/api/notes");
        Assert.Equal(created.Id, Assert.Single(list!.Items).Id);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/notes/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/notes/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/notes/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Users_cannot_see_or_modify_each_others_notes()
    {
        var (alice, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (bob, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);

        var create = await alice.PostAsJsonAsync("/api/notes", new NoteRequest("Private", "Alice only"));
        var note = (await create.Content.ReadFromJsonAsync<NoteResponse>())!;

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsJsonAsync($"/api/notes/{note.Id}", new NoteRequest("Hacked", ""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Empty((await bob.GetFromJsonAsync<PagedResponse<NoteResponse>>("/api/notes"))!.Items);

        var stillThere = await alice.GetFromJsonAsync<NoteResponse>($"/api/notes/{note.Id}");
        Assert.Equal("Private", stillThere!.Title);
    }

    [Fact]
    public async Task List_is_paged_newest_first()
    {
        var (client, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        for (var i = 1; i <= 5; i++)
        {
            (await client.PostAsJsonAsync("/api/notes", new NoteRequest($"Note {i}", ""))).EnsureSuccessStatusCode();
        }

        var page1 = (await client.GetFromJsonAsync<PagedResponse<NoteResponse>>("/api/notes?page=1&pageSize=2"))!;
        var page3 = (await client.GetFromJsonAsync<PagedResponse<NoteResponse>>("/api/notes?page=3&pageSize=2"))!;

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(["Note 5", "Note 4"], page1.Items.Select(n => n.Title));
        Assert.Equal(["Note 1"], page3.Items.Select(n => n.Title));
    }

    [Theory]
    [InlineData("", "content")]
    [InlineData(null, "content")]
    [InlineData("title", null)]
    public async Task Create_validates_input(string? title, string? content)
    {
        var (client, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var response = await client.PostAsJsonAsync("/api/notes", new { title, content });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_oversized_title()
    {
        var (client, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var response = await client.PostAsJsonAsync("/api/notes", new NoteRequest(new string('x', ValidationLimits.TitleMaxLength + 1), ""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    public async Task List_validates_paging(string query)
    {
        var (client, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/notes?{query}")).StatusCode);
    }
}
