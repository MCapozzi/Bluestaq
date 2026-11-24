using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Controllers;

/// <summary>Owner-only management of a note's visibility and the users it is shared with.</summary>
[ApiController]
[Route("api/notes/{id:long}")]
public sealed class NoteSharingController(NotesDbContext db, TimeProvider clock) : ControllerBase
{
    [HttpGet("sharing")]
    [ProducesResponseType<NoteSharingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        var (note, error) = await NoteAccessGuard.FindOwnedAsync(this, db, id, ct);
        return note is null ? error! : Ok(await ToResponseAsync(note, ct));
    }

    [HttpPut("visibility")]
    [ProducesResponseType<NoteSharingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetVisibility(long id, SetVisibilityRequest request, CancellationToken ct)
    {
        var (note, error) = await NoteAccessGuard.FindOwnedAsync(this, db, id, ct);
        if (note is null)
        {
            return error!;
        }

        note.Visibility = request.Visibility!.Value;
        await db.SaveChangesAsync(ct);
        return Ok(await ToResponseAsync(note, ct));
    }

    /// <summary>Grants a user read access. Idempotent.</summary>
    [HttpPost("shares")]
    [ProducesResponseType<NoteSharingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Share(long id, ShareNoteRequest request, CancellationToken ct)
    {
        var (note, error) = await NoteAccessGuard.FindOwnedAsync(this, db, id, ct);
        if (note is null)
        {
            return error!;
        }

        var target = await FindUserAsync(request.Username, ct);
        if (target is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown username.");
        }

        if (target.Id == note.OwnerId)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Owners already have access to their notes.");
        }

        if (!await db.NoteShares.AnyAsync(s => s.NoteId == id && s.UserId == target.Id, ct))
        {
            db.NoteShares.Add(new NoteShare { NoteId = id, UserId = target.Id, SharedAt = clock.GetUtcNow().UtcDateTime });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // A concurrent request created the same share; the outcome is identical.
            }
        }

        return Ok(await ToResponseAsync(note, ct));
    }

    /// <summary>Revokes a user's access. Idempotent.</summary>
    [HttpDelete("shares/{username}")]
    [ProducesResponseType<NoteSharingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unshare(long id, string username, CancellationToken ct)
    {
        var (note, error) = await NoteAccessGuard.FindOwnedAsync(this, db, id, ct);
        if (note is null)
        {
            return error!;
        }

        // Resolve the user first: MySQL rejects a DELETE that joins back to its own target table.
        var target = await FindUserAsync(username, ct);
        if (target is not null)
        {
            await db.NoteShares.Where(s => s.NoteId == id && s.UserId == target.Id).ExecuteDeleteAsync(ct);
        }

        return Ok(await ToResponseAsync(note, ct));
    }

    private Task<User?> FindUserAsync(string username, CancellationToken ct)
    {
        var normalized = Normalize(username);
        return db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Username == normalized, ct);
    }

    private static string Normalize(string username) => username.Trim().ToLowerInvariant();

    private async Task<NoteSharingResponse> ToResponseAsync(Note note, CancellationToken ct)
    {
        var shares = await db.NoteShares.AsNoTracking()
            .Where(s => s.NoteId == note.Id)
            .OrderBy(s => s.User!.Username)
            .Select(s => new NoteShareResponse(s.UserId, s.User!.Username, s.SharedAt))
            .ToListAsync(ct);
        return new NoteSharingResponse(note.Id, note.Visibility, shares);
    }
}
