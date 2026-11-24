using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Bluestaq.Notes.Api.Data;

public sealed class NotesDbContext(DbContextOptions<NotesDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<NoteShare> NoteShares => Set<NoteShare>();
    public DbSet<AdminAuditEntry> AdminAuditLog => Set<AdminAuditEntry>();
    public DbSet<NoteAlert> NoteAlerts => Set<NoteAlert>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // MySQL DATETIME has no time zone; all timestamps are written as UTC, so mark them UTC on read.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.Property(u => u.Username).HasMaxLength(Contracts.ValidationLimits.UsernameMaxLength).IsRequired();
            e.Property(u => u.PasswordHash).HasMaxLength(255).IsRequired();
            e.HasIndex(u => u.Username).IsUnique();
        });

        modelBuilder.Entity<AdminAuditEntry>(e =>
        {
            e.ToTable("admin_audit_log");
            e.Property(a => a.AdminUsername).HasMaxLength(Contracts.ValidationLimits.UsernameMaxLength).IsRequired();
            e.Property(a => a.Action).HasMaxLength(100).IsRequired();
            e.Property(a => a.HttpMethod).HasMaxLength(10).IsRequired();
            e.Property(a => a.Path).HasMaxLength(500).IsRequired();
            e.Property(a => a.QueryString).HasMaxLength(1000);
            e.Property(a => a.ClientIp).HasMaxLength(45);
            e.Property(a => a.TraceId).HasMaxLength(100);
            e.HasIndex(a => new { a.AdminUserId, a.OccurredAt });
            e.HasIndex(a => a.TargetNoteId);
        });

        modelBuilder.Entity<Note>(e =>
        {
            e.ToTable("notes");
            e.Property(n => n.Title).HasMaxLength(Contracts.ValidationLimits.TitleMaxLength).IsRequired();
            e.Property(n => n.Content).HasColumnType("mediumtext").IsRequired();
            e.HasOne(n => n.Owner).WithMany(u => u.Notes).HasForeignKey(n => n.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(n => new { n.OwnerId, n.UpdatedAt });
            e.HasIndex(n => new { n.OwnerId, n.CreatedAt });
            e.HasIndex(n => n.Visibility);
        });

        modelBuilder.Entity<NoteShare>(e =>
        {
            e.ToTable("note_shares");
            e.HasKey(s => new { s.NoteId, s.UserId });
            e.HasOne(s => s.Note).WithMany(n => n.Shares).HasForeignKey(s => s.NoteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.UserId);
        });

        modelBuilder.Entity<NoteAlert>(e =>
        {
            e.ToTable("note_alerts");
            // No FK to notes: deleting a note must not erase the delivered rows that throttle pushes. AlertInterceptor removes
            // a deleted note's pending alerts, and AlertService drops any for notes that are otherwise no longer readable.
            e.HasIndex(a => a.NoteId);
            e.HasOne<User>().WithMany().HasForeignKey(a => a.RecipientUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => new { a.RecipientUserId, a.DeliveredAt });
        });

        modelBuilder.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.Property(s => s.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
            e.HasIndex(s => s.TokenHash).IsUnique();
            e.HasIndex(s => s.ExpiresAt);
            e.HasOne(s => s.User).WithMany(u => u.Sessions).HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

internal sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
