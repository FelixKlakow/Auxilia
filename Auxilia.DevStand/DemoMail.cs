using Auxilia.SystemTestSuite.EndToEnd;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Auxilia.DevStand;

/// <summary>Sends the demo mail that triggers a Code Review run on the booted dev stand.</summary>
internal static class DemoMail
{
    public const string AdapterMailbox  = "workflows@localhost"; // the mailbox the email adapter polls
    public const string OperatorMailbox = "operator@localhost";  // demo mails are sent from here

    public static async Task SendAsync(string subject, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(OperatorMailbox));
        message.To.Add(MailboxAddress.Parse(AdapterMailbox));
        message.Subject = subject;
        message.Body = new TextPart("plain")
        {
            Text = "Dev-stand demo: please review the changes in PR-42."
        };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(
            EndToEndEnvironment.MailHost, EndToEndEnvironment.MappedSmtp, SecureSocketOptions.None, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }
}
