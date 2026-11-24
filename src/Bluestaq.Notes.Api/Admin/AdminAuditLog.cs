using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;

namespace Bluestaq.Notes.Api.Admin;

public interface IAdminAuditLog
{
    Task RecordAsync(AdminAuditEntry entry, CancellationToken ct);
}

/// <summary>Records each admin action to both the external audit file and the <c>admin_audit_log</c> table.</summary>
public sealed class AdminAuditLog(
    AdminAuditFileLog file,
    IServiceScopeFactory scopes,
    ILogger<AdminAuditLog> logger) : IAdminAuditLog
{
    public async Task RecordAsync(AdminAuditEntry entry, CancellationToken ct)
    {
        file.Write(entry);
        try
        {
            // A fresh context so pending/failed changes from the request itself are never flushed with the entry.
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotesDbContext>();
            db.AdminAuditLog.Add(entry);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The response has already been produced; the file entry still exists, so surface the gap loudly.
            logger.LogError(ex, "Failed to persist admin audit entry for {Action} by user {AdminUserId}", entry.Action, entry.AdminUserId);
        }
    }
}
