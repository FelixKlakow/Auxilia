using Auxilia.Adapters.Email;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Auxilia.SystemTestSuite.EmailAdapter;

/// <summary>
/// Exercises the REAL <see cref="MailKitMailboxClient"/> against the containerized GreenMail
/// server: real IMAP fetch/flag round-trip and real SMTP reply write-back, no cost.
/// </summary>
[TestFixture]
[Category("System")]
public class EmailAdapterSystemTests
{
    // GreenMail auto-creates accounts on delivery/login (auth disabled), but the login must
    // equal the mailbox address — a domain-less login would create a separate empty account.
    private const string AdapterMailbox = "test@localhost";
    private const string AdapterPassword = "pw";

    private EmailTaskSourceSettings _settings = null!;
    private MailKitMailboxClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _settings = new EmailTaskSourceSettings
        {
            ImapHost = EmailAdapterEnvironment.Host,
            ImapPort = EmailAdapterEnvironment.MappedImap,
            UseSsl = false,
            Username = AdapterMailbox,
            Password = AdapterPassword,
            SmtpHost = EmailAdapterEnvironment.Host,
            SmtpPort = EmailAdapterEnvironment.MappedSmtp,
            Folder = "INBOX"
        };
        _client = new MailKitMailboxClient(Options.Create(_settings));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task InboundMail_IsFetchedUnseen_AndMarkSeenRemovesIt(
        CancellationToken cancellationToken)
    {
        const string subject = "System test inquiry";
        const string body = "Please triage this mail.";
        await SendMailAsync("alice@example.com", AdapterMailbox, subject, body, cancellationToken);

        var mail = await WaitForUnseenAsync(m => m.Subject == subject, cancellationToken);
        Assert.Multiple(() =>
        {
            Assert.That(mail.Subject, Is.EqualTo(subject));
            Assert.That(mail.From, Does.Contain("alice@example.com"));
            Assert.That(mail.BodyText, Does.Contain(body));
        });

        await _client.MarkSeenAsync(mail.Uid, cancellationToken);

        var unseenAfter = await _client.FetchUnseenAsync(cancellationToken);
        Assert.That(unseenAfter.Where(m => m.Subject == subject), Is.Empty,
            "A seen message must no longer be fetched as unseen.");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task SendReply_ArrivesInRecipientMailboxWithReSubject(
        CancellationToken cancellationToken)
    {
        const string recipient = "someone@localhost";
        const string subject = "Status update";

        await _client.SendReplyAsync(
            recipient, subject, "All green.", "<original-1@example.com>", cancellationToken);

        var reply = await WaitForMessageAsync(
            recipient, m => m.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase),
            cancellationToken);
        Assert.Multiple(() =>
        {
            Assert.That(reply.Subject, Does.StartWith("Re:"));
            Assert.That(reply.Subject, Does.Contain(subject));
            Assert.That(reply.TextBody, Does.Contain("All green."));
        });
    }

    private async Task SendMailAsync(
        string from, string to, string subject, string body, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(
            _settings.SmtpHost, _settings.SmtpPort, SecureSocketOptions.None, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }

    private async Task<InboundMail> WaitForUnseenAsync(
        Func<InboundMail, bool> predicate, CancellationToken ct)
    {
        while (true)
        {
            var unseen = await _client.FetchUnseenAsync(ct);
            var match = unseen.FirstOrDefault(predicate);
            if (match is not null)
                return match;
            await Task.Delay(250, ct);
        }
    }

    /// <summary>Polls the recipient's GreenMail INBOX (any login works — auth is disabled).</summary>
    private async Task<MimeMessage> WaitForMessageAsync(
        string mailbox, Func<MimeMessage, bool> predicate, CancellationToken ct)
    {
        while (true)
        {
            using var imap = new ImapClient();
            await imap.ConnectAsync(
                _settings.ImapHost, _settings.ImapPort, SecureSocketOptions.None, ct);
            await imap.AuthenticateAsync(mailbox, AdapterPassword, ct);
            var inbox = imap.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

            for (var i = 0; i < inbox.Count; i++)
            {
                var message = await inbox.GetMessageAsync(i, ct);
                if (predicate(message))
                {
                    await imap.DisconnectAsync(quit: true, ct);
                    return message;
                }
            }

            await imap.DisconnectAsync(quit: true, ct);
            await Task.Delay(250, ct);
        }
    }
}
