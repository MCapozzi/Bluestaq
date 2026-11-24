using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Bluestaq.Notes.Api.Data;

/// <summary>
/// Stamps who/when on every <see cref="IAuditable"/> entity being inserted or updated, so audit data
/// can't be forgotten or forged by callers. Created* fields are immutable after insert.
/// </summary>
public sealed class AuditInterceptor(ICurrentUserAccessor currentUser, TimeProvider clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        // Truncate to the microsecond precision MySQL DATETIME(6) stores, so values returned to callers match what's persisted.
        var utcNow = clock.GetUtcNow().UtcDateTime;
        var now = utcNow.AddTicks(-(utcNow.Ticks % TimeSpan.TicksPerMicrosecond));

        foreach (var entry in context.ChangeTracker.Entries<IAuditable>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    var creator = RequireUserId();
                    entry.Entity.CreatedByUserId = creator;
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedByUserId = creator;
                    entry.Entity.UpdatedAt = now;
                    if (entry.Entity is Note added)
                    {
                        (added.ContentUpdatedByUserId, added.ContentUpdatedAt) = (creator, now);
                    }

                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedByUserId = RequireUserId();
                    entry.Entity.UpdatedAt = now;
                    if (entry.Entity is Note note && (Changed(entry.Property(nameof(Note.Title))) || Changed(entry.Property(nameof(Note.Content)))))
                    {
                        (note.ContentUpdatedByUserId, note.ContentUpdatedAt) = (entry.Entity.UpdatedByUserId, now);
                    }

                    entry.Property(e => e.CreatedByUserId).IsModified = false;
                    entry.Property(e => e.CreatedAt).IsModified = false;
                    break;
            }
        }
    }

    private static bool Changed(Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry property) =>
        property.IsModified && !Equals(property.OriginalValue, property.CurrentValue);

    private long RequireUserId() =>
        currentUser.UserId ?? throw new InvalidOperationException("Cannot save auditable entities without an authenticated user.");
}
