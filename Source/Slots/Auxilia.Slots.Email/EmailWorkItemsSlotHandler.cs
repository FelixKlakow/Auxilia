using Auxilia.Adapters.Email;
using Auxilia.Workflows;
using Auxilia.Workflows.TaskSource;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Auxilia.Slots.Email;

/// <summary>
/// Slot handler for provider type "email-work-items": backs the "work-items" slot with an
/// IMAP/SMTP mailbox so the triggering mail is the work item and comments become mail replies.
/// </summary>
public sealed class EmailWorkItemsSlotHandler : ISlotHandler
{
    /// <summary>Seam for tests: maps the slot settings to the mailbox client.</summary>
    internal Func<EmailTaskSourceSettings, IMailboxClient> MailboxClientFactory { get; set; } =
        settings => new MailKitMailboxClient(Options.Create(settings));

    public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
    {
        switch (slotName)
        {
            case "work-items":
                var settings = SettingsFrom(configuration.Settings);
                var factory = MailboxClientFactory;
                services.AddScoped<IWorkItemAccess>(_ => new EmailWorkItemAccess(factory(settings)));
                break;

            default:
                throw new InvalidOperationException($"Unknown slot: {slotName}");
        }
    }

    private static EmailTaskSourceSettings SettingsFrom(IReadOnlyDictionary<string, string> settings)
        => new()
        {
            ImapHost = settings.GetValueOrDefault("ImapHost", string.Empty),
            ImapPort = int.TryParse(settings.GetValueOrDefault("ImapPort"), out var imapPort) ? imapPort : 993,
            UseSsl = !bool.TryParse(settings.GetValueOrDefault("UseSsl"), out var useSsl) || useSsl,
            Username = settings.GetValueOrDefault("Username", string.Empty),
            Password = settings.GetValueOrDefault("Password", string.Empty),
            SmtpHost = settings.GetValueOrDefault("SmtpHost", string.Empty),
            SmtpPort = int.TryParse(settings.GetValueOrDefault("SmtpPort"), out var smtpPort) ? smtpPort : 587,
            Folder = settings.GetValueOrDefault("Folder", "INBOX")
        };
}
