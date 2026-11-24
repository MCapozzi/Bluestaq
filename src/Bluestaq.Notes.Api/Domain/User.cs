namespace Bluestaq.Notes.Api.Domain;

public sealed class User
{
    public long Id { get; set; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public UserRole Role { get; set; } = UserRole.User;
    public DigestFrequency DigestFrequency { get; set; } = DigestFrequency.None;

    /// <summary>End of the window covered by the last digest; null until the first digest after opting in.</summary>
    public DateTime? DigestLastSentAt { get; set; }

    public List<Note> Notes { get; } = [];
    public List<Session> Sessions { get; } = [];
}
