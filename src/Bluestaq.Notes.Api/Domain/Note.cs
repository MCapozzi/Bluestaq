namespace Bluestaq.Notes.Api.Domain;

public sealed class Note : IAuditable
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public User? Owner { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public NoteVisibility Visibility { get; set; } = NoteVisibility.Private;
    public List<NoteShare> Shares { get; } = [];
    public long CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public long UpdatedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Last change to the title or content (unlike <see cref="UpdatedAt"/>, which visibility changes also bump).</summary>
    public long ContentUpdatedByUserId { get; set; }

    public DateTime ContentUpdatedAt { get; set; }

    /// <summary>When the note last became public; null while private.</summary>
    public DateTime? PublicSince { get; set; }
}
