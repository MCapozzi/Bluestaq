using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Export;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class NoteCsvWriterTests
{
    [Fact]
    public void Empty_export_has_only_header()
    {
        Assert.Equal(NoteCsvWriter.Header + "\r\n", NoteCsvWriter.Write([]));
    }

    [Fact]
    public void Writes_one_row_per_note_with_iso_timestamps()
    {
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var note = new NoteResponse(7, 3, "Title", "a,b", 3, created, 4, created.AddHours(1));

        var lines = NoteCsvWriter.Write([note, note with { Id = 8 }]).Split("\r\n");

        Assert.Equal(4, lines.Length);
        Assert.Equal("7,3,Title,\"a,b\",3,2026-01-02T03:04:05.0000000Z,4,2026-01-02T04:04:05.0000000Z,Private", lines[1]);
        Assert.StartsWith("8,", lines[2]);
        Assert.Equal("", lines[3]);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("cr\rhere", "\"cr\rhere\"")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("'quoted", "''quoted")]
    [InlineData("\tx", "'\tx")]
    [InlineData("=a,b", "\"'=a,b\"")]
    public void Escape_quotes_and_neutralizes_formulas(string input, string expected)
    {
        Assert.Equal(expected, NoteCsvWriter.Escape(input));
    }
}
