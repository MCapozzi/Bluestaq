namespace Bluestaq.Notes.Api.Alerts;

public sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    /// <summary>Minimum time between two pushes to the same user; alerts in between are aggregated.</summary>
    public TimeSpan PushInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often connected users are checked for due alerts.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A socket that doesn't accept a push within this time is dropped, so it can't stall other recipients.</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Browser origins, besides the API's own, allowed to open a cookie-authenticated alert socket
    /// (e.g. <c>https://app.example.com</c>).
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];
}
