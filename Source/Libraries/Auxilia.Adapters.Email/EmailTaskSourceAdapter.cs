using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auxilia.Adapters.Email;

/// <summary>Adapter-level settings; the mailboxes themselves come from trigger records.</summary>
public sealed class MailboxTriggerAdapterSettings
{
    /// <summary>Dispatch command queue consumed by the Core.Runner pool.</summary>
    public string CommandQueueName { get; set; } = "workflow.run-commands";

    /// <summary>Tick of the trigger sweep; each trigger additionally honours its own poll interval.</summary>
    public int TickSeconds { get; set; } = 5;
}

/// <summary>
/// Work-item-event trigger source (ARCHITECTURE §6): polls every mailbox (an email slot
/// instance holds the credentials) that an enabled trigger references and dispatches, per
/// unseen message, the workflow configuration of EVERY enabled trigger of that mailbox whose
/// filters match — the mailbox is fetched once per poll and a mail is marked \Seen only after
/// all of its triggers were evaluated, so one trigger's filter never steals mail from another.
/// The IMAP \Seen flag is the idempotency guard; the work-item ID is a stable hash of the
/// Message-Id so re-deliveries converge on the same lineage. Dispatches carry the trigger's
/// run-as principal and pass the same policy checks as manual runs. Only the host's own token
/// stops the adapter — a slow Core (unary timeout) is logged and retried.
/// </summary>
public sealed class EmailTaskSourceAdapter(
    IDataAccess<MailboxTriggerRecord> triggers,
    IDataAccess<SlotInstanceRecord> slotInstances,
    IDataAccess<TriggerHealthRecord> health,
    ISettingsProtector protector,
    IMailboxClientFactory mailboxFactory,
    ITaskSourceRunDispatcher dispatcher,
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
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Mailbox trigger sweep failed — will retry next tick.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One sweep: every mailbox with at least one due, enabled trigger is polled ONCE and all of
    /// its enabled triggers evaluate the unseen mail together (a mailbox is polled at the pace
    /// of its most frequent trigger).
    /// </summary>
    internal async Task PollDueTriggersAsync(CancellationToken ct)
    {
        var mailboxes = (await triggers.ReadAsync(ct))
            .Where(t => t.Enabled)
            .GroupBy(t => t.SlotInstanceId);
        foreach (var mailbox in mailboxes)
        {
            var now = timeProvider.GetUtcNow();
            var due = mailbox.Where(t => IsDue(t, now)).ToList();
            if (due.Count == 0)
                continue;
            var group = mailbox.ToList();
            foreach (var trigger in group)
                _lastPolls[trigger.Id] = now;

            try
            {
                var dispatched = await PollMailboxAsync(mailbox.Key, group, ct);
                foreach (var trigger in group)
                    await WriteHealthAsync(trigger.Id, now, error: null, dispatched.GetValueOrDefault(trigger.Id), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One unreachable mailbox (or a slow Core) must not stall the other mailboxes.
                logger.LogWarning(ex,
                    "Mailbox poll failed for slot instance {SlotInstanceId} — will retry next interval.", mailbox.Key);
                foreach (var trigger in group)
                    await WriteHealthAsync(trigger.Id, now, error: ex.Message, dispatched: 0, ct);
            }
        }
    }

    private bool IsDue(MailboxTriggerRecord trigger, DateTimeOffset now)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, trigger.PollIntervalSeconds));
        return !_lastPolls.TryGetValue(trigger.Id, out var lastPoll) || now - lastPoll >= interval;
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

    /// <summary>Polls one mailbox for all of its triggers; returns the dispatch count per trigger.</summary>
    private async Task<Dictionary<Guid, int>> PollMailboxAsync(
        Guid slotInstanceId, IReadOnlyList<MailboxTriggerRecord> group, CancellationToken ct)
    {
        var instance = await slotInstances.ReadAsync(slotInstanceId, ct)
                       ?? throw new InvalidOperationException(
                           "The trigger references a deleted slot instance.");

        var settings = SettingsFrom(instance);
        if (string.IsNullOrWhiteSpace(settings.ImapHost))
            throw new InvalidOperationException(
                $"Slot instance '{instance.Name}' declares no IMAP host.");

        var mailbox = mailboxFactory.Create(settings);
        var unseen = await mailbox.FetchUnseenAsync(ct);
        var dispatched = group.ToDictionary(t => t.Id, _ => 0);

        foreach (var mail in unseen)
        {
            var matching = group.Where(t => MatchesFilters(t, mail)).ToList();
            if (matching.Count == 0)
            {
                // A mail no trigger of this mailbox wants is marked seen without a dispatch so
                // it is not re-evaluated on every poll. Never log subjects or senders.
                await mailbox.MarkSeenAsync(mail.Uid, ct);
                logger.LogInformation(
                    "Mail skipped by every trigger filter. SlotInstance={SlotInstanceId} Uid={Uid}",
                    slotInstanceId, mail.Uid);
                continue;
            }

            var workItemId = WorkItemIdFor(mail.MessageId);
            var context = new Dictionary<string, string>
            {
                ["WorkItemId"] = workItemId,
                ["Title"] = mail.Subject,
                ["From"] = mail.From,
                ["Body"] = mail.BodyText,
                // Protocol handle so the workflow's work-items slot can refetch the
                // mail's attachments in-container — attachments never ride the bus.
                ["MailUid"] = mail.Uid.ToString()
            };

            foreach (var trigger in matching)
            {
                // The dispatch seam decides how the run reaches the platform; mail dispatches
                // carry no type — the trigger's workflow configuration supplies it.
                var dispatchId = await dispatcher.DispatchAsync(
                    trigger.WorkflowConfigurationId, workflowType: null,
                    context, trigger.RunAsPrincipalId, ct);

                await auditLog.AppendAsync("email-adapter", "trigger.mail-dispatch",
                    workItemId, dispatchId.ToString(), ct: ct);

                logger.LogInformation(
                    "Mail dispatched as work item. WorkItem={WorkItemId} Configuration={ConfigurationId} Dispatch={DispatchId}",
                    workItemId, trigger.WorkflowConfigurationId, dispatchId);
                dispatched[trigger.Id]++;
            }

            // Marked seen only after EVERY matching trigger's dispatch was accepted: a crash in
            // between causes a re-dispatch, which the platform's idempotency contract absorbs
            // (same WorkItemId per configuration).
            await mailbox.MarkSeenAsync(mail.Uid, ct);
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
