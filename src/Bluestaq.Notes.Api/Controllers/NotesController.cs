using System.ComponentModel.DataAnnotations;
using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Controllers;

/// <summary>
/// CRUD for the authenticated user's notes. Notes owned by other users are reported as 404
/// so their existence is not disclosed.
/// </summary>
[ApiController]
[Route("api/notes")]
public sealed class NotesController(NotesDbContext db, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResponse<NoteResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, ValidationLimits.MaxPageSize)] int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = OwnedNotes().AsNoTracking();
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(n => n.UpdatedAt).ThenByDescending(n => n.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new NoteResponse(n.Id, n.Title, n.Content, n.CreatedAt, n.UpdatedAt))
            .ToListAsync(ct);

        return Ok(new PagedResponse<NoteResponse>(items, page, pageSize, total));
    }

    [HttpGet("{id:long}")]
    [ProducesResponseType<NoteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        var note = await OwnedNotes().AsNoTracking().SingleOrDefaultAsync(n => n.Id == id, ct);
        return note is null ? NotFound() : Ok(NoteResponse.From(note));
    }

    [HttpPost]
    [ProducesResponseType<NoteResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(NoteRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var note = new Note
        {
            OwnerId = User.GetUserId(),
            Title = request.Title,
            Content = request.Content,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = note.Id }, NoteResponse.From(note));
    }

    [HttpPut("{id:long}")]
    [ProducesResponseType<NoteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(long id, NoteRequest request, CancellationToken ct)
    {
        var note = await OwnedNotes().SingleOrDefaultAsync(n => n.Id == id, ct);
        if (note is null)
        {
            return NotFound();
        }

        note.Title = request.Title;
        note.Content = request.Content;
        note.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        return Ok(NoteResponse.From(note));
    }

    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        var deleted = await OwnedNotes().Where(n => n.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? NotFound() : NoContent();
    }

    private IQueryable<Note> OwnedNotes()
    {
        var userId = User.GetUserId();
        return db.Notes.Where(n => n.OwnerId == userId);
    }
}
