using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auxilia.Adapters.Email;

/// <summary>Adapter-level settings; the mailboxes themselves come from trigger records.</summary>
public sealed class MailboxTriggerAdapterSettings
{
    /// <summary>Dispatch command queue consumed by the Steering Instance pool.</summary>
    public string CommandQueueName { get; set; } = "workflow.run-commands";

    /// <summary>Tick of the trigger sweep; each trigger additionally honours its own poll interval.</summary>
    public int TickSeconds { get; set; } = 5;
}

/// <summary>
/// Work-item-event trigger source (ARCHITECTURE §6): polls every enabled mailbox trigger's
/// mailbox (an email slot instance holds the credentials) and dispatches the trigger's
/// workflow configuration once per unseen message. The IMAP \Seen flag is the idempotency
/// guard; the work-item ID is a stable hash of the Message-Id so re-deliveries converge on
/// the same lineage. Dispatches carry the trigger's run-as principal and pass the same
/// policy checks as manual runs.
/// </summary>
public sealed class EmailTaskSourceAdapter(
    IDataAccess<MailboxTriggerRecord> triggers,
    IDataAccess<SlotInstanceRecord> slotInstances,
    ISettingsProtector protector,
    IMailboxClientFactory mailboxFactory,
    IMessageBusClient messageBus,
    AuditLog auditLog,
    TimeProvider timeProvider,
    IOptions<MailboxTriggerAdapterSettings> options,
    ILogger<EmailTaskSourceAdapter> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastPolls = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Email task source started — polling enabled mailbox triggers every {Tick}s.",
            options.Value.TickSeconds);

        var tick = TimeSpan.FromSeconds(Math.Max(1, options.Value.TickSeconds));
        using var timer = new PeriodicTimer(tick, timeProvider);

        do
        {
            try
            {
                await PollDueTriggersAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Mailbox trigger sweep failed — will retry next tick.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task PollDueTriggersAsync(CancellationToken ct)
    {
        var records = (await triggers.ReadAsync(ct)).ToList().Where(t => t.Enabled).ToList();
        foreach (var trigger in records)
        {
            var now = timeProvider.GetUtcNow();
            var interval = TimeSpan.FromSeconds(Math.Max(1, trigger.PollIntervalSeconds));
            if (_lastPolls.TryGetValue(trigger.Id, out var lastPoll) && now - lastPoll < interval)
                continue;
            _lastPolls[trigger.Id] = now;

            try
            {
                await PollTriggerAsync(trigger, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreachable mailbox must not stall the other triggers.
                logger.LogWarning(ex,
                    "Mailbox poll failed for trigger {TriggerId} — will retry next interval.", trigger.Id);
            }
        }
    }

    private async Task PollTriggerAsync(MailboxTriggerRecord trigger, CancellationToken ct)
    {
        var instance = await slotInstances.ReadAsync(trigger.SlotInstanceId, ct);
        if (instance is null)
        {
            logger.LogWarning(
                "Mailbox trigger {TriggerId} references a deleted slot instance — skipping.", trigger.Id);
            return;
        }

        var settings = SettingsFrom(instance);
        if (string.IsNullOrWhiteSpace(settings.ImapHost))
        {
            logger.LogWarning(
                "Mailbox trigger {TriggerId}: slot instance '{Instance}' declares no IMAP host — skipping.",
                trigger.Id, instance.Name);
            return;
        }

        var mailbox = mailboxFactory.Create(settings);
        var unseen = await mailbox.FetchUnseenAsync(ct);

        foreach (var mail in unseen)
        {
            var workItemId = WorkItemIdFor(mail.MessageId);
            var command = new RunWorkflowCommand(
                Guid.NewGuid(), null, null,
                new Dictionary<string, string>
                {
                    ["WorkItemId"] = workItemId,
                    ["Title"] = mail.Subject,
                    ["From"] = mail.From,
                    ["Body"] = mail.BodyText
                },
                trigger.RunAsPrincipalId,
                trigger.WorkflowConfigurationId);

            await messageBus.PublishAsync(options.Value.CommandQueueName, command, ct);

            // Marked seen only after the dispatch is on the bus: a crash in between causes a
            // re-dispatch, which the platform's idempotency contract absorbs (same WorkItemId).
            await mailbox.MarkSeenAsync(mail.Uid, ct);

            await auditLog.AppendAsync("email-adapter", "trigger.mail-dispatch",
                workItemId, command.CommandId.ToString(), ct: ct);

            logger.LogInformation(
                "Mail dispatched as work item. WorkItem={WorkItemId} Configuration={ConfigurationId} Command={CommandId}",
                workItemId, trigger.WorkflowConfigurationId, command.CommandId);
        }
    }

    /// <summary>Maps the instance's decrypted settings onto the mailbox client's shape; never logged.</summary>
    private EmailTaskSourceSettings SettingsFrom(SlotInstanceRecord instance)
    {
        Dictionary<string, string> settings;
        try
        {
            settings = JsonSerializer.Deserialize<Dictionary<string, string>>(
                protector.Unprotect(instance.ProtectedSettingsJson)) ?? [];
        }
        catch
        {
            settings = [];
        }

        return new EmailTaskSourceSettings
        {
            ImapHost = settings.GetValueOrDefault("ImapHost", string.Empty),
            ImapPort = int.TryParse(settings.GetValueOrDefault("ImapPort"), out var imapPort) ? imapPort : 993,
            UseSsl = !bool.TryParse(settings.GetValueOrDefault("UseSsl"), out var useSsl) || useSsl,
            Username = settings.GetValueOrDefault("Username", string.Empty),
            Password = settings.GetValueOrDefault("Password", string.Empty),
            Folder = settings.GetValueOrDefault("Folder", "INBOX")
        };
    }

    /// <summary>Stable work-item ID from the RFC 5322 Message-Id.</summary>
    internal static string WorkItemIdFor(string messageId)
        => "mail-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(messageId)))[..16].ToLowerInvariant();
}
