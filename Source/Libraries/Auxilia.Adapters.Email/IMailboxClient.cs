namespace Auxilia.Adapters.Email;

/// <summary>One inbound message; <see cref="MessageId"/> is the RFC 5322 Message-Id.</summary>
public sealed record InboundMail(
    string MessageId,
    string Subject,
    string From,
    string BodyText,
    /// <summary>Protocol-level handle used to flag the message as processed.</summary>
    uint Uid);

/// <summary>One file attached to an inbound mail; the content stays in memory, never logged.</summary>
public sealed record MailAttachment(string FileName, byte[] Content);

/// <summary>
/// Protocol abstraction over the mailbox: the real implementation speaks IMAP/SMTP via
/// MailKit; unit tests fake this interface.
/// </summary>
public interface IMailboxClient
{
    Task<IReadOnlyList<InboundMail>> FetchUnseenAsync(CancellationToken ct = default);

    /// <summary>The attachments of one message, fetched by its protocol UID; empty when none.</summary>
    Task<IReadOnlyList<MailAttachment>> FetchAttachmentsAsync(uint uid, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MailAttachment>>([]);

    /// <summary>Marks a message processed (\Seen) — the adapter's idempotency guard.</summary>
    Task MarkSeenAsync(uint uid, CancellationToken ct = default);

    /// <summary>Sends a reply on the thread of <paramref name="inReplyToMessageId"/>.</summary>
    Task SendReplyAsync(
        string to, string subject, string body, string inReplyToMessageId,
        CancellationToken ct = default);
}
