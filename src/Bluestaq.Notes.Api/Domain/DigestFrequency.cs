using System.Text.Json.Serialization;

namespace Bluestaq.Notes.Api.Domain;

/// <summary>How often a user is emailed a digest of notes shared with them. Users are opted out until they choose one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DigestFrequency>))]
public enum DigestFrequency
{
    None = 0,
    Daily = 1,
    Weekly = 2,
}
