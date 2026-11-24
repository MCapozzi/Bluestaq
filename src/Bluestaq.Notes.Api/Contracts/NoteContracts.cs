using System.ComponentModel.DataAnnotations;
using Bluestaq.Notes.Api.Domain;
using static Bluestaq.Notes.Api.Contracts.ValidationLimits;

namespace Bluestaq.Notes.Api.Contracts;

public sealed record NoteRequest(
    [Required(AllowEmptyStrings = false), StringLength(TitleMaxLength)]
    string Title,
    [Required(AllowEmptyStrings = true), StringLength(ContentMaxLength)]
    string Content);

public sealed record NoteResponse(
    long Id,
    long OwnerId,
    string Title,
    string Content,
    long CreatedByUserId,
    DateTime CreatedAt,
    long UpdatedByUserId,
    DateTime UpdatedAt)
{
    public static NoteResponse From(Note note) =>
        new(note.Id, note.OwnerId, note.Title, note.Content,
            note.CreatedByUserId, note.CreatedAt, note.UpdatedByUserId, note.UpdatedAt);
}

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
