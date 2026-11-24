namespace Bluestaq.Notes.Api.Domain;

public sealed class User
{
    public long Id { get; set; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<Note> Notes { get; } = [];
    public List<Session> Sessions { get; } = [];
}
