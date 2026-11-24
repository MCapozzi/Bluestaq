namespace Bluestaq.Notes.Api.Domain;

public sealed class User
{
    public long Id { get; set; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public UserRole Role { get; set; } = UserRole.User;
    /// <summary>Opted in to the daily digest of notes shared with the user (off by default).</summary>
    public bool DigestSharedNotes { get; set; }

    /// <summary>Opted in to the daily digest of new and edited public notes (off by default).</summary>
    public bool DigestPublicNotes { get; set; }

    /// <summary>End (midnight) of the last day a digest was processed for; null if none yet.</summary>
    public DateTime? DigestLastSentAt { get; set; }

    public List<Note> Notes { get; } = [];
    public List<Session> Sessions { get; } = [];
}
