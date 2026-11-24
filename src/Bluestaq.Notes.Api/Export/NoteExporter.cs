using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Export;

/// <summary>Resolves a <see cref="NoteExportRequest"/> into the requesting user's matching notes.</summary>
public sealed class NoteExporter(NotesDbContext db, TimeProvider clock)
{
    /// <remarks>
    /// Only the requester's own notes are ever returned. IDs that don't exist or belong to another user
    /// are reported together in <see cref="NoteExportResponse.NotFoundIds"/> so ownership isn't disclosed.
    /// </remarks>
    public async Task<NoteExportResponse> ExportAsync(long requesterId, NoteExportRequest request, CancellationToken ct)
    {
        var query = db.Notes.AsNoTracking().Where(n => n.OwnerId == requesterId);
        long[] requestedIds = [];

        if (request.NoteIds is not null)
        {
            requestedIds = request.NoteIds.Distinct().ToArray();
            query = query.Where(n => requestedIds.Contains(n.Id));
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
