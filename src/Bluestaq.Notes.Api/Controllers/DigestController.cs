using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Digest;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Controllers;

/// <summary>The caller's opt-in to emailed digests of notes shared with them.</summary>
[ApiController]
[Route("api/digest")]
public sealed class DigestController(NotesDbContext db, DigestService digests, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<DigestSettingsResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(ToResponse(await CurrentUserAsync(ct)));

    /// <summary>Opts in (<c>Daily</c> or <c>Weekly</c>) or out (<c>None</c>). Opting out resets the digest window.</summary>
    [HttpPut]
    [ProducesResponseType<DigestSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update(UpdateDigestRequest request, CancellationToken ct)
    {
        // Written directly, not via change tracking: a digest claimed after the user was loaded must still be cleared.
        var frequency = request.Frequency!.Value;
        var users = db.Users.Where(u => u.Id == User.GetUserId());
        await (frequency == DigestFrequency.None
            ? users.ExecuteUpdateAsync(s => s.SetProperty(u => u.DigestFrequency, frequency).SetProperty(u => u.DigestLastSentAt, (DateTime?)null), ct)
            : users.ExecuteUpdateAsync(s => s.SetProperty(u => u.DigestFrequency, frequency), ct));
        return Ok(ToResponse(await CurrentUserAsync(ct)));
    }

    /// <summary>What the next digest would contain right now, without sending it.</summary>
    [HttpGet("preview")]
    [ProducesResponseType<NoteDigest>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview(CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        return Ok(await digests.BuildAsync(
            user.Id, user.Username, user.DigestFrequency, user.DigestLastSentAt, clock.GetUtcNow().UtcDateTime, ct));
    }

    private Task<User> CurrentUserAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        return db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
    }

    private DigestSettingsResponse ToResponse(User user)
    {
        var next = DigestService.PeriodOf(user.DigestFrequency) is { } period
            ? user.DigestLastSentAt + period ?? clock.GetUtcNow().UtcDateTime
            : (DateTime?)null;
        return new DigestSettingsResponse(user.DigestFrequency, user.DigestLastSentAt, next, DigestOptions.Recipient);
    }
}
