namespace Bluestaq.Notes.Api.Domain;

/// <summary>
/// A server-side login session. Only a SHA-256 hash of the bearer token is stored,
/// so a database leak does not expose usable tokens.
/// </summary>
public sealed class Session
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public User? User { get; set; }
    public required string TokenHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}
