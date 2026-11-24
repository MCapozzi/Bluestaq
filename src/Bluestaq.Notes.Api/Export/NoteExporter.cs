using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Export;

/// <summary>Resolves a <see cref="NoteExportRequest"/> into the matching notes the requester is allowed to read.</summary>
public sealed class NoteExporter(NotesDbContext db, TimeProvider clock)
{
    /// <remarks>
    /// Only notes readable by the requester (own, public, or shared with them) are ever returned. Requested IDs that
    /// don't exist or aren't readable are reported together in <see cref="NoteExportResponse.NotFoundIds"/>.
    /// </remarks>
    public async Task<NoteExportResponse> ExportAsync(long requesterId, NoteExportRequest request, CancellationToken ct)
    {
        var query = db.Notes.AsNoTracking().ReadableBy(requesterId);
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

        var notes = await query
            .OrderBy(n => n.CreatedAt).ThenBy(n => n.Id)
            .ToListAsync(ct);

        var found = notes.Select(n => n.Id).ToHashSet();
        var notFound = requestedIds.Where(id => !found.Contains(id)).ToList();

        return new NoteExportResponse(
            clock.GetUtcNow().UtcDateTime,
            notes.Count,
            notes.Select(NoteResponse.From).ToList(),
            notFound);
    }
}
