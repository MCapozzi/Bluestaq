namespace Bluestaq.Notes.Api.Domain;

/// <summary>An entity whose audit fields are stamped automatically by <see cref="Data.AuditInterceptor"/> on save.</summary>
public interface IAuditable
{
    long CreatedByUserId { get; set; }
    DateTime CreatedAt { get; set; }
    long UpdatedByUserId { get; set; }
    DateTime UpdatedAt { get; set; }
}
