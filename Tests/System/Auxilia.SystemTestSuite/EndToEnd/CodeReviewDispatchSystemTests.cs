using System.Collections.Concurrent;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SystemTestSuite.EndToEnd;

/// <summary>
/// The code-review workflow over the Core-dispatch path with INLINE slot bindings
/// (ProviderType + settings, no connector and no stored configuration) — the resolver's inline
/// path the CI-assumption list calls out. Happy bindings must end in Success; the
/// write-back-failure fake must fail the run loudly. Configuration-based dispatch of the same
/// workflow is covered by the mail-triggered EndToEnd test.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class CodeReviewDispatchSystemTests
{
    private static readonly string[] FakeBoundSlots =
    [
        "repository", "pull-request", "work-items",
        "primary-reviewer", "secondary-reviewer", "workflow-bootstrap"
    ];

    [Test]
    [CancelAfter(180_000)]
    public async Task InlineHappyBindings_RunReportsSuccess(CancellationToken cancellationToken)
    {
        var terminal = await DispatchAndAwaitTerminalAsync("fake-code-review-happy", cancellationToken);

        Assert.That(terminal.State, Is.EqualTo("Success"),
            $"The happy-path run must succeed. Error: {terminal.ErrorMessage}");
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task InlineWriteBackFailureBindings_RunReportsFailed(CancellationToken cancellationToken)
    {
        var terminal = await DispatchAndAwaitTerminalAsync(
            "fake-code-review-write-back-failure", cancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(terminal.State, Is.EqualTo("Failed"),
                "A failing write-back must fail the run — never a silent Success.");
            Assert.That(terminal.ErrorMessage, Is.Not.Null.And.Not.Empty,
                "The failure must carry the workflow's error message.");
        });
    }

    private static async Task<WorkflowStatusEvent> DispatchAndAwaitTerminalAsync(
        string providerType, CancellationToken cancellationToken)
    {
        var bus = EndToEndEnvironment.MessageBusClient;
        var statusEvents = new ConcurrentQueue<WorkflowStatusEvent>();
        await bus.DeclareTopicExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        await using var subscription = await bus.SubscribeToTopicExchangeAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName, ["#"],
            (msg, _) => { statusEvents.Enqueue(msg); return Task.CompletedTask; },
            cancellationToken);

        var response = await EndToEndEnvironment.CoreApiClient.PostAsJsonAsync(
            "/api/runs",
            new RunRequest(
                EndToEndEnvironment.WorkflowType,
                new Dictionary<string, string>(),
                SlotBindings: FakeBoundSlots
                    .Select(slot => new SlotBinding(slot, providerType))
                    .ToList()),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var accepted = (await response.Content.ReadFromJsonAsync<RunAccepted>(cancellationToken))!;

        // The claim transition carries the originating command id AND the real instance id —
        // that pins the instance, so a concurrent run of the same workflow type can never be
        // confused with this one. The Core-authored Dispatched event also matches the command
        // id but is KEYED by it (instance id == command id until the claim rekeys the record),
        // so it must not be mistaken for the claim.
        WorkflowStatusEvent? terminal = null;
        while (terminal is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(500, cancellationToken);
            var instanceId = statusEvents
                .FirstOrDefault(e => e.CommandId == accepted.CommandId
                                     && e.WorkflowInstanceId != accepted.CommandId)
                ?.WorkflowInstanceId;
            if (instanceId is null)
                continue;
            terminal = statusEvents.FirstOrDefault(e =>
                e.WorkflowInstanceId == instanceId
                && e.State is "Success" or "Failed" or "PreFlightFailed" or "Cancelled");
        }
        return terminal;
    }
}
