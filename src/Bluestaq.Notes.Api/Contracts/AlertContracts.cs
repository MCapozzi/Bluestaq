using Bluestaq.Notes.Api.Domain;

namespace Bluestaq.Notes.Api.Contracts;

/// <summary>One WebSocket push: every pending alert for the recipient, aggregated per note.</summary>
public sealed record AlertMessage(DateTime SentAt, IReadOnlyList<NoteAlertItem> Alerts)
{
    public string Type => "alerts";
}

/// <summary>Pushed immediately to online administrators when a registration fails for a reason other than a taken username.</summary>
/// <param name="ConsecutiveFailures">Registration failures in a row from the same client IP, including this one.</param>
/// <param name="Error">The underlying database error message.</param>
public sealed record RegistrationFailedAlert(DateTime OccurredAt, string Username, string? ClientIp, int ConsecutiveFailures, string Error)
{
    public string Type => "registrationFailed";
}

/// <param name="SharedBy">
/// Username of whoever shared the note with the recipient (the owner, an administrator, or the editor whose request was
/// approved). For a share request on the recipient's own note, the requesting editor; for edits to it, the owner.
/// </param>
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
