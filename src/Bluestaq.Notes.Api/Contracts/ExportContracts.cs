using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using static Bluestaq.Notes.Api.Contracts.ValidationLimits;

namespace Bluestaq.Notes.Api.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ExportFormat>))]
public enum ExportFormat
{
    Json,
    Csv,
}

/// <summary>
/// Selects notes to export. Exactly one selector must be given: <see cref="NoteIds"/>, <see cref="UserId"/>,
/// or the <see cref="From"/>/<see cref="To"/> creation-date range (inclusive, UTC). Results are limited to notes
/// the caller can read.
/// </summary>
public sealed record NoteExportRequest(
    long[]? NoteIds = null,
    long? UserId = null,
    DateTime? From = null,
    DateTime? To = null,
    ExportFormat Format = ExportFormat.Json) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var hasRange = From is not null || To is not null;
        var selectors = (NoteIds is not null ? 1 : 0) + (UserId is not null ? 1 : 0) + (hasRange ? 1 : 0);
        if (selectors != 1)
        {
            yield return new ValidationResult(
                "Specify exactly one of: noteIds, userId, or from/to.",
                [nameof(NoteIds), nameof(UserId), nameof(From), nameof(To)]);
            yield break;
        }

        if (NoteIds is { Length: 0 })
        {
            yield return new ValidationResult("noteIds must contain at least one ID.", [nameof(NoteIds)]);
        }
        else if (NoteIds is { Length: > MaxExportNoteIds })
        {
            yield return new ValidationResult($"noteIds exceeds the maximum length of {MaxExportNoteIds}.", [nameof(NoteIds)]);
        }

        if (hasRange)
        {
            if (From is null || To is null)
            {
                yield return new ValidationResult("Both from and to are required for a date range.", [nameof(From), nameof(To)]);
            }
            else if (ToUtc(From.Value) > ToUtc(To.Value))
            {
                yield return new ValidationResult("from must be on or before to.", [nameof(From), nameof(To)]);
            }
        }
    }

    /// <summary>Timestamps without an offset are treated as UTC.</summary>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}

public sealed record NoteExportResponse(
    DateTime ExportedAt,
    int Count,
    IReadOnlyList<NoteResponse> Notes,
    IReadOnlyList<long> NotFoundIds);
