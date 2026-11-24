using System.ComponentModel.DataAnnotations;

namespace Bluestaq.Notes.Api.Contracts;

/// <param name="SharedNotes">Include notes shared with you and edits to them.</param>
/// <param name="PublicNotes">Include new public notes and edits to public notes.</param>
public sealed record UpdateDigestRequest([Required] bool? SharedNotes, [Required] bool? PublicNotes);

/// <param name="LastDigestUntil">End (midnight) of the last day a digest was processed for; null if none yet.</param>
/// <param name="NextDigestAt">When the next digest goes out (UTC); null when opted out of both.</param>
/// <param name="TimeZone">Digests go out at midnight in this time zone.</param>
/// <param name="Recipient">Where digests are emailed (fixed for this project).</param>
public sealed record DigestSettingsResponse(
    bool SharedNotes,
    bool PublicNotes,
    DateTime? LastDigestUntil,
    DateTime? NextDigestAt,
    string TimeZone,
    string Recipient);

/// <summary>A note shared with the digest's user.</summary>
/// <param name="SharedBy">Username of whoever shared the note (the owner, unless an admin shared it).</param>
/// <param name="UpdatedBy">Username of whoever last edited the note's title or content.</param>
public sealed record DigestNote(
    long NoteId,
    string Title,
    string SharedBy,
    DateTime SharedAt,
    string UpdatedBy,
    DateTime LastUpdatedAt);

/// <summary>A public note owned by someone else.</summary>
/// <param name="PublicSince">When the note became public.</param>
/// <param name="UpdatedBy">Username of whoever last edited the note's title or content.</param>
public sealed record PublicDigestNote(
    long NoteId,
    string Title,
    string Owner,
    DateTime PublicSince,
    string UpdatedBy,
    DateTime LastUpdatedAt);

/// <summary>Digest contents for one user, covering [<paramref name="From"/>, <paramref name="Until"/>).</summary>
/// <param name="NewlyShared">Notes shared with the user during the window.</param>
/// <param name="Updated">Notes shared with the user that someone else edited during the window, after sharing.</param>
/// <param name="NewPublic">Notes that became public during the window.</param>
/// <param name="UpdatedPublic">Public notes that someone else edited during the window, after they became public.</param>
public sealed record NoteDigest(
    string Username,
    DateTime From,
    DateTime Until,
    IReadOnlyList<DigestNote> NewlyShared,
    IReadOnlyList<DigestNote> Updated,
    IReadOnlyList<PublicDigestNote> NewPublic,
    IReadOnlyList<PublicDigestNote> UpdatedPublic)
{
    public bool IsEmpty => NewlyShared.Count == 0 && Updated.Count == 0 && NewPublic.Count == 0 && UpdatedPublic.Count == 0;
}
