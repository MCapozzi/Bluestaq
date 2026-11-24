using System.ComponentModel.DataAnnotations;
using Bluestaq.Notes.Api.Domain;
using static Bluestaq.Notes.Api.Contracts.ValidationLimits;

namespace Bluestaq.Notes.Api.Contracts;

/// <summary>
/// One note to import. Matches a note in an export, so an export document can be posted as-is: only
/// <see cref="Title"/>, <see cref="Content"/> and <see cref="Visibility"/> are imported; <see cref="Id"/> is echoed
/// back as <see cref="ImportedNoteResult.SourceId"/>, and other export fields (owner, audit) are ignored because they
/// belong to the source instance.
/// </summary>
public sealed class ImportedNote
{
    public long? Id { get; init; }

    [Required(AllowEmptyStrings = false), StringLength(TitleMaxLength)]
    public string? Title { get; init; }

    [Required(AllowEmptyStrings = true), StringLength(ContentMaxLength)]
    public string? Content { get; init; }

    [EnumDataType(typeof(NoteVisibility))]
    public NoteVisibility Visibility { get; init; } = NoteVisibility.Private;
}

/// <summary>The JSON import body: an export document (<c>{ exportedAt, count, notes, notFoundIds }</c>) or just <c>{ notes }</c>.</summary>
public sealed class NoteImportRequest
{
    [Required]
    public IReadOnlyList<ImportedNote?>? Notes { get; init; }
}

public sealed record ImportedNoteResult(long? SourceId, NoteResponse Note);

public sealed record NoteImportResponse(DateTime ImportedAt, int Count, IReadOnlyList<ImportedNoteResult> Notes);
