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
    IDataAccess<TriggerHealthRecord> health,
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
                var dispatched = await PollTriggerAsync(trigger, ct);
                await WriteHealthAsync(trigger.Id, now, error: null, dispatched, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreachable mailbox must not stall the other triggers.
                logger.LogWarning(ex,
                    "Mailbox poll failed for trigger {TriggerId} — will retry next interval.", trigger.Id);
                await WriteHealthAsync(trigger.Id, now, error: ex.Message, dispatched: 0, ct);
            }
        }
    }

    /// <summary>
    /// Health sidecar of the poll: connection errors and configuration errors become
    /// "failing since"; a clean poll clears them. Never carries mail content.
    /// </summary>
    private async Task WriteHealthAsync(
        Guid triggerId, DateTimeOffset now, string? error, int dispatched, CancellationToken ct)
    {
        var existing = await health.ReadAsync(triggerId, ct);
        await health.SaveAsync(new TriggerHealthRecord
        {
            Id = triggerId,
            LastPollUtc = now,
            LastSuccessUtc = error is null ? now : existing?.LastSuccessUtc,
            LastDispatchUtc = dispatched > 0 ? now : existing?.LastDispatchUtc,
            LastError = error,
            FailingSinceUtc = error is null
                ? null
                : existing?.LastError is not null ? existing.FailingSinceUtc : now
        }, ct);
    }

    private async Task<int> PollTriggerAsync(MailboxTriggerRecord trigger, CancellationToken ct)
    {
        var instance = await slotInstances.ReadAsync(trigger.SlotInstanceId, ct)
                       ?? throw new InvalidOperationException(
                           "The trigger references a deleted slot instance.");

        var settings = SettingsFrom(instance);
        if (string.IsNullOrWhiteSpace(settings.ImapHost))
            throw new InvalidOperationException(
                $"Slot instance '{instance.Name}' declares no IMAP host.");

        var mailbox = mailboxFactory.Create(settings);
        var unseen = await mailbox.FetchUnseenAsync(ct);
        var dispatched = 0;

        foreach (var mail in unseen)
        {
            if (!MatchesFilters(trigger, mail))
            {
                // Filtered mails are marked seen without a dispatch so they are not
                // re-evaluated on every poll. Never log subjects or senders.
                await mailbox.MarkSeenAsync(mail.Uid, ct);
                logger.LogInformation(
                    "Mail skipped by trigger filters. Trigger={TriggerId} Uid={Uid}", trigger.Id, mail.Uid);
                continue;
            }

            var workItemId = WorkItemIdFor(mail.MessageId);
            var command = new RunWorkflowCommand(
                Guid.NewGuid(), null, null,
                new Dictionary<string, string>
                {
                    ["WorkItemId"] = workItemId,
                    ["Title"] = mail.Subject,
                    ["From"] = mail.From,
                    ["Body"] = mail.BodyText,
                    // Protocol handle so the workflow's work-items slot can refetch the
                    // mail's attachments in-container — attachments never ride the bus.
                    ["MailUid"] = mail.Uid.ToString()
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
            dispatched++;
        }

        return dispatched;
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

    internal static bool MatchesFilters(MailboxTriggerRecord trigger, InboundMail mail)
        => (string.IsNullOrWhiteSpace(trigger.SubjectContains)
            || mail.Subject.Contains(trigger.SubjectContains.Trim(), StringComparison.OrdinalIgnoreCase))
           && (string.IsNullOrWhiteSpace(trigger.FromContains)
               || mail.From.Contains(trigger.FromContains.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Stable work-item ID from the RFC 5322 Message-Id.</summary>
    internal static string WorkItemIdFor(string messageId)
        => "mail-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(messageId)))[..16].ToLowerInvariant();
}
