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
    DateTime UpdatedAt,
    NoteVisibility Visibility = NoteVisibility.Private)
{
    public static NoteResponse From(Note note) =>
        new(note.Id, note.OwnerId, note.Title, note.Content,
            note.CreatedByUserId, note.CreatedAt, note.UpdatedByUserId, note.UpdatedAt, note.Visibility);
}

public sealed record SetVisibilityRequest([Required, EnumDataType(typeof(NoteVisibility))] NoteVisibility? Visibility);

public sealed record ShareNoteRequest([Required(AllowEmptyStrings = false)] string Username);

public sealed record NoteShareResponse(long UserId, string Username, DateTime SharedAt);

public sealed record NoteSharingResponse(long NoteId, NoteVisibility Visibility, IReadOnlyList<NoteShareResponse> SharedWith);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
