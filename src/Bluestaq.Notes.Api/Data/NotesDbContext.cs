using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Bluestaq.Notes.Api.Data;

public sealed class NotesDbContext(DbContextOptions<NotesDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Session> Sessions => Set<Session>();

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

        modelBuilder.Entity<Note>(e =>
        {
            e.ToTable("notes");
            e.Property(n => n.Title).HasMaxLength(Contracts.ValidationLimits.TitleMaxLength).IsRequired();
            e.Property(n => n.Content).HasColumnType("mediumtext").IsRequired();
            e.HasOne(n => n.Owner).WithMany(u => u.Notes).HasForeignKey(n => n.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(n => new { n.OwnerId, n.UpdatedAt });
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
