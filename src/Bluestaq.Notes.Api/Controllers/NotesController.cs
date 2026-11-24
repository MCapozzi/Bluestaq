using System.ComponentModel.DataAnnotations;
using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Bluestaq.Notes.Api.Export;
using Bluestaq.Notes.Api.Import;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Controllers;

/// <summary>
/// Notes CRUD. Callers can read their own, public, and shared-with-them notes; only owners can modify them.
/// Notes the caller can't read are reported as 404 so their existence is not disclosed.
/// </summary>
[ApiController]
[Route("api/notes")]
public sealed class NotesController(NotesDbContext db, NoteExporter exporter, NoteImporter importer) : ControllerBase
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
            .Select(n => new NoteResponse(
                n.Id, n.OwnerId, n.Title, n.Content, n.CreatedByUserId, n.CreatedAt, n.UpdatedByUserId, n.UpdatedAt, n.Visibility))
            .ToListAsync(ct);

        return Ok(new PagedResponse<NoteResponse>(items, page, pageSize, total));
    }

    /// <summary>Other users' notes the caller can read: shared with them directly, or public.</summary>
    [HttpGet("shared")]
    [ProducesResponseType<PagedResponse<NoteResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListShared(
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, ValidationLimits.MaxPageSize)] int pageSize = 20,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        var query = db.Notes.AsNoTracking().ReadableBy(userId).Where(n => n.OwnerId != userId);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(n => n.UpdatedAt).ThenByDescending(n => n.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new NoteResponse(
                n.Id, n.OwnerId, n.Title, n.Content, n.CreatedByUserId, n.CreatedAt, n.UpdatedByUserId, n.UpdatedAt, n.Visibility))
            .ToListAsync(ct);

        return Ok(new PagedResponse<NoteResponse>(items, page, pageSize, total));
    }

    [HttpGet("{id:long}")]
    [ProducesResponseType<NoteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        var note = await db.Notes.AsNoTracking().VisibleTo(User.GetUserId(), User.IsAdministrator()).SingleOrDefaultAsync(n => n.Id == id, ct);
        return note is null ? NotFound() : Ok(NoteResponse.From(note));
    }

    [HttpPost]
    [ProducesResponseType<NoteResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(NoteRequest request, CancellationToken ct)
    {
        // Audit fields (created/updated by and at) are stamped by AuditInterceptor.
        var note = new Note
        {
            OwnerId = User.GetUserId(),
            Title = request.Title,
            Content = request.Content,
        };
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = note.Id }, NoteResponse.From(note));
    }

    [HttpPut("{id:long}")]
    [ProducesResponseType<NoteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(long id, NoteRequest request, CancellationToken ct)
    {
        var (note, error) = await NoteAccessGuard.FindOwnedAsync(this, db, id, ct);
        if (note is null)
        {
            return error!;
        }

        note.Title = request.Title;
        note.Content = request.Content;

        // PUT is a full replacement: write every field so overlapping updates can't interleave fields.
        var entry = db.Entry(note);
        entry.Property(n => n.Title).IsModified = true;
        entry.Property(n => n.Content).IsModified = true;
        await db.SaveChangesAsync(ct);

        return Ok(NoteResponse.From(note));
    }

    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        var (note, error) = await NoteAccessGuard.FindOwnedAsync(this, db, id, ct);
        if (note is null)
        {
            return error!;
        }

        db.Notes.Remove(note);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Bulk-exports notes the caller can read, selected by exactly one of: <c>noteIds</c>, <c>userId</c>, or a
    /// <c>from</c>/<c>to</c> creation-date range. Returns JSON, or a CSV file when <c>format</c> is <c>csv</c>.
    /// </summary>
    [HttpPost("export")]
    // One declaration per status code: MVC keeps only the last one, so both content types go on the same attribute.
    [ProducesResponseType<NoteExportResponse>(StatusCodes.Status200OK, "application/json", "text/csv")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Export(NoteExportRequest request, CancellationToken ct)
    {
        NoteExportResponse export;
        try
        {
            export = await exporter.ExportAsync(User.GetUserId(), request, ct, User.IsAdministrator());
        }
        catch (ExportLimitExceededException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Export too large.",
                detail: $"{ex.Message} Narrow the selection (fewer IDs or a shorter date range).");
        }

        if (request.Format == ExportFormat.Csv)
        {
            var csv = System.Text.Encoding.UTF8.GetBytes(NoteCsvWriter.Write(export.Notes));
            return File(csv, "text/csv; charset=utf-8", $"notes-export-{export.ExportedAt:yyyyMMddTHHmmssZ}.csv");
        }

        return Ok(export);
    }

    /// <summary>
    /// Bulk-imports notes from an export document (JSON body) as new notes owned by the caller. Imports are
    /// all-or-nothing: any invalid note rejects the whole request with 400.
    /// </summary>
    [HttpPost("import")]
    [Consumes("application/json")]
    [ProducesResponseType<NoteImportResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<IActionResult> Import(NoteImportRequest request, CancellationToken ct) => ImportCoreAsync(() => request.Notes!, ct);

    /// <summary>Bulk-imports notes from a CSV export (<c>text/csv</c> body), with the same rules as the JSON import.</summary>
    [HttpPost("import")]
    [Consumes("text/csv")]
    [ProducesResponseType<NoteImportResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportCsv(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8);
        var csv = await reader.ReadToEndAsync(ct);
        return await ImportCoreAsync(() => NoteCsvReader.Read(csv), ct);
    }

    private async Task<IActionResult> ImportCoreAsync(Func<IReadOnlyList<ImportedNote?>> notes, CancellationToken ct)
    {
        try
        {
            var result = await importer.ImportAsync(User.GetUserId(), notes(), ct);
            return StatusCode(StatusCodes.Status201Created, result);
        }
        catch (NoteImportException ex)
        {
            foreach (var (key, messages) in ex.Errors)
            {
                foreach (var message in messages)
                {
                    ModelState.AddModelError(key, message);
                }
            }

            return ValidationProblem(ModelState);
        }
    }

    private IQueryable<Note> OwnedNotes()
    {
        var userId = User.GetUserId();
        return db.Notes.Where(n => n.OwnerId == userId);
    }
}
