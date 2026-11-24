namespace Bluestaq.Notes.Api.Domain;

public sealed class Note : IAuditable
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public User? Owner { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public long CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public long UpdatedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; }
}
