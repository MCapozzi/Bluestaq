using System.Text.Json.Serialization;

namespace Bluestaq.Notes.Api.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<NoteVisibility>))]
public enum NoteVisibility
{
    /// <summary>Only the owner and users the note is explicitly shared with.</summary>
    Private = 0,

    /// <summary>Any authenticated user.</summary>
    Public = 1,
}

/// <summary>Grants a specific user read access to a note.</summary>
public sealed class NoteShare
{
    public long NoteId { get; set; }
    public Note? Note { get; set; }
    public long UserId { get; set; }
    public User? User { get; set; }
    public DateTime SharedAt { get; set; }

    /// <summary>Who created the share (the owner or an administrator); null for shares created before this was recorded.</summary>
    public long? SharedByUserId { get; set; }
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
}
