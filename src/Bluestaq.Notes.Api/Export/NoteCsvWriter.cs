using System.Globalization;
using System.Text;
using Bluestaq.Notes.Api.Contracts;

namespace Bluestaq.Notes.Api.Export;

/// <summary>Writes notes as RFC 4180 CSV.</summary>
public static class NoteCsvWriter
{
    public const string Header = "Id,OwnerId,Title,Content,CreatedByUserId,CreatedAt,UpdatedByUserId,UpdatedAt";

    public static string Write(IEnumerable<NoteResponse> notes)
    {
        var sb = new StringBuilder().Append(Header).Append("\r\n");
        foreach (var n in notes)
        {
            sb.Append(n.Id.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(n.OwnerId.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Escape(n.Title)).Append(',')
              .Append(Escape(n.Content)).Append(',')
              .Append(n.CreatedByUserId.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(n.CreatedAt.ToString("O", CultureInfo.InvariantCulture)).Append(',')
              .Append(n.UpdatedByUserId.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(n.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)).Append("\r\n");
        }

        return sb.ToString();
    }

    public static string Escape(string value)
    {
        // Neutralize spreadsheet formula injection: a leading =, +, -, @, tab or CR would be evaluated by Excel/Sheets.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
