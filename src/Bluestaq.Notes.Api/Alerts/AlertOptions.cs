namespace Bluestaq.Notes.Api.Alerts;

public sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    /// <summary>Minimum time between two pushes to the same user; alerts in between are aggregated.</summary>
    public TimeSpan PushInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often connected users are checked for due alerts.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}
