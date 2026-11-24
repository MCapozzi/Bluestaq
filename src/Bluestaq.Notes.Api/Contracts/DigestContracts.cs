using System.ComponentModel.DataAnnotations;
using Bluestaq.Notes.Api.Domain;

namespace Bluestaq.Notes.Api.Contracts;

public sealed record UpdateDigestRequest([Required, EnumDataType(typeof(DigestFrequency))] DigestFrequency? Frequency);

/// <param name="LastSentAt">End of the window covered by the last digest; null if none has been sent since opting in.</param>
/// <param name="NextDigestAt">Earliest time the next digest goes out; null when opted out.</param>
/// <param name="Recipient">Where digests are emailed (fixed for this project).</param>
public sealed record DigestSettingsResponse(DigestFrequency Frequency, DateTime? LastSentAt, DateTime? NextDigestAt, string Recipient);

/// <summary>A note shared with the digest's user.</summary>
/// <param name="SharedBy">Username of whoever shared the note (the owner, unless an admin shared it).</param>
/// <param name="UpdatedBy">Username of whoever last updated the note.</param>
public sealed record DigestNote(
    long NoteId,
    string Title,
    string SharedBy,
    DateTime SharedAt,
    string UpdatedBy,
    DateTime LastUpdatedAt);

/// <summary>Digest contents for one user.</summary>
/// <param name="Since">Start of the window (exclusive); null for the first digest, which lists every note shared with the user.</param>
/// <param name="Until">End of the window.</param>
/// <param name="NewlyShared">Notes shared with the user during the window.</param>
/// <param name="Updated">Notes changed during the window, after they were shared with the user, by someone else.</param>
public sealed record NoteDigest(
    string Username,
    DigestFrequency Frequency,
    DateTime? Since,
    DateTime Until,
    IReadOnlyList<DigestNote> NewlyShared,
    IReadOnlyList<DigestNote> Updated)
{
    public bool IsEmpty => NewlyShared.Count == 0 && Updated.Count == 0;
}
