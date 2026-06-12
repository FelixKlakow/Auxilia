using System.Collections.Concurrent;
using Auxilia.Governance;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace Auxilia.SystemTestSuite.EndToEnd;

/// <summary>
/// Goal-v1 acceptance (docs/goal-v1.md): one real, governed, fully observable Code Review run
/// end to end — an incoming mail triggers the workflow through the whole platform (adapter →
/// pre-flight policy check → dispatch → registration handshake → JIT slot activation → live
/// views → artifact persistence → mail reply write-back), every step audited; plus the RBAC
/// negative: an unprivileged principal is denied at pre-flight, visibly and audited.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class EndToEndSystemTests
{
    private const string MailSubject = "Please review PR-42";
    private const string ReviewerMailbox = "reviewer@localhost";

    [Test]
    [CancelAfter(300_000)]
    public async Task WhenMailArrives_CodeReviewRunsThroughTheWholePlatform(
        CancellationToken cancellationToken)
    {
        var bus = EndToEndEnvironment.MessageBusClient;

        var statusEvents = new ConcurrentQueue<WorkflowStatusEvent>();
        var artifactEvents = new ConcurrentQueue<ArtifactPersistedEvent>();

        await bus.DeclareExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        await bus.DeclareExchangeAsync(ArtifactPersistedEvent.ExchangeName, cancellationToken);
        await using var statusSubscription = await bus.SubscribeToExchangeAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName,
            (msg, _) => { statusEvents.Enqueue(msg); return Task.CompletedTask; },
            cancellationToken);
        await using var artifactSubscription = await bus.SubscribeToExchangeAsync<ArtifactPersistedEvent>(
            ArtifactPersistedEvent.ExchangeName,
            (msg, _) => { artifactEvents.Enqueue(msg); return Task.CompletedTask; },
            cancellationToken);

        // 1. The external trigger: a human mails the watched mailbox.
        await SendMailAsync(
            from: ReviewerMailbox, to: EndToEndEnvironment.AdapterMailbox,
            subject: MailSubject, body: "Please take a look at PR-42, it touches Widget.cs.",
            cancellationToken);

        // 2. The full lifecycle must be visible on the bus: Received → Queued → Running → Success
        //    for ONE instance of the configured workflow type.
        Guid instanceId = default;
        var lifecycleComplete = await WaitForAsync(() =>
        {
            var complete = statusEvents
                .Where(e => e.WorkflowType == EndToEndEnvironment.WorkflowType)
                .GroupBy(e => e.WorkflowInstanceId)
                .FirstOrDefault(g => g.Any(e => e.State == "Success"));
            if (complete is null)
                return false;
            instanceId = complete.Key;
            return true;
        }, TimeSpan.FromSeconds(210), cancellationToken);
        if (!lifecycleComplete)
            await FailWithDiagnosticsAsync(
                "The mail-triggered Code Review run must reach Success.", statusEvents, cancellationToken);

        var states = statusEvents
            .Where(e => e.WorkflowInstanceId == instanceId)
            .Select(e => e.State)
            .ToList();
        Assert.Multiple(() =>
        {
            Assert.That(states, Does.Contain("Received"), "Dispatch receipt must be a visible lifecycle state.");
            Assert.That(states, Does.Contain("Queued"),   "Pre-flight completion must be a visible lifecycle state.");
            Assert.That(states, Does.Contain("Running"),  "The accepted registration must surface as Running.");
            Assert.That(states, Does.Contain("Success"),  "The completed run must surface as Success.");
        });

        // 3. Artifact persistence is announced on the bus (reference + hash, never payloads).
        var artifactAnnounced = await WaitForAsync(
            () => artifactEvents.Any(e =>
                e.RunInstanceId == instanceId && e.ArtifactType == "code-review-result"),
            TimeSpan.FromSeconds(60), cancellationToken);
        if (!artifactAnnounced)
            await FailWithDiagnosticsAsync(
                $"An ArtifactPersistedEvent for run {instanceId} (code-review-result) must be published.",
                statusEvents, cancellationToken);

        // 4. Durable platform state: artifact metadata, persisted views, and the audit trail.
        await using var provider = EndToEndEnvironment.BuildPlatformDataProvider();

        var artifacts = await provider.GetRequiredService<IDataAccess<ArtifactRecord>>()
            .ReadAsync(cancellationToken);
        var artifact = artifacts.FirstOrDefault(a =>
            a.RunInstanceId == instanceId && a.ArtifactType == "code-review-result");
        Assert.That(artifact, Is.Not.Null,
            "The declared output 'code-review-result' must be indexed in the artifact store.");
        Assert.That(artifact!.WorkItemId, Does.StartWith("mail-"),
            "The artifact lineage must reference the mail-derived work item.");

        var viewData = (await provider.GetRequiredService<IDataAccess<ViewDataRecord>>()
                .ReadAsync(cancellationToken))
            .Where(v => v.WorkflowInstanceId == instanceId)
            .ToList();
        Assert.Multiple(() =>
        {
            Assert.That(viewData.Where(v => v.ViewName == "progress" && v.Sequence >= 1), Is.Not.Empty,
                "The 'progress' view must be persisted for replay.");
            Assert.That(viewData.Where(v => v.ViewName == "review-findings"), Is.Not.Empty,
                "The 'review-findings' view must be persisted for replay.");
            Assert.That(viewData.Where(v => v.ViewName == "agent-conversation"), Is.Not.Empty,
                "The fake reviewer's published 'agent-conversation' chat view must be persisted for replay.");
        });

        var auditRecords = await provider.GetRequiredService<IDataAccess<AuditRecord>>()
            .ReadAsync(cancellationToken);
        Assert.Multiple(() =>
        {
            Assert.That(auditRecords.Any(r => r.Action == "trigger.mail-dispatch"),
                Is.True, "The mail trigger must be audited.");
            Assert.That(auditRecords.Any(r =>
                    r.Action == "policy.allowed" &&
                    r.Actor == EndToEndEnvironment.RunAsPrincipalId.ToString()),
                Is.True, "The pre-flight policy allow for the run-as principal must be audited.");
            Assert.That(auditRecords.Any(r =>
                    r.Action == "workflow.registration.accepted" &&
                    r.Subject == instanceId.ToString()),
                Is.True, "The accepted registration handshake must be audited.");
            Assert.That(auditRecords.Count(r =>
                    r.Action == "workflow.slot-activated" &&
                    r.Subject == instanceId.ToString()),
                Is.GreaterThanOrEqualTo(1), "Just-in-time slot activations must be audited.");
            Assert.That(auditRecords.Any(r =>
                    r.Action == "artifact.persisted" &&
                    r.Subject == instanceId.ToString()),
                Is.True, "The artifact persistence must be audited.");
        });

        // 5. Write-back: the review summary must arrive as a mail reply in the
        //    original sender's mailbox.
        var reply = await WaitForMessageAsync(
            ReviewerMailbox,
            m => m.Subject is not null &&
                 m.Subject.StartsWith($"Re: {MailSubject}", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(60), cancellationToken);
        if (reply is null)
            await FailWithDiagnosticsAsync(
                $"A reply 'Re: {MailSubject}' must arrive in {ReviewerMailbox}'s mailbox.",
                statusEvents, cancellationToken);
        Assert.That(reply!.TextBody, Does.Contain("Code Review Summary"),
            "The reply must carry the review summary.");
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task WhenUnprivilegedPrincipalTriggers_PreFlightFailsAndDenialIsAudited(
        CancellationToken cancellationToken)
    {
        var bus = EndToEndEnvironment.MessageBusClient;

        await using var provider = EndToEndEnvironment.BuildPlatformDataProvider();
        var directory = provider.GetRequiredService<PrincipalDirectory>();
        var (nobody, _) = await directory.CreateApiKeyPrincipalAsync(
            "E2E Unprivileged", "Service", cancellationToken); // deliberately NO role

        var statusEvents = new ConcurrentQueue<WorkflowStatusEvent>();
        await bus.DeclareExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        await using var subscription = await bus.SubscribeToExchangeAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName,
            (msg, _) => { statusEvents.Enqueue(msg); return Task.CompletedTask; },
            cancellationToken);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), EndToEndEnvironment.WorkflowType, EndToEndEnvironment.WorkflowPackageUri,
            new Dictionary<string, string>(), RequestedBy: nobody.Id);
        await bus.PublishAsync(EndToEndEnvironment.CommandQueue, command, cancellationToken);

        var denied = await WaitForAsync(
            () => statusEvents.Any(e =>
                e.WorkflowType == EndToEndEnvironment.WorkflowType &&
                e.State == "PreFlightFailed" &&
                e.ErrorMessage is not null &&
                e.ErrorMessage.Contains("policy", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(60), cancellationToken);
        if (!denied)
            await FailWithDiagnosticsAsync(
                "An unprivileged dispatch must fail pre-flight with a policy error — never silently.",
                statusEvents, cancellationToken);

        var auditRecords = await provider.GetRequiredService<IDataAccess<AuditRecord>>()
            .ReadAsync(cancellationToken);
        Assert.That(auditRecords.Any(r =>
                r.Action == "policy.denied" && r.Actor == nobody.Id.ToString()),
            Is.True, "The policy denial for the unprivileged principal must be audited.");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task AfterSeeding_EmailProviderRecordCarriesItsSettingDescriptors(
        CancellationToken cancellationToken)
    {
        await using var provider = EndToEndEnvironment.BuildPlatformDataProvider();
        var providers = provider.GetRequiredService<IDataAccess<SlotProviderRecord>>();

        // The seed commands are applied asynchronously by the SI — poll the shared store.
        SlotProviderRecord? record = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            record = await providers.ReadAsync(SlotProviderRecord.IdFor("email-work-items"), cancellationToken);
            if (record?.SettingDescriptorsJson is not null)
                break;
            await Task.Delay(500, cancellationToken);
        }

        Assert.That(record, Is.Not.Null, "The email slot provider must be registered.");
        Assert.That(record!.SettingDescriptorsJson, Is.Not.Null,
            "The registration must carry the manifest's setting descriptors into the record.");

        var descriptors = System.Text.Json.JsonSerializer
            .Deserialize<List<Auxilia.Workflows.SettingDescriptor>>(record.SettingDescriptorsJson!)!;
        Assert.Multiple(() =>
        {
            Assert.That(descriptors.Select(d => d.Key), Is.EquivalentTo(new[]
            {
                "ImapHost", "ImapPort", "UseSsl", "Username",
                "Password", "SmtpHost", "SmtpPort", "Folder"
            }), "All eight email settings must be described.");
            Assert.That(descriptors.Single(d => d.Key == "Password").Kind,
                Is.EqualTo(Auxilia.Workflows.SettingKind.Secret), "The password must be a secret.");
            Assert.That(descriptors.Single(d => d.Key == "UseSsl").Kind,
                Is.EqualTo(Auxilia.Workflows.SettingKind.Boolean));
            Assert.That(descriptors.Single(d => d.Key == "ImapPort").Kind,
                Is.EqualTo(Auxilia.Workflows.SettingKind.Number));
            Assert.That(descriptors.Single(d => d.Key == "SmtpPort").Kind,
                Is.EqualTo(Auxilia.Workflows.SettingKind.Number));
            Assert.That(descriptors.Single(d => d.Key == "Folder").DefaultValue, Is.EqualTo("INBOX"));
        });
    }

    // ------------------------------------------------------------------ helpers

    private static async Task SendMailAsync(
        string from, string to, string subject, string body, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(
            EndToEndEnvironment.MailHost, EndToEndEnvironment.MappedSmtp,
            SecureSocketOptions.None, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }

    /// <summary>Polls a GreenMail mailbox over IMAP (auth disabled — login equals mailbox).</summary>
    private static async Task<MimeMessage?> WaitForMessageAsync(
        string mailbox, Func<MimeMessage, bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var imap = new ImapClient();
            await imap.ConnectAsync(
                EndToEndEnvironment.MailHost, EndToEndEnvironment.MappedImap,
                SecureSocketOptions.None, ct);
            await imap.AuthenticateAsync(mailbox, EndToEndEnvironment.MailboxPassword, ct);
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
            await Task.Delay(500, ct);
        }
        return null;
    }

    private static async Task<bool> WaitForAsync(
        Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(500, ct);
        }
        return condition();
    }

    /// <summary>Fails with the observed status events plus SI/Backend log tails.</summary>
    private static async Task FailWithDiagnosticsAsync(
        string message, ConcurrentQueue<WorkflowStatusEvent> statusEvents, CancellationToken ct)
    {
        var observed = statusEvents.IsEmpty
            ? "  <none>"
            : string.Join("\n", statusEvents.Select(e =>
                $"  {e.TimestampUtc:HH:mm:ss} {e.WorkflowInstanceId} {e.WorkflowType} {e.State} {e.ErrorMessage}"));
        var siLogs = await EndToEndEnvironment.LogTailAsync(EndToEndEnvironment.SteeringInstance);
        var beLogs = await EndToEndEnvironment.LogTailAsync(EndToEndEnvironment.Backend);
        Assert.Fail(
            $"{message}\n" +
            $"Observed status events:\n{observed}\n\n" +
            $"--- SteeringInstance logs (tail) ---\n{siLogs}\n\n" +
            $"--- BackendService logs (tail) ---\n{beLogs}");
    }
}
