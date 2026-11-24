using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Controllers;

internal static class NoteAccessGuard
{
    /// <summary>
    /// Loads a note (tracked) for an owner-only operation. Returns 404 if the caller can't read it, 403 if they can
    /// read it but don't own it.
    /// </summary>
    public static async Task<(Note? Note, IActionResult? Error)> FindOwnedAsync(
        ControllerBase controller, NotesDbContext db, long id, CancellationToken ct)
    {
        var userId = controller.User.GetUserId();
        var note = await db.Notes.ReadableBy(userId).SingleOrDefaultAsync(n => n.Id == id, ct);
        if (note is null)
        {
            return (null, controller.NotFound());
        }

        if (note.OwnerId != userId)
        {
            return (null, controller.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the note's owner can do this."));
        }

        return (note, null);
    }
}
