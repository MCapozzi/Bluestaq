using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Export;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class NoteExporterTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task By_ids_returns_owned_notes_in_creation_order_and_reports_the_rest_as_not_found()
    {
        var alice = await _db.AddUserAsync("alice");
        var bob = await _db.AddUserAsync("bob");
        var a1 = await _db.AddNoteAsync(alice.Id, "a1");
        _db.Clock.Advance(TimeSpan.FromSeconds(1));
        var a2 = await _db.AddNoteAsync(alice.Id, "a2");
        var b1 = await _db.AddNoteAsync(bob.Id, "b1");

        await using var ctx = _db.CreateContext();
        var export = await new NoteExporter(ctx, _db.Clock, Microsoft.Extensions.Options.Options.Create(new ExportOptions())).ExportAsync(
            alice.Id, new NoteExportRequest(NoteIds: [a2.Id, a1.Id, b1.Id, 9999, a1.Id]), default);

        Assert.Equal(2, export.Count);
        Assert.Equal([a1.Id, a2.Id], export.Notes.Select(n => n.Id));
        Assert.Equal([b1.Id, 9999L], export.NotFoundIds);
        Assert.Equal(TestDb.Start.UtcDateTime.AddSeconds(1), export.ExportedAt);
    }

    [Fact]
    public async Task By_user_returns_all_of_the_requesters_notes_with_audit_fields()
    {
        var alice = await _db.AddUserAsync("alice");
        var bob = await _db.AddUserAsync("bob");
        await _db.AddNoteAsync(alice.Id);
        await _db.AddNoteAsync(alice.Id);
        await _db.AddNoteAsync(bob.Id);

        await using var ctx = _db.CreateContext();
        var export = await new NoteExporter(ctx, _db.Clock, Microsoft.Extensions.Options.Options.Create(new ExportOptions())).ExportAsync(alice.Id, new NoteExportRequest(UserId: alice.Id), default);

        Assert.Equal(2, export.Count);
        Assert.Empty(export.NotFoundIds);
        Assert.All(export.Notes, n =>
        {
            Assert.Equal(alice.Id, n.OwnerId);
            Assert.Equal(alice.Id, n.CreatedByUserId);
            Assert.Equal(alice.Id, n.UpdatedByUserId);
        });
    }

    [Fact]
    public async Task By_date_range_is_inclusive_and_scoped_to_requester()
    {
        var alice = await _db.AddUserAsync("alice");
        var bob = await _db.AddUserAsync("bob");
        var day0 = await _db.AddNoteAsync(alice.Id, "day0");
        _db.Clock.Advance(TimeSpan.FromDays(1));
        var day1 = await _db.AddNoteAsync(alice.Id, "day1");
        await _db.AddNoteAsync(bob.Id, "bob-day1");
        _db.Clock.Advance(TimeSpan.FromDays(1));
        var day2 = await _db.AddNoteAsync(alice.Id, "day2");
        _db.Clock.Advance(TimeSpan.FromDays(1));
        await _db.AddNoteAsync(alice.Id, "day3");

        await using var ctx = _db.CreateContext();
        var exporter = new NoteExporter(ctx, _db.Clock, Microsoft.Extensions.Options.Options.Create(new ExportOptions()));

        var export = await exporter.ExportAsync(alice.Id, new NoteExportRequest(From: day1.CreatedAt, To: day2.CreatedAt), default);
        Assert.Equal([day1.Id, day2.Id], export.Notes.Select(n => n.Id));

        // Unspecified-kind bounds are treated as UTC.
        var unspecified = DateTime.SpecifyKind(day0.CreatedAt, DateTimeKind.Unspecified);
        var first = await exporter.ExportAsync(alice.Id, new NoteExportRequest(From: unspecified, To: unspecified), default);
        Assert.Equal(day0.Id, Assert.Single(first.Notes).Id);
    }

    [Fact]
    public async Task Selections_larger_than_the_limit_are_rejected_without_returning_partial_results()
    {
        var alice = await _db.AddUserAsync("alice");
        for (var i = 0; i < 3; i++)
        {
            await _db.AddNoteAsync(alice.Id);
        }

        await using var ctx = _db.CreateContext();
        Assert.Equal(3, (await Exporter(ctx, 3).ExportAsync(alice.Id, new NoteExportRequest(UserId: alice.Id), default)).Count);
        var ex = await Assert.ThrowsAsync<ExportLimitExceededException>(
            () => Exporter(ctx, 2).ExportAsync(alice.Id, new NoteExportRequest(UserId: alice.Id), default));
        Assert.Equal(2, ex.MaxNotes);
    }

    private NoteExporter Exporter(Bluestaq.Notes.Api.Data.NotesDbContext ctx, int maxNotes) =>
        new(ctx, _db.Clock, Microsoft.Extensions.Options.Options.Create(new ExportOptions { MaxNotes = maxNotes }));
}
