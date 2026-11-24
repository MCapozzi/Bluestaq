using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Bluestaq.Notes.Api.Digest;

public sealed record EmailMessage(string To, string Subject, string TextBody);

public interface IEmailSender
{
    /// <summary>False when no mail server is configured, in which case nothing should be sent.</summary>
    bool IsConfigured { get; }

    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public sealed class SmtpEmailSender(IOptions<DigestOptions> options) : IEmailSender
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.Smtp.Host);

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var smtp = options.Value.Smtp;
        using var client = new SmtpClient();
        await client.ConnectAsync(smtp.Host, smtp.Port, smtp.Security, ct);
        if (!string.IsNullOrEmpty(smtp.Username))
        {
            await client.AuthenticateAsync(smtp.Username, smtp.Password, ct);
        }

        await client.SendAsync(ToMimeMessage(message, options.Value.From), ct);
        await client.DisconnectAsync(quit: true, ct);
    }

    public static MimeMessage ToMimeMessage(EmailMessage message, string from)
    {
        var mime = new MimeMessage { Subject = message.Subject, Body = new TextPart("plain") { Text = message.TextBody } };
        mime.From.Add(MailboxAddress.Parse(from));
        mime.To.Add(MailboxAddress.Parse(message.To));
        return mime;
    }
}
