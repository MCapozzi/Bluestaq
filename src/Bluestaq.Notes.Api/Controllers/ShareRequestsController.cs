using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Controllers;

/// <summary>
/// Editors request to share a note with more users; the note's owner (or an administrator) approves or rejects.
/// </summary>
[ApiController]
public sealed class ShareRequestsController(NotesDbContext db, TimeProvider clock) : ControllerBase
{
    /// <summary>Asks the owner to share the note with a user. Repeating a pending request returns it unchanged.</summary>
    [HttpPost("api/notes/{id:long}/share-requests")]
    [ProducesResponseType<ShareRequestResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ShareRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(long id, CreateShareRequestRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var isAdmin = User.IsAdministrator();
        var note = await db.Notes.AsNoTracking().VisibleTo(userId, isAdmin).SingleOrDefaultAsync(n => n.Id == id, ct);
        if (note is null)
        {
            return NotFound();
        }

        if (note.OwnerId == userId || isAdmin)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Owners and administrators can share the note directly.");
        }

        if (!await db.NoteShares.IsEditorAsync(id, userId, ct))
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only editors can request to share this note.");
        }

        var normalized = request.Username.Trim().ToLowerInvariant();
        var target = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Username == normalized, ct);
        if (target is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown username.");
        }

        var permission = request.Permission ?? SharePermission.Read;
        var current = await db.NoteShares.AsNoTracking()
            .Where(s => s.NoteId == id && s.UserId == target.Id)
            .Select(s => (SharePermission?)s.Permission)
            .SingleOrDefaultAsync(ct);
        if (target.Id == note.OwnerId || current >= permission)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "That user already has this access.");
        }

        var pending = await db.ShareRequests.AsNoTracking()
            .Where(r => r.NoteId == id && r.RequestedByUserId == userId && r.TargetUserId == target.Id
                && r.Permission == permission && r.Status == ShareRequestStatus.Pending)
            .Select(r => (long?)r.Id)
            .FirstOrDefaultAsync(ct);
        if (pending is { } existingId)
        {
            return Ok(await LoadAsync(existingId, ct));
        }

        var shareRequest = new ShareRequest
        {
            NoteId = id,
            RequestedByUserId = userId,
            TargetUserId = target.Id,
            Permission = permission,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.ShareRequests.Add(shareRequest);
        await db.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, await LoadAsync(shareRequest.Id, ct));
    }

    /// <summary>The note's share requests: all of them for the owner and administrators, an editor's own for editors.</summary>
    [HttpGet("api/notes/{id:long}/share-requests")]
    [ProducesResponseType<IReadOnlyList<ShareRequestResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListForNote(long id, [FromQuery] ShareRequestStatus? status, CancellationToken ct)
    {
        var (note, error) = await NoteAccessGuard.FindEditableAsync(this, db, id, ct);
        if (note is null)
        {
            return error!;
        }

        var userId = User.GetUserId();
        var requests = db.ShareRequests.AsNoTracking().Where(r => r.NoteId == id);
        if (note.OwnerId != userId && !User.IsAdministrator())
        {
            requests = requests.Where(r => r.RequestedByUserId == userId);
        }

        if (status is { } s)
        {
            requests = requests.Where(r => r.Status == s);
        }

        return Ok(await ToResponses(requests).ToListAsync(ct));
    }

    /// <summary>Pending share requests on notes the caller owns, across all of them.</summary>
    [HttpGet("api/notes/share-requests")]
    [ProducesResponseType<IReadOnlyList<ShareRequestResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PendingForMyNotes(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var requests = db.ShareRequests.AsNoTracking()
            .Where(r => r.Status == ShareRequestStatus.Pending && r.Note!.OwnerId == userId);
        return Ok(await ToResponses(requests).ToListAsync(ct));
    }

    /// <summary>Approves a pending request and creates (or upgrades) the share. The requester must still be an editor.</summary>
    [HttpPost("api/notes/{id:long}/share-requests/{requestId:long}/approve")]
    [ProducesResponseType<ShareRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Approve(long id, long requestId, CancellationToken ct) =>
        DecideAsync(id, requestId, ShareRequestStatus.Approved, ct);

    /// <summary>Rejects a pending request; no share is created.</summary>
    [HttpPost("api/notes/{id:long}/share-requests/{requestId:long}/reject")]
    [ProducesResponseType<ShareRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Reject(long id, long requestId, CancellationToken ct) =>
        DecideAsync(id, requestId, ShareRequestStatus.Rejected, ct);

    private async Task<IActionResult> DecideAsync(long id, long requestId, ShareRequestStatus decision, CancellationToken ct)
    {
        var (note, error) = await NoteAccessGuard.FindOwnedAsync(this, db, id, ct);
        if (note is null)
        {
            return error!;
        }

        var shareRequest = await db.ShareRequests.SingleOrDefaultAsync(r => r.Id == requestId && r.NoteId == id, ct);
        if (shareRequest is null)
        {
            return NotFound();
        }

        if (shareRequest.Status != ShareRequestStatus.Pending)
        {
            return Conflict($"This request was already {shareRequest.Status.ToString().ToLowerInvariant()}.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (decision == ShareRequestStatus.Approved)
        {
            if (!await db.NoteShares.IsEditorAsync(id, shareRequest.RequestedByUserId, ct))
            {
                return Conflict("The requester is no longer an editor of this note; reject the request instead.");
            }

            var share = await db.NoteShares.SingleOrDefaultAsync(s => s.NoteId == id && s.UserId == shareRequest.TargetUserId, ct);
            if (share is null)
            {
                db.NoteShares.Add(new NoteShare
                {
                    NoteId = id,
                    UserId = shareRequest.TargetUserId,
                    SharedAt = now,
                    SharedByUserId = shareRequest.RequestedByUserId,
                    Permission = shareRequest.Permission,
                });
            }
            else if (share.Permission < shareRequest.Permission)
            {
                share.Permission = shareRequest.Permission;
            }
        }

        shareRequest.Status = decision;
        shareRequest.DecidedByUserId = User.GetUserId();
        shareRequest.DecidedAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another decision on this request, or a concurrent share for the same user, won the race.
            return Conflict("The request or the note's shares changed at the same time; reload and try again.");
        }

        return Ok(await LoadAsync(requestId, ct));
    }

    private ObjectResult Conflict(string title) => Problem(statusCode: StatusCodes.Status409Conflict, title: title);

    private Task<ShareRequestResponse> LoadAsync(long requestId, CancellationToken ct) =>
        ToResponses(db.ShareRequests.AsNoTracking().Where(r => r.Id == requestId)).SingleAsync(ct);

    private static IQueryable<ShareRequestResponse> ToResponses(IQueryable<ShareRequest> requests) =>
        requests
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Select(r => new ShareRequestResponse(
                r.Id, r.NoteId, r.Note!.Title, r.RequestedByUserId, r.RequestedBy!.Username, r.TargetUserId, r.TargetUser!.Username,
                r.Permission, r.Status, r.CreatedAt, r.DecidedByUserId, r.DecidedAt));
}
