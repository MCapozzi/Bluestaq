namespace Bluestaq.Notes.Api.Domain;

/// <summary>One request made by an administrator. Deliberately has no foreign keys so entries outlive users/notes.</summary>
public sealed class AdminAuditEntry
{
    public long Id { get; set; }
    public DateTime OccurredAt { get; set; }
    public long AdminUserId { get; set; }
    public required string AdminUsername { get; set; }
    public required string Action { get; set; }
    public required string HttpMethod { get; set; }
    public required string Path { get; set; }
    public string? QueryString { get; set; }
    public long? TargetNoteId { get; set; }
    public long? TargetUserId { get; set; }
    public int StatusCode { get; set; }
    public string? ClientIp { get; set; }
    public string? TraceId { get; set; }
}
