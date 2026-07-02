using Microsoft.Extensions.Options;

namespace Auxilia.Adapters.Email;

/// <summary>Creates one mailbox client per configured mailbox (triggers poll many mailboxes).</summary>
public interface IMailboxClientFactory
{
    IMailboxClient Create(EmailTaskSourceSettings settings);
}

public sealed class MailKitMailboxClientFactory : IMailboxClientFactory
{
    public IMailboxClient Create(EmailTaskSourceSettings settings)
        => new MailKitMailboxClient(Options.Create(settings));
}
