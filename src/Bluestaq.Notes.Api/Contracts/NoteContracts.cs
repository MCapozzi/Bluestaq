using System.ComponentModel.DataAnnotations;
using Bluestaq.Notes.Api.Domain;
using static Bluestaq.Notes.Api.Contracts.ValidationLimits;

namespace Bluestaq.Notes.Api.Contracts;

public sealed record NoteRequest(
    [Required(AllowEmptyStrings = false), StringLength(TitleMaxLength)]
    string Title,
    [Required(AllowEmptyStrings = true), StringLength(ContentMaxLength)]
    string Content);

public sealed record NoteResponse(long Id, string Title, string Content, DateTime CreatedAt, DateTime UpdatedAt)
{
    public static NoteResponse From(Note note) =>
        new(note.Id, note.Title, note.Content, note.CreatedAt, note.UpdatedAt);
}

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
