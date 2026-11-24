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

/// <param name="Permission">
/// <c>Read</c> or <c>Edit</c>. Omitted: <c>Read</c> for a new share, unchanged for an existing one.
/// </param>
public sealed record ShareNoteRequest(
    [Required(AllowEmptyStrings = false)] string Username,
    [EnumDataType(typeof(SharePermission))] SharePermission? Permission = null);

public sealed record NoteShareResponse(long UserId, string Username, DateTime SharedAt, SharePermission Permission);

/// <param name="Permission">Access to grant if the owner approves; defaults to <c>Read</c>.</param>
public sealed record CreateShareRequestRequest(
    [Required(AllowEmptyStrings = false)] string Username,
    [EnumDataType(typeof(SharePermission))] SharePermission? Permission = null);

public sealed record ShareRequestResponse(
    long Id,
    long NoteId,
    string NoteTitle,
    long RequestedByUserId,
    string RequestedBy,
    long UserId,
    string Username,
    SharePermission Permission,
    ShareRequestStatus Status,
    DateTime CreatedAt,
    long? DecidedByUserId,
    DateTime? DecidedAt);

public sealed record NoteSharingResponse(long NoteId, NoteVisibility Visibility, IReadOnlyList<NoteShareResponse> SharedWith);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
