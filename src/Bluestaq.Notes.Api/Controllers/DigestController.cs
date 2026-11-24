using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Digest;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Controllers;

/// <summary>The caller's opt-ins to the daily digest email.</summary>
[ApiController]
[Route("api/digest")]
public sealed class DigestController(NotesDbContext db, DigestService digests, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<DigestSettingsResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(ToResponse(await CurrentUserAsync(ct)));

    /// <summary>Opts in or out of each digest section; both are off by default.</summary>
    [HttpPut]
    [ProducesResponseType<DigestSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update(UpdateDigestRequest request, CancellationToken ct)
    {
        var (sharedNotes, publicNotes) = (request.SharedNotes!.Value, request.PublicNotes!.Value);
        await db.Users.Where(u => u.Id == User.GetUserId()).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.DigestSharedNotes, sharedNotes)
            .SetProperty(u => u.DigestPublicNotes, publicNotes), ct);
        return Ok(ToResponse(await CurrentUserAsync(ct)));
    }

    /// <summary>What tonight's digest contains so far (today's activity), without sending it.</summary>
    [HttpGet("preview")]
    [ProducesResponseType<NoteDigest>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview(CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        return Ok(await digests.BuildAsync(
            user.Id, user.Username, user.DigestSharedNotes, user.DigestPublicNotes, digests.Schedule.StartOfDay(now), digests.Schedule.NextMidnight(now), ct));
    }

    private Task<User> CurrentUserAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        return db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
    }

    private DigestSettingsResponse ToResponse(User user)
    {
        var next = user.DigestSharedNotes || user.DigestPublicNotes
            ? digests.Schedule.NextMidnight(clock.GetUtcNow().UtcDateTime)
            : (DateTime?)null;
        return new DigestSettingsResponse(
            user.DigestSharedNotes, user.DigestPublicNotes, user.DigestLastSentAt, next, digests.Schedule.Zone.Id, DigestOptions.Recipient);
    }
}
