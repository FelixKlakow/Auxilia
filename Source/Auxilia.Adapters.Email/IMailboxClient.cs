namespace Auxilia.Adapters.Email;

/// <summary>One inbound message; <see cref="MessageId"/> is the RFC 5322 Message-Id.</summary>
public sealed record InboundMail(
    string MessageId,
    string Subject,
    string From,
    string BodyText,
    /// <summary>Protocol-level handle used to flag the message as processed.</summary>
    uint Uid);

/// <summary>
/// Protocol abstraction over the mailbox: the real implementation speaks IMAP/SMTP via
/// MailKit; unit tests fake this interface.
/// </summary>
public interface IMailboxClient
{
    Task<IReadOnlyList<InboundMail>> FetchUnseenAsync(CancellationToken ct = default);

    /// <summary>Marks a message processed (\Seen) — the adapter's idempotency guard.</summary>
    Task MarkSeenAsync(uint uid, CancellationToken ct = default);

    /// <summary>Sends a reply on the thread of <paramref name="inReplyToMessageId"/>.</summary>
    Task SendReplyAsync(
        string to, string subject, string body, string inReplyToMessageId,
        CancellationToken ct = default);
}
