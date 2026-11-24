using System.Globalization;
using System.Text;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Domain;
using Bluestaq.Notes.Api.Export;

namespace Bluestaq.Notes.Api.Import;

/// <summary>
/// Reads the RFC 4180 CSV written by <see cref="NoteCsvWriter"/>. Columns are matched by header name
/// (case-insensitive); <c>Title</c> and <c>Content</c> are required, <c>Id</c> and <c>Visibility</c> are optional, and
/// any other column is ignored. The writer's spreadsheet-formula guard (a <c>'</c> before a leading = + - @ tab or CR)
/// is removed again.
/// </summary>
public static class NoteCsvReader
{
    private const string FormulaChars = "=+-@\t\r";

    /// <summary>Parses the CSV. Throws <see cref="NoteImportException"/> for malformed CSV or unparseable cells.</summary>
    public static IReadOnlyList<ImportedNote> Read(string csv)
    {
        var rows = Parse(csv);
        if (rows.Count == 0)
        {
            throw Error("Csv", "The CSV is empty; expected a header row.");
        }

        var header = rows[0].Select(h => h.Trim()).ToList();
        int Column(string name) => header.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        var (id, title, content, visibility) = (Column("Id"), Column("Title"), Column("Content"), Column("Visibility"));
        if (title < 0 || content < 0)
        {
            throw Error("Csv", "The header must include Title and Content columns.");
        }

        var notes = new List<ImportedNote>(rows.Count - 1);
        var errors = new Dictionary<string, string[]>();
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var key = $"Notes[{r - 1}]";
            if (row.Count != header.Count)
            {
                errors[key] = [$"Row {r + 1} has {row.Count} fields; the header has {header.Count}."];
                continue;
            }

            long? sourceId = null;
            if (id >= 0 && row[id].Length > 0)
            {
                if (long.TryParse(row[id], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                {
                    sourceId = parsed;
                }
                else
                {
                    errors[$"{key}.Id"] = [$"Row {r + 1}: '{row[id]}' is not a valid note ID."];
                }
            }

            var noteVisibility = NoteVisibility.Private;
            if (visibility >= 0 && row[visibility].Length > 0 && !TryParseVisibility(row[visibility], out noteVisibility))
            {
                errors[$"{key}.Visibility"] = [$"Row {r + 1}: visibility must be Private or Public."];
            }

            notes.Add(new ImportedNote
            {
                Id = sourceId,
                Title = Unguard(row[title]),
                Content = Unguard(row[content]),
                Visibility = noteVisibility,
            });
        }

        return errors.Count > 0 ? throw new NoteImportException(errors) : notes;
    }

    /// <summary>Reverses <see cref="NoteCsvWriter.Escape"/>'s formula guard.</summary>
    public static string Unguard(string value) =>
        value.Length > 1 && value[0] == '\'' && FormulaChars.Contains(value[1]) ? value[1..] : value;

    /// <summary>Accepts only a visibility name (any case), not numbers or flag combinations like "Private, Public".</summary>
    private static bool TryParseVisibility(string value, out NoteVisibility visibility)
    {
        var name = Enum.GetNames<NoteVisibility>().FirstOrDefault(n => string.Equals(n, value.Trim(), StringComparison.OrdinalIgnoreCase));
        visibility = name is null ? default : Enum.Parse<NoteVisibility>(name);
        return name is not null;
    }

    private static NoteImportException Error(string key, string message) => new(new Dictionary<string, string[]> { [key] = [message] });

    /// <summary>Splits CSV into rows of fields. Accepts CRLF or LF line endings and quoted fields spanning lines.</summary>
    private static List<List<string>> Parse(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var i = 0;
        var atFieldStart = true;

        while (i < csv.Length)
        {
            var c = csv[i];
            if (atFieldStart && c == '"')
            {
                i++;
                while (true)
                {
                    if (i >= csv.Length)
                    {
                        throw Error("Csv", $"Unterminated quoted field in row {rows.Count + 1}.");
                    }

                    if (csv[i] == '"')
                    {
                        if (i + 1 < csv.Length && csv[i + 1] == '"')
                        {
                            field.Append('"');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    field.Append(csv[i++]);
                }

                if (i < csv.Length && csv[i] is not (',' or '\r' or '\n'))
                {
                    throw Error("Csv", $"Unexpected character after a quoted field in row {rows.Count + 1}.");
                }

                atFieldStart = false;
                continue;
            }

            switch (c)
            {
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    atFieldStart = true;
                    i++;
                    break;
                case '\r' or '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    atFieldStart = true;
                    i += c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n' ? 2 : 1;
                    break;
                case '"':
                    throw Error("Csv", $"Unexpected quote in an unquoted field in row {rows.Count + 1}.");
                default:
                    field.Append(c);
                    atFieldStart = false;
                    i++;
                    break;
            }
        }

        if (!atFieldStart || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        // Blank lines (e.g. a trailing newline in an edited file) carry no data.
        rows.RemoveAll(r => r is [""]);
        return rows;
    }
}
