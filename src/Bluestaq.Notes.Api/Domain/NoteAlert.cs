using System.Text.Json.Serialization;

namespace Bluestaq.Notes.Api.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<NoteAlertKind>))]
public enum NoteAlertKind
{
    /// <summary>The note was shared with the recipient.</summary>
    Shared = 0,

    /// <summary>A note shared with the recipient was edited.</summary>
    Updated = 1,

    /// <summary>An editor asked to share the recipient's note with another user.</summary>
    ShareRequested = 2,
}

/// <summary>
/// A pending (or recently delivered) alert for one recipient about one note. Repeated events of the same kind
/// collapse into one pending row, and rows are pushed to the recipient in aggregated batches.
/// </summary>
public sealed class NoteAlert
{
    public long Id { get; set; }
    public long RecipientUserId { get; set; }
    /// <summary>No foreign key: delivered rows must outlive a deleted note, since they enforce the push interval.</summary>
    public long NoteId { get; set; }
    public NoteAlertKind Kind { get; set; }

    /// <summary>Who shared or updated the note (latest occurrence).</summary>
    public long ActorUserId { get; set; }

    public DateTime FirstOccurredAt { get; set; }
    public DateTime LastOccurredAt { get; set; }
    public int Occurrences { get; set; } = 1;
    public DateTime? DeliveredAt { get; set; }
}
