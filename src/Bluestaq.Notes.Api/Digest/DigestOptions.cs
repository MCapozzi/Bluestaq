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

    /// <summary>How often users are checked for a due digest.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);

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
