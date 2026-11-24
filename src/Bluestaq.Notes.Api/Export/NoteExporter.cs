using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.Export;

/// <summary>Resolves a <see cref="NoteExportRequest"/> into the matching notes the requester is allowed to read.</summary>
public sealed class NoteExporter(NotesDbContext db, TimeProvider clock, IOptions<ExportOptions> options)
{
    /// <remarks>
    /// Only notes readable by the requester (own, public, or shared with them; all notes for administrators) are returned. Requested IDs that
    /// don't exist or aren't readable are reported together in <see cref="NoteExportResponse.NotFoundIds"/>.
    /// Throws <see cref="ExportLimitExceededException"/> rather than buffering more than
    /// <see cref="ExportOptions.MaxNotes"/> notes.
    /// </remarks>
    public async Task<NoteExportResponse> ExportAsync(
        long requesterId, NoteExportRequest request, CancellationToken ct, bool isAdministrator = false)
    {
        var query = db.Notes.AsNoTracking().VisibleTo(requesterId, isAdministrator);
        long[] requestedIds = [];

        if (request.NoteIds is not null)
        {
            requestedIds = request.NoteIds.Distinct().ToArray();
            query = query.Where(n => requestedIds.Contains(n.Id));
        }
        else if (request.UserId is { } ownerId)
        {
            query = query.Where(n => n.OwnerId == ownerId);
        }
        else if (request.From is { } from && request.To is { } to)
        {
            var fromUtc = NoteExportRequest.ToUtc(from);
            var toUtc = NoteExportRequest.ToUtc(to);
            query = query.Where(n => n.CreatedAt >= fromUtc && n.CreatedAt <= toUtc);
        }

        var maxNotes = options.Value.MaxNotes;
        var notes = await query
            .OrderBy(n => n.CreatedAt).ThenBy(n => n.Id)
            .Take(maxNotes + 1)
            .ToListAsync(ct);
        if (notes.Count > maxNotes)
        {
            throw new ExportLimitExceededException(maxNotes);
        }

        var found = notes.Select(n => n.Id).ToHashSet();
        var notFound = requestedIds.Where(id => !found.Contains(id)).ToList();

        return new NoteExportResponse(
            clock.GetUtcNow().UtcDateTime,
            notes.Count,
            notes.Select(NoteResponse.From).ToList(),
            notFound);
    }
}
