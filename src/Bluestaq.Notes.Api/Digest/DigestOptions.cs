using MailKit.Security;

namespace Bluestaq.Notes.Api.Digest;

public sealed class DigestOptions
{
    public const string SectionName = "Digest";

    /// <summary>
    /// Every digest is sent here instead of to the user (users have no email address in this project). The intended
    /// user is named in the subject and body.
    /// </summary>
    public const string Recipient = "TEST@TEST.TEST";

    /// <summary>Digests go out at midnight in this time zone and cover the day that just ended.</summary>
    public string TimeZone { get; set; } = "America/New_York";

    /// <summary>How long after midnight a digest may still be sent (covers restarts and failed-send retries).</summary>
    public TimeSpan SendWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How often the scheduler checks for digests to send.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    public string From { get; set; } = "notes@bluestaq.local";

    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class SmtpOptions
{
    /// <summary>SMTP server; digests are not sent while this is empty.</summary>
    public string Host { get; set; } = "";

    public int Port { get; set; } = 25;

    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.Auto;

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";
}
