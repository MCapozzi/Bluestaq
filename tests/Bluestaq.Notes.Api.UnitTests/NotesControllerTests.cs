using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Controllers;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Export;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class NotesControllerTests : IAsyncLifetime
{
    private readonly TestDb _db = new();
    private NotesDbContext _ctx = null!;
    private long _aliceId;
    private long _bobId;

    public async Task InitializeAsync()
    {
        _aliceId = (await _db.AddUserAsync("alice")).Id;
        _bobId = (await _db.AddUserAsync("bob")).Id;
        _ctx = _db.CreateContext();
    }

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        _db.Dispose();
    }

    private NotesController ControllerFor(long userId)
    {
        _db.CurrentUser.UserId = userId;
        return new NotesController(_ctx, new NoteExporter(_ctx, _db.Clock, Microsoft.Extensions.Options.Options.Create(new ExportOptions { MaxNotes = 3 })),
            new Import.NoteImporter(_ctx, _db.Clock, Microsoft.Extensions.Options.Options.Create(new Import.ImportOptions { MaxNotes = 3 })))
        {
            ControllerContext = TestPrincipals.ControllerContext(TestPrincipals.For(userId)),
        };
    }

    [Fact]
    public async Task Create_returns_201_with_audited_note()
    {
        var result = await ControllerFor(_aliceId).Create(new NoteRequest("Title", "Body"), default);

        var created = Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(nameof(NotesController.Get), created.ActionName);
        var note = Assert.IsType<NoteResponse>(created.Value);
        Assert.Equal(note.Id, created.RouteValues!["id"]);
        Assert.Equal(("Title", "Body", _aliceId, _aliceId, _aliceId), (note.Title, note.Content, note.OwnerId, note.CreatedByUserId, note.UpdatedByUserId));
        Assert.Equal(TestDb.Start.UtcDateTime, note.CreatedAt);
    }

    [Fact]
    public async Task Get_returns_own_note_and_404_for_others()
    {
        var note = await _db.AddNoteAsync(_aliceId, "mine");

        var ok = Assert.IsType<OkObjectResult>(await ControllerFor(_aliceId).Get(note.Id, default));
        Assert.Equal("mine", Assert.IsType<NoteResponse>(ok.Value).Title);

        Assert.IsType<NotFoundResult>(await ControllerFor(_bobId).Get(note.Id, default));
        Assert.IsType<NotFoundResult>(await ControllerFor(_aliceId).Get(12345, default));
    }

    [Fact]
    public async Task List_pages_own_notes_most_recently_updated_first()
    {
        for (var i = 1; i <= 3; i++)
        {
            await _db.AddNoteAsync(_aliceId, $"n{i}");
            _db.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        await _db.AddNoteAsync(_bobId, "bob");

        var ok = Assert.IsType<OkObjectResult>(await ControllerFor(_aliceId).List(page: 1, pageSize: 2));
        var page = Assert.IsType<PagedResponse<NoteResponse>>(ok.Value);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(["n3", "n2"], page.Items.Select(n => n.Title));

        var page2 = (PagedResponse<NoteResponse>)((OkObjectResult)await ControllerFor(_aliceId).List(page: 2, pageSize: 2)).Value!;
        Assert.Equal(["n1"], page2.Items.Select(n => n.Title));
    }

    [Fact]
    public async Task Update_replaces_fields_and_stamps_updater()
    {
        var note = await _db.AddNoteAsync(_aliceId, "old", "old body");
        _db.Clock.Advance(TimeSpan.FromMinutes(1));

        var ok = Assert.IsType<OkObjectResult>(await ControllerFor(_aliceId).Update(note.Id, new NoteRequest("new", "new body"), default));
        var updated = Assert.IsType<NoteResponse>(ok.Value);
        Assert.Equal(("new", "new body"), (updated.Title, updated.Content));
        Assert.Equal(TestDb.Start.UtcDateTime, updated.CreatedAt);
        Assert.Equal(TestDb.Start.UtcDateTime.AddMinutes(1), updated.UpdatedAt);
        Assert.Equal(_aliceId, updated.UpdatedByUserId);
    }

    [Fact]
    public async Task Update_with_identical_values_still_writes_and_stamps()
    {
        var note = await _db.AddNoteAsync(_aliceId, "same", "same");
        _db.Clock.Advance(TimeSpan.FromMinutes(1));

        await ControllerFor(_aliceId).Update(note.Id, new NoteRequest("same", "same"), default);

        await using var verify = _db.CreateContext();
        Assert.Equal(TestDb.Start.UtcDateTime.AddMinutes(1), (await verify.Notes.SingleAsync(n => n.Id == note.Id)).UpdatedAt);
    }

    [Fact]
    public async Task Update_of_another_users_note_is_404()
    {
        var note = await _db.AddNoteAsync(_aliceId);
        Assert.IsType<NotFoundResult>(await ControllerFor(_bobId).Update(note.Id, new NoteRequest("x", "y"), default));
    }

    [Fact]
    public async Task Delete_removes_own_note_only()
    {
        var note = await _db.AddNoteAsync(_aliceId);

        Assert.IsType<NotFoundResult>(await ControllerFor(_bobId).Delete(note.Id, default));
        Assert.IsType<NoContentResult>(await ControllerFor(_aliceId).Delete(note.Id, default));
        Assert.IsType<NotFoundResult>(await ControllerFor(_aliceId).Delete(note.Id, default));
    }

    [Fact]
    public async Task Import_returns_201_with_new_notes_owned_by_the_caller()
    {
        var result = await ControllerFor(_bobId).Import(
            new NoteImportRequest { Notes = [new ImportedNote { Id = 12, Title = "T", Content = "C" }] }, default);

        var created = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var imported = Assert.Single(Assert.IsType<NoteImportResponse>(created.Value).Notes);
        Assert.Equal((12L, _bobId), ((long)imported.SourceId!, imported.Note.OwnerId));
    }

    [Fact]
    public async Task Import_returns_validation_problem_for_invalid_notes()
    {
        var result = await ControllerFor(_bobId).Import(new NoteImportRequest { Notes = [null] }, default);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Contains("Notes[0]", problem.Errors.Keys);
    }

    [Fact]
    public async Task Import_csv_reads_the_request_body()
    {
        var controller = ControllerFor(_aliceId);
        controller.HttpContext.Request.Body = new MemoryStream("Title,Content\r\nFrom CSV,Body\r\n"u8.ToArray());

        var created = Assert.IsType<ObjectResult>(await controller.ImportCsv(default));

        Assert.Equal("From CSV", Assert.Single(Assert.IsType<NoteImportResponse>(created.Value).Notes).Note.Title);
    }

    [Fact]
    public async Task Import_csv_returns_validation_problem_for_malformed_csv()
    {
        var controller = ControllerFor(_aliceId);
        controller.HttpContext.Request.Body = new MemoryStream("Id\r\n1\r\n"u8.ToArray());

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(await controller.ImportCsv(default)).Value);
        Assert.Contains("Csv", problem.Errors.Keys);
    }

    [Fact]
    public async Task Export_returns_json_by_default()
    {
        var note = await _db.AddNoteAsync(_aliceId);

        var ok = Assert.IsType<OkObjectResult>(await ControllerFor(_aliceId).Export(new NoteExportRequest(NoteIds: [note.Id]), default));
        var export = Assert.IsType<NoteExportResponse>(ok.Value);
        Assert.Equal(note.Id, Assert.Single(export.Notes).Id);
    }

    [Fact]
    public async Task Export_as_csv_returns_named_file()
    {
        await _db.AddNoteAsync(_aliceId, "csv title");

        var result = await ControllerFor(_aliceId).Export(new NoteExportRequest(UserId: _aliceId, Format: ExportFormat.Csv), default);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.Equal("notes-export-20260101T120000Z.csv", file.FileDownloadName);
        var csv = System.Text.Encoding.UTF8.GetString(file.FileContents);
        Assert.StartsWith(NoteCsvWriter.Header, csv);
        Assert.Contains("csv title", csv);
    }

    [Fact]
    public async Task Export_for_another_user_returns_only_their_notes_the_caller_can_read()
    {
        await _db.AddNoteAsync(_bobId, "private");
        var shared = await ShareAsync(await _db.AddNoteAsync(_bobId, "shared"), _aliceId);
        var pub = await MakePublicAsync(await _db.AddNoteAsync(_bobId, "public"));

        var ok = Assert.IsType<OkObjectResult>(await ControllerFor(_aliceId).Export(new NoteExportRequest(UserId: _bobId), default));
        Assert.Equal([shared.Id, pub.Id], Assert.IsType<NoteExportResponse>(ok.Value).Notes.Select(n => n.Id));
    }

    [Fact]
    public async Task Shared_and_public_notes_are_readable_but_not_writable_by_others()
    {
        var shared = await ShareAsync(await _db.AddNoteAsync(_bobId, "shared"), _aliceId);
        var pub = await MakePublicAsync(await _db.AddNoteAsync(_bobId, "public"));
        var priv = await _db.AddNoteAsync(_bobId, "private");
        var alice = ControllerFor(_aliceId);

        Assert.IsType<OkObjectResult>(await alice.Get(shared.Id, default));
        var publicNote = Assert.IsType<NoteResponse>(Assert.IsType<OkObjectResult>(await alice.Get(pub.Id, default)).Value);
        Assert.Equal(Domain.NoteVisibility.Public, publicNote.Visibility);
        Assert.IsType<NotFoundResult>(await alice.Get(priv.Id, default));

        foreach (var id in new[] { shared.Id, pub.Id })
        {
            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(await alice.Update(id, new NoteRequest("x", "y"), default)).StatusCode);
            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(await alice.Delete(id, default)).StatusCode);
        }
        Assert.IsType<NotFoundResult>(await alice.Update(priv.Id, new NoteRequest("x", "y"), default));
        Assert.IsType<NotFoundResult>(await alice.Delete(priv.Id, default));
    }

    [Fact]
    public async Task ListShared_returns_other_users_readable_notes_only()
    {
        await _db.AddNoteAsync(_aliceId, "mine");
        await _db.AddNoteAsync(_bobId, "private");
        var shared = await ShareAsync(await _db.AddNoteAsync(_bobId, "shared"), _aliceId);
        _db.Clock.Advance(TimeSpan.FromSeconds(1));
        var pub = await MakePublicAsync(await _db.AddNoteAsync(_bobId, "public"));

        var page = (PagedResponse<NoteResponse>)((OkObjectResult)await ControllerFor(_aliceId).ListShared()).Value!;
        Assert.Equal(2, page.TotalCount);
        Assert.Equal([pub.Id, shared.Id], page.Items.Select(n => n.Id));
    }

    private async Task<Domain.Note> ShareAsync(Domain.Note note, long userId)
    {
        await using var ctx = _db.CreateContext();
        ctx.NoteShares.Add(new Domain.NoteShare { NoteId = note.Id, UserId = userId, SharedAt = TestDb.Start.UtcDateTime });
        await ctx.SaveChangesAsync();
        return note;
    }

    private async Task<Domain.Note> MakePublicAsync(Domain.Note note)
    {
        var previous = _db.CurrentUser.UserId;
        _db.CurrentUser.UserId = note.OwnerId;
        await using var ctx = _db.CreateContext();
        var tracked = await ctx.Notes.SingleAsync(n => n.Id == note.Id);
        tracked.Visibility = Domain.NoteVisibility.Public;
        await ctx.SaveChangesAsync();
        _db.CurrentUser.UserId = previous;
        return tracked;
    }

    [Fact]
    public async Task Export_over_the_limit_is_a_400()
    {
        for (var i = 0; i < 4; i++)
        {
            await _db.AddNoteAsync(_aliceId);
        }

        var result = Assert.IsType<ObjectResult>(await ControllerFor(_aliceId).Export(new NoteExportRequest(UserId: _aliceId), default));
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal("Export too large.", Assert.IsType<ProblemDetails>(result.Value).Title);
    }
}
