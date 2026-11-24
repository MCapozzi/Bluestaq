using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Domain;
using Bluestaq.Notes.Api.Export;
using Bluestaq.Notes.Api.Import;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class NoteCsvReaderTests
{
    private static NoteResponse Exported(long id, string title, string content, NoteVisibility visibility = NoteVisibility.Private) =>
        new(id, 7, title, content, 7, DateTime.UnixEpoch, 8, DateTime.UnixEpoch.AddDays(1), visibility);

    private static IReadOnlyDictionary<string, string[]> ErrorsOf(string csv) =>
        Assert.Throws<NoteImportException>(() => NoteCsvReader.Read(csv)).Errors;

    [Fact]
    public void Reads_back_everything_the_csv_writer_exports()
    {
        NoteResponse[] notes =
        [
            Exported(1, "Plain", "Body"),
            Exported(2, "Comma, \"quotes\"", "Line 1\r\nLine 2\nLine 3", NoteVisibility.Public),
            Exported(3, "=SUM(A1)", "-1"),
            Exported(4, "@user", "\tindented"),
            Exported(5, "+x", "\rcr"),
            Exported(6, "'already quoted", ""),
            Exported(7, "'=not a guard", "''two"),
        ];

        var imported = NoteCsvReader.Read(NoteCsvWriter.Write(notes));

        Assert.Equal(
            notes.Select(n => (n.Id, n.Title, n.Content, n.Visibility)),
            imported.Select(n => ((long)n.Id!, n.Title!, n.Content!, n.Visibility)));
    }

    [Fact]
    public void Header_only_csv_has_no_notes() =>
        Assert.Empty(NoteCsvReader.Read(NoteCsvWriter.Header + "\r\n"));

    [Fact]
    public void Only_title_and_content_columns_are_required_and_columns_match_by_name()
    {
        var imported = NoteCsvReader.Read("content,TITLE,Extra\nBody,Title,ignored\n\n,Empty body,x");

        Assert.Equal([("Title", "Body"), ("Empty body", "")], imported.Select(n => (n.Title!, n.Content!)));
        Assert.All(imported, n => Assert.Equal((null, NoteVisibility.Private), (n.Id, n.Visibility)));
    }

    [Fact]
    public void Empty_id_and_visibility_cells_use_defaults()
    {
        var note = Assert.Single(NoteCsvReader.Read("Id,Title,Content,Visibility\r\n,T,C,"));
        Assert.Equal((null, NoteVisibility.Private), (note.Id, note.Visibility));
    }

    [Fact]
    public void Visibility_is_case_insensitive() =>
        Assert.Equal(NoteVisibility.Public, Assert.Single(NoteCsvReader.Read("Title,Content,Visibility\nT,C, public ")).Visibility);

    [Theory]
    [InlineData("", "Csv")]
    [InlineData("\r\n\r\n", "Csv")]
    [InlineData("Id,Content\n1,C", "Csv")]
    [InlineData("Title,Content\n\"unterminated,C", "Csv")]
    [InlineData("Title,Content\n\"T\"x,C", "Csv")]
    [InlineData("Title,Content\nT\"x,C", "Csv")]
    [InlineData("Title,Content\nT,C,extra", "Notes[0]")]
    [InlineData("Id,Title,Content\nabc,T,C", "Notes[0].Id")]
    [InlineData("Id,Title,Content\n-1,T,C", "Notes[0].Id")]
    [InlineData("Title,Content,Visibility\nT,C,Secret", "Notes[0].Visibility")]
    [InlineData("Title,Content,Visibility\nT,C,1", "Notes[0].Visibility")]
    [InlineData("Title,Content,Visibility\nT,C,+1", "Notes[0].Visibility")]
    [InlineData("Title,Content,Visibility\nT,C,\"Private, Public\"", "Notes[0].Visibility")]
    public void Malformed_csv_is_rejected_with_the_offending_field(string csv, string key) =>
        Assert.Contains(key, ErrorsOf(csv).Keys);

    [Fact]
    public void Row_limit_is_enforced_while_parsing()
    {
        Assert.Equal(2, NoteCsvReader.Read("Title,Content\nA,1\n\nB,2\n", maxNotes: 2).Count);

        var errors = Assert.Throws<NoteImportException>(() => NoteCsvReader.Read("Title,Content\nA,1\nB,2\nC,3\nD,\"unterminated", maxNotes: 2)).Errors;
        Assert.Contains("at most 2", Assert.Single(errors["Notes"]));
    }

    [Fact]
    public void All_bad_rows_are_reported_together()
    {
        var errors = ErrorsOf("Id,Title,Content\nx,A,B\nok,C\n3,D,E");
        Assert.Equal(["Notes[0].Id", "Notes[1]"], errors.Keys.Order());
    }

    [Theory]
    [InlineData("'=x", "=x")]
    [InlineData("'\tx", "\tx")]
    [InlineData("'abc", "'abc")]
    [InlineData("''abc", "'abc")]
    [InlineData("'", "'")]
    [InlineData("plain", "plain")]
    public void Unguard_only_strips_the_writers_formula_guard(string value, string expected) =>
        Assert.Equal(expected, NoteCsvReader.Unguard(value));
}
