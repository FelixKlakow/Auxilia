using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
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
/// Goal-v1 acceptance, retargeted for the BackendService retirement (Phase 4): one real, governed,
/// fully observable Code Review run end to end — an incoming mail triggers the workflow through the
/// split platform (TriggerHost email adapter → Core Run API on-behalf-of → runner dispatch →
/// registration handshake → JIT slot activation → live views → artifact persistence → mail reply
/// write-back), every step audited; plus the RBAC negative: an unprivileged principal is denied at
/// the Core Run API, visibly and audited.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class EndToEndSystemTests
{
    private const string MailSubject = "Please review PR-42";
    private const string ReviewerMailbox = "reviewer@localhost";

    [Test]
    [CancelAfter(540_000)]
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

        // 1. The external trigger: a human mails the watched mailbox. the TriggerHost's adapter polls it and
        //    dispatches the mail-review configuration through the Core Run API on-behalf-of the
        //    trigger's principal.
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
            Assert.That(states, Does.Contain("Running"), "The accepted registration must surface as Running.");
            Assert.That(states, Does.Contain("Success"), "The completed run must surface as Success.");
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

        // 4. Durable platform state: artifact metadata, persisted views, and the audit trail written
        //    by the TriggerHost (trigger.mail-dispatch) and the runner (registration/slot/artifact) into the
        //    shared Mongo. The Core-side policy/on-behalf-of audit is read over /api/audit below.
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
                Is.True, "The mail trigger must be audited by the trigger-host adapter.");
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

        // The Core audits the on-behalf-of delegation (the trigger dispatching AS the run-as principal).
        var delegationAudited = await WaitForAsync(
            () => CoreAuditContainsAsync("run.on-behalf-of", EndToEndEnvironment.RunAsPrincipalId.ToString())
                .GetAwaiter().GetResult(),
            TimeSpan.FromSeconds(30), cancellationToken);
        Assert.That(delegationAudited, Is.True,
            "The Core must audit the mail trigger's on-behalf-of delegation for the run-as principal.");

        // 5. Write-back: the review summary must arrive as a mail reply in the original sender's mailbox.
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

        // 6. RERUN: re-dispatch the mail-review configuration through the Core Run API (on-behalf-of the
        //    same principal) — the governed, tokenized path the console uses, not a raw bus command.
        var rerunResp = await EndToEndEnvironment.CoreApiClient.PostAsJsonAsync(
            $"/api/configurations/{EndToEndEnvironment.MailReviewConfigurationId}/run" +
            $"?onBehalfOf={EndToEndEnvironment.RunAsPrincipalId}",
            new Dictionary<string, string>(), cancellationToken);
        rerunResp.EnsureSuccessStatusCode();

        var rerunComplete = await WaitForAsync(() =>
            statusEvents
                .Where(e => e.WorkflowType == EndToEndEnvironment.WorkflowType &&
                            e.WorkflowInstanceId != instanceId)
                .GroupBy(e => e.WorkflowInstanceId)
                .Any(g => g.Any(e => e.State == "Success")),
            TimeSpan.FromSeconds(210), cancellationToken);
        if (!rerunComplete)
            await FailWithDiagnosticsAsync(
                "The rerun must reach Success like the original run.", statusEvents, cancellationToken);
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task WhenUnprivilegedPrincipalTriggers_TheCoreDeniesAndAuditsIt(
        CancellationToken cancellationToken)
    {
        // A fresh Core AI/service principal with NO role — deny-by-default.
        var createResp = await EndToEndEnvironment.CoreApiClient.PostAsJsonAsync(
            "/api/principals/ai",
            new CreateApiKeyPrincipalRequest("E2E Unprivileged", "Service"), cancellationToken);
        createResp.EnsureSuccessStatusCode();
        var nobody = (await createResp.Content.ReadFromJsonAsync<CreatedApiKeyPrincipal>(cancellationToken))!;

        using var nobodyClient = new HttpClient
        {
            BaseAddress = EndToEndEnvironment.CoreApiClient.BaseAddress
        };
        nobodyClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", nobody.ApiKey);

        var runResp = await nobodyClient.PostAsJsonAsync("/api/runs",
            new RunRequest(EndToEndEnvironment.WorkflowType,
                new Dictionary<string, string>()), cancellationToken);

        Assert.That(runResp.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
            "An unprivileged dispatch must be denied by the Core Run API — never silently accepted.");

        var denialAudited = await WaitForAsync(
            () => CoreAuditContainsAsync("policy.denied", null, nobody.Principal.Id.ToString())
                .GetAwaiter().GetResult(),
            TimeSpan.FromSeconds(30), cancellationToken);
        Assert.That(denialAudited, Is.True,
            "The policy denial for the unprivileged principal must be audited by the Core.");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Queries the Core audit REST endpoint for a record matching action/subject/actor.</summary>
    private static async Task<bool> CoreAuditContainsAsync(
        string action, string? subject = null, string? actor = null)
    {
        var query = $"/api/audit?action={Uri.EscapeDataString(action)}";
        if (subject is not null) query += $"&subject={Uri.EscapeDataString(subject)}";
        if (actor is not null) query += $"&actor={Uri.EscapeDataString(actor)}";
        var resp = await EndToEndEnvironment.CoreApiClient.GetAsync(query);
        if (!resp.IsSuccessStatusCode)
            return false;
        var page = await resp.Content.ReadFromJsonAsync<PagedResult<AuditEntry>>();
        return page is { Items.Count: > 0 };
    }

    /// <summary>Minimal shape for deserializing Core audit rows (only what the assertions need).</summary>
    private sealed record AuditEntry(string Actor, string Action, string Subject);

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

    /// <summary>Fails with the observed status events plus Runner/TriggerHost log tails.</summary>
    private static async Task FailWithDiagnosticsAsync(
        string message, ConcurrentQueue<WorkflowStatusEvent> statusEvents, CancellationToken ct)
    {
        var observed = statusEvents.IsEmpty
            ? "  <none>"
            : string.Join("\n", statusEvents.Select(e =>
                $"  {e.TimestampUtc:HH:mm:ss} {e.WorkflowInstanceId} {e.WorkflowType} {e.State} {e.ErrorMessage}"));
        var siLogs = await EndToEndEnvironment.LogTailAsync(EndToEndEnvironment.Runner);
        var studioLogs = await EndToEndEnvironment.LogTailAsync(EndToEndEnvironment.TriggerHost);
        Assert.Fail(
            $"{message}\n" +
            $"Observed status events:\n{observed}\n\n" +
            $"--- Runner logs (tail) ---\n{siLogs}\n\n" +
            $"--- TriggerHost logs (tail) ---\n{studioLogs}");
    }
}
