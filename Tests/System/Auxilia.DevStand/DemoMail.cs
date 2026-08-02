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

    /// <summary>Fillable session-mail document, searched upwards from the exe (repo root).</summary>
    public const string SessionMailFileName = "demo-session-mail.md";

    public static Task SendAsync(string subject, CancellationToken ct)
        => SendAsync(subject, "Dev-stand demo: please review the changes in PR-42.", ct);

    public static async Task SendAsync(string subject, string body, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(OperatorMailbox));
        message.To.Add(MailboxAddress.Parse(AdapterMailbox));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(
            EndToEndEnvironment.MailHost, EndToEndEnvironment.MappedSmtp, SecureSocketOptions.None, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }

    /// <summary>
    /// The session mail from <see cref="SessionMailFileName"/>: first line "Subject: …", then
    /// a blank line, then the body (= the session's instruction). "{n}" becomes the counter.
    /// Falls back to a built-in mail when the document is absent or empty.
    /// </summary>
    public static (string Subject, string Body, string? Source) LoadSessionMail(int counter)
    {
        var path = FindUpwards(SessionMailFileName);
        if (path is not null)
        {
            var lines = File.ReadAllLines(path);
            var subjectLine = lines.FirstOrDefault(l => l.TrimStart().StartsWith("Subject:", StringComparison.OrdinalIgnoreCase));
            if (subjectLine is not null)
            {
                var subject = subjectLine[(subjectLine.IndexOf(':') + 1)..].Trim();
                var body = string.Join('\n', lines
                        .SkipWhile(l => !ReferenceEquals(l, subjectLine))
                        .Skip(1)
                        .SkipWhile(string.IsNullOrWhiteSpace))
                    .TrimEnd();
                if (subject.Length > 0 && body.Length > 0)
                    return (subject.Replace("{n}", counter.ToString()),
                            body.Replace("{n}", counter.ToString()), path);
            }
        }

        return ($"session #{counter}: live coding",
                "Check out the repository and look around.", null);
    }

    private static string? FindUpwards(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
