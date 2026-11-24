namespace Bluestaq.Notes.Api.Auth;

public sealed class SessionAuthOptions
{
    public const string SectionName = "Sessions";

    /// <summary>How long a session stays valid without activity.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(8);

    public string CookieName { get; set; } = "notes_session";
}
