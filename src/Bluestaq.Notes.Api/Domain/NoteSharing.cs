using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<NoteVisibility>))]
public enum NoteVisibility
{
    /// <summary>Only the owner and users the note is explicitly shared with.</summary>
    Private = 0,

    /// <summary>Any authenticated user.</summary>
    Public = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter<SharePermission>))]
public enum SharePermission
{
    /// <summary>Can read the note.</summary>
    Read = 0,

    /// <summary>Can read and edit the note's title and content, and request to share it with more users.</summary>
    Edit = 1,
}

/// <summary>Grants a specific user read (or read-write) access to a note.</summary>
public sealed class NoteShare
{
    public long NoteId { get; set; }
    public Note? Note { get; set; }
    public long UserId { get; set; }
    public User? User { get; set; }
    public DateTime SharedAt { get; set; }

    /// <summary>Who created the share (the owner or an administrator); null for shares created before this was recorded.</summary>
    public long? SharedByUserId { get; set; }

    public SharePermission Permission { get; set; } = SharePermission.Read;
}

[JsonConverter(typeof(JsonStringEnumConverter<ShareRequestStatus>))]
public enum ShareRequestStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
}

/// <summary>An editor's request to share a note with another user; the share is created only when the owner approves.</summary>
public sealed class ShareRequest
{
    public long Id { get; set; }
    public long NoteId { get; set; }
    public Note? Note { get; set; }
    public long RequestedByUserId { get; set; }
    public User? RequestedBy { get; set; }
    public long TargetUserId { get; set; }
    public User? TargetUser { get; set; }
    public SharePermission Permission { get; set; }
    public ShareRequestStatus Status { get; set; } = ShareRequestStatus.Pending;
    public DateTime CreatedAt { get; set; }
    public long? DecidedByUserId { get; set; }
    public DateTime? DecidedAt { get; set; }
}

public static class NoteAccess
{
    /// <summary>Administrators can read every note; everyone else gets <see cref="ReadableBy"/>.</summary>
    public static IQueryable<Note> VisibleTo(this IQueryable<Note> notes, long userId, bool isAdministrator) =>
        isAdministrator ? notes : notes.ReadableBy(userId);

    /// <summary>Notes the user may read: their own, public notes, and notes shared with them.</summary>
    public static IQueryable<Note> ReadableBy(this IQueryable<Note> notes, long userId) =>
        notes.Where(n => n.OwnerId == userId
            || n.Visibility == NoteVisibility.Public
            || n.Shares.Any(s => s.UserId == userId));

    /// <summary>Whether the user holds an edit share on the note (owners and administrators are checked separately).</summary>
    public static Task<bool> IsEditorAsync(this IQueryable<NoteShare> shares, long noteId, long userId, CancellationToken ct) =>
        shares.AnyAsync(s => s.NoteId == noteId && s.UserId == userId && s.Permission == SharePermission.Edit, ct);
}
