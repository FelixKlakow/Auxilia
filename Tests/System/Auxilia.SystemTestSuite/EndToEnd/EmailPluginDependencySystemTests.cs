using System.Collections.Concurrent;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

namespace Auxilia.SystemTestSuite.EndToEnd;

/// <summary>
/// The email slot's PLUGIN-DEPENDENCY loading in isolation — the CI-assumption list's
/// highest-risk item: the runner must copy the provider DLL <b>and its bundled mail stack</b>
/// (MailKit/MimeKit/BouncyCastle via <c>BundleDependencies</c>) into the workflow container,
/// where the default load context resolves them. The run binds <c>work-items</c> to the real
/// <c>email-work-items</c> provider (everything else stays fake) and the workflow's write-back
/// goes out over REAL SMTP — the reply landing in GreenMail is proof the whole dependency
/// closure loaded and worked inside the container. Unlike the mail-triggered EndToEnd test,
/// nothing else (intake, trigger host, configuration store) is in the failure surface.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class EmailPluginDependencySystemTests
{
    [Test]
    [CancelAfter(300_000)]
    public async Task InlineEmailWorkItems_MailStackLoadsInTheContainer_AndRepliesOverSmtp(
        CancellationToken cancellationToken)
    {
        var workItemId = $"wi-{Guid.NewGuid():N}";
        var author = $"author-{Guid.NewGuid():N}@localhost";

        var bus = EndToEndEnvironment.MessageBusClient;
        var statusEvents = new ConcurrentQueue<WorkflowStatusEvent>();
        await bus.DeclareTopicExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        await using var subscription = await bus.SubscribeToTopicExchangeAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName, ["#"],
            (msg, _) => { statusEvents.Enqueue(msg); return Task.CompletedTask; },
            cancellationToken);

        // The launch context carries the work-item fields the email provider reads in-container
        // (WORKFLOW_CONTEXT__*), exactly as the mail intake would have stamped them.
        var runResp = await EndToEndEnvironment.CoreApiClient.PostAsJsonAsync(
            "/api/runs",
            new RunRequest(
                EndToEndEnvironment.WorkflowType,
                new Dictionary<string, string>
                {
                    ["WORKITEMID"] = workItemId,
                    ["TITLE"] = "Please review my change",
                    ["FROM"] = author,
                    ["BODY"] = "Emailed review request."
                },
                SlotBindings: new List<SlotBinding>
                {
                    new("repository", "fake-code-review-happy"),
                    new("pull-request", "fake-code-review-happy"),
                    new("primary-reviewer", "fake-code-review-happy"),
                    new("secondary-reviewer", "fake-code-review-happy"),
                    new("workflow-bootstrap", "fake-code-review-happy"),
                    new("work-items", "email-work-items",
                        Settings: EndToEndEnvironment.EmailSettings())
                }),
            cancellationToken);
        runResp.EnsureSuccessStatusCode();
        var accepted = (await runResp.Content.ReadFromJsonAsync<RunAccepted>(cancellationToken))!;

        // The run must succeed — a missing MailKit/MimeKit/BouncyCastle DLL in the container
        // fails the provider's activation (or the SMTP write-back) and never reaches Success.
        var terminal = await AwaitTerminalAsync(accepted, statusEvents, cancellationToken);
        Assert.That(terminal.State, Is.EqualTo("Success"),
            $"The email-bound run must succeed. Error: {terminal.ErrorMessage}\n" +
            $"--- Runner logs (tail) ---\n{await EndToEndEnvironment.LogTailAsync(EndToEndEnvironment.Runner)}");

        // The reply mail is the physical proof the mail stack worked INSIDE the container.
        var reply = await WaitForMessageAsync(
            author,
            m => (m.TextBody ?? "").Contains($"[work-item: {workItemId}]"),
            TimeSpan.FromSeconds(60), cancellationToken);
        Assert.That(reply, Is.Not.Null,
            "The workflow's write-back must arrive as an SMTP reply carrying the work-item marker.");
        Assert.That(reply!.Subject, Does.StartWith("Re:"));
    }

    private static async Task<WorkflowStatusEvent> AwaitTerminalAsync(
        RunAccepted accepted, ConcurrentQueue<WorkflowStatusEvent> statusEvents, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(500, ct);
            var instanceId = statusEvents
                .FirstOrDefault(e => e.CommandId == accepted.CommandId
                                     && e.WorkflowInstanceId != accepted.CommandId)
                ?.WorkflowInstanceId;
            if (instanceId is null)
                continue;
            if (statusEvents.FirstOrDefault(e =>
                    e.WorkflowInstanceId == instanceId
                    && e.State is "Success" or "Failed" or "PreFlightFailed" or "Cancelled") is { } terminal)
                return terminal;
        }
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
            await Task.Delay(1000, ct);
        }
        return null;
    }
}
