using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Auxilia.Adapters.Email;

/// <summary>
/// Real IMAP/SMTP implementation. A connection is opened per operation — mailbox polling is
/// low-frequency and short-lived connections sidestep IMAP idle-timeout edge cases.
/// </summary>
public sealed class MailKitMailboxClient(IOptions<EmailTaskSourceSettings> options) : IMailboxClient
{
    public async Task<IReadOnlyList<InboundMail>> FetchUnseenAsync(CancellationToken ct = default)
    {
        var settings = options.Value;
        using var client = new ImapClient();
        await client.ConnectAsync(settings.ImapHost, settings.ImapPort,
            settings.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None, ct);
        await client.AuthenticateAsync(settings.Username, settings.Password, ct);

        var folder = await client.GetFolderAsync(settings.Folder, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        var uids = await folder.SearchAsync(SearchQuery.NotSeen, ct);
        var result = new List<InboundMail>(uids.Count);
        foreach (var uid in uids)
        {
            var message = await folder.GetMessageAsync(uid, ct);
            result.Add(new InboundMail(
                message.MessageId ?? uid.Id.ToString(),
                message.Subject ?? string.Empty,
                message.From.ToString(),
                message.TextBody ?? message.HtmlBody ?? string.Empty,
                uid.Id));
        }

        await client.DisconnectAsync(quit: true, ct);
        return result;
    }

    public async Task MarkSeenAsync(uint uid, CancellationToken ct = default)
    {
        var settings = options.Value;
        using var client = new ImapClient();
        await client.ConnectAsync(settings.ImapHost, settings.ImapPort,
            settings.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None, ct);
        await client.AuthenticateAsync(settings.Username, settings.Password, ct);

        var folder = await client.GetFolderAsync(settings.Folder, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        await folder.AddFlagsAsync(new UniqueId(uid), MessageFlags.Seen, silent: true, ct);
        await client.DisconnectAsync(quit: true, ct);
    }

    public async Task SendReplyAsync(
        string to, string subject, string body, string inReplyToMessageId,
        CancellationToken ct = default)
    {
        var settings = options.Value;
        if (string.IsNullOrEmpty(settings.SmtpHost))
            throw new InvalidOperationException("SMTP is not configured for this mailbox.");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(settings.Username));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? subject
            : $"Re: {subject}";
        message.InReplyTo = inReplyToMessageId;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort,
            SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrEmpty(settings.Username))
            await client.AuthenticateAsync(settings.Username, settings.Password, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}
