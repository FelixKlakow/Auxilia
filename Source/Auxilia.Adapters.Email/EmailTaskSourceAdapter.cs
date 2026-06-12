using System.Security.Cryptography;
using System.Text;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auxilia.Adapters.Email;

/// <summary>
/// Work-item-event trigger source (ARCHITECTURE §6): polls the configured mailbox and
/// dispatches the configured workflow once per unseen message. The IMAP \Seen flag is the
/// idempotency guard; the work-item ID is a stable hash of the Message-Id so re-deliveries
/// converge on the same lineage. Dispatches carry the configured run-as principal and pass
/// the same policy checks as manual triggers.
/// </summary>
public sealed class EmailTaskSourceAdapter(
    IMailboxClient mailbox,
    IMessageBusClient messageBus,
    AuditLog auditLog,
    TimeProvider timeProvider,
    IOptions<EmailTaskSourceSettings> options,
    ILogger<EmailTaskSourceAdapter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Email task source is disabled — adapter idle.");
            return;
        }

        logger.LogInformation(
            "Email task source started. Mailbox={Host}:{Port}/{Folder} → Workflow={WorkflowType} every {Interval}s",
            settings.ImapHost, settings.ImapPort, settings.Folder,
            settings.WorkflowType, settings.PollIntervalSeconds);

        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.PollIntervalSeconds));
        using var timer = new PeriodicTimer(interval, timeProvider);

        do
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Mailbox poll failed — will retry next interval.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var unseen = await mailbox.FetchUnseenAsync(ct);

        foreach (var mail in unseen)
        {
            var workItemId = WorkItemIdFor(mail.MessageId);
            var command = new RunWorkflowCommand(
                Guid.NewGuid(), settings.WorkflowType, settings.WorkflowPackageUri,
                new Dictionary<string, string>
                {
                    ["WorkItemId"] = workItemId,
                    ["Title"] = mail.Subject,
                    ["From"] = mail.From,
                    ["Body"] = mail.BodyText
                },
                settings.RunAsPrincipalId);

            await messageBus.PublishAsync(settings.CommandQueueName, command, ct);

            // Marked seen only after the dispatch is on the bus: a crash in between causes a
            // re-dispatch, which the platform's idempotency contract absorbs (same WorkItemId).
            await mailbox.MarkSeenAsync(mail.Uid, ct);

            await auditLog.AppendAsync("email-adapter", "trigger.mail-dispatch",
                workItemId, command.CommandId.ToString(), ct: ct);

            logger.LogInformation(
                "Mail dispatched as work item. WorkItem={WorkItemId} Workflow={WorkflowType} Command={CommandId}",
                workItemId, settings.WorkflowType, command.CommandId);
        }
    }

    /// <summary>Stable work-item ID from the RFC 5322 Message-Id.</summary>
    internal static string WorkItemIdFor(string messageId)
        => "mail-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(messageId)))[..16].ToLowerInvariant();
}
