using System.Text.Json;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Domain;
using Bluestaq.Notes.Api.Export;
using Bluestaq.Notes.Api.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class NoteImporterTests : IAsyncLifetime
{
    private readonly TestDb _db = new();
    private long _alice;
    private long _bob;

    public async Task InitializeAsync()
    {
        _alice = (await _db.AddUserAsync("alice")).Id;
        _bob = (await _db.AddUserAsync("bob")).Id;
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private async Task<NoteImportResponse> ImportAsync(long userId, IReadOnlyList<ImportedNote?> notes, int maxNotes = 10)
    {
        _db.CurrentUser.UserId = userId;
        await using var ctx = _db.CreateContext();
        return await new NoteImporter(ctx, _db.Clock, Options.Create(new ImportOptions { MaxNotes = maxNotes }))
            .ImportAsync(userId, notes, default);
    }

    private static ImportedNote Note(string? title = "T", string? content = "C", long? id = null) =>
        new() { Id = id, Title = title, Content = content };

    private async Task<int> NoteCountAsync()
    {
        await using var ctx = _db.CreateContext();
        return await ctx.Notes.CountAsync();
    }

    [Fact]
    public async Task Imported_notes_get_new_ids_and_belong_to_the_importer()
    {
        var existing = await _db.AddNoteAsync(_alice, "Existing");
        _db.Clock.Advance(TimeSpan.FromHours(1));

        var result = await ImportAsync(_bob,
        [
            Note("First", "One", id: existing.Id),
            new ImportedNote { Id = 99, Title = "Second", Content = "", Visibility = NoteVisibility.Public },
        ]);

        Assert.Equal(2, result.Count);
        Assert.Equal(_db.Clock.GetUtcNow().UtcDateTime, result.ImportedAt);
        Assert.Equal([existing.Id, 99L], result.Notes.Select(n => (long)n.SourceId!));
        Assert.DoesNotContain(existing.Id, result.Notes.Select(n => n.Note.Id));
        Assert.All(result.Notes, r => Assert.Equal(
            (_bob, _bob, _bob, _db.Clock.GetUtcNow().UtcDateTime),
            (r.Note.OwnerId, r.Note.CreatedByUserId, r.Note.UpdatedByUserId, r.Note.CreatedAt)));
        Assert.Equal(
            [("First", "One", NoteVisibility.Private), ("Second", "", NoteVisibility.Public)],
            result.Notes.Select(r => (r.Note.Title, r.Note.Content, r.Note.Visibility)));
        Assert.Equal(3, await NoteCountAsync());
    }

    [Fact]
    public async Task A_json_export_from_another_instance_imports_as_is()
    {
        var exported = new NoteExportResponse(DateTime.UnixEpoch, 1,
            [new NoteResponse(500, 42, "Remote", "From elsewhere", 42, DateTime.UnixEpoch, 43, DateTime.UnixEpoch, NoteVisibility.Public)],
            [7]);
        var request = JsonSerializer.Deserialize<NoteImportRequest>(
            JsonSerializer.Serialize(exported, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;

        var imported = Assert.Single((await ImportAsync(_alice, request.Notes!)).Notes);

        Assert.Equal((500L, "Remote", "From elsewhere", NoteVisibility.Public, _alice),
            ((long)imported.SourceId!, imported.Note.Title, imported.Note.Content, imported.Note.Visibility, imported.Note.OwnerId));
    }

    [Fact]
    public async Task A_csv_export_round_trips_through_import()
    {
        var source = await _db.AddNoteAsync(_alice, "=formula, \"quoted\"", "multi\r\nline");
        var csv = NoteCsvWriter.Write([NoteResponse.From(source)]);

        var imported = Assert.Single((await ImportAsync(_bob, NoteCsvReader.Read(csv))).Notes);

        Assert.Equal((source.Id, source.Title, source.Content), ((long)imported.SourceId!, imported.Note.Title, imported.Note.Content));
    }

    [Fact]
    public async Task Any_invalid_note_rejects_the_whole_import()
    {
        var ex = await Assert.ThrowsAsync<NoteImportException>(() => ImportAsync(_alice,
        [
            Note(),
            Note(title: ""),
            null,
            Note(content: null),
            Note(title: new string('x', ValidationLimits.TitleMaxLength + 1)),
            new ImportedNote { Title = "T", Content = "C", Visibility = (NoteVisibility)9 },
        ]));

        Assert.Equal(
            ["Notes[1].Title", "Notes[2]", "Notes[3].Content", "Notes[4].Title", "Notes[5].Visibility"],
            ex.Errors.Keys.Order());
        Assert.Equal(0, await NoteCountAsync());
    }

    [Fact]
    public async Task Empty_imports_are_rejected()
    {
        var ex = await Assert.ThrowsAsync<NoteImportException>(() => ImportAsync(_alice, []));
        Assert.Contains("at least one", Assert.Single(ex.Errors[nameof(NoteImportRequest.Notes)]));
    }

    [Fact]
    public async Task Imports_over_the_limit_are_rejected_before_saving()
    {
        var ex = await Assert.ThrowsAsync<NoteImportException>(() => ImportAsync(_alice, [Note(), Note(), Note(title: "")], maxNotes: 2));

        Assert.Contains("at most 2", Assert.Single(ex.Errors).Value.Single());
        Assert.Equal(0, await NoteCountAsync());
    }
}
