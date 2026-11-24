using System.Text.Json.Serialization;

namespace Bluestaq.Notes.Api.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<UserRole>))]
public enum UserRole
{
    User = 0,

    /// <summary>Full access to every note, regardless of owner or sharing. All requests are audit-logged.</summary>
    Administrator = 1,
}
