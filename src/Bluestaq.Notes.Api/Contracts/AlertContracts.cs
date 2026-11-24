using Bluestaq.Notes.Api.Domain;

namespace Bluestaq.Notes.Api.Contracts;

/// <summary>One WebSocket push: every pending alert for the recipient, aggregated per note.</summary>
public sealed record AlertMessage(DateTime SentAt, IReadOnlyList<NoteAlertItem> Alerts)
{
    public string Type => "alerts";
}

/// <param name="SharedBy">Username of whoever shared the note with the recipient (the owner, unless an admin shared it).</param>
/// <param name="UpdatedBy">Username of whoever last updated the note.</param>
/// <param name="LastUpdatedAt">When the note was last updated (UTC).</param>
/// <param name="Events">What happened since the previous push.</param>
/// <param name="EventCount">How many share/update events this entry aggregates.</param>
public sealed record NoteAlertItem(
    long NoteId,
    string Title,
    string SharedBy,
    string UpdatedBy,
    DateTime LastUpdatedAt,
    IReadOnlyList<NoteAlertKind> Events,
    int EventCount,
    DateTime FirstOccurredAt,
    DateTime LastOccurredAt);
