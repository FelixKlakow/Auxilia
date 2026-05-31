using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SystemTestSuite.CodeReviewWorkflow;

[TestFixture]
[Category("System")]
public sealed class CodeReviewWorkflowSystemTests
{
    private IMessageBusClient Bus => CodeReviewWorkflowEnvironment.MessageBusClient;

    [Test]
    [CancelAfter(120_000)]
    public async Task WhenCodeReviewWorkflowTriggered_HappyPath_ReportsSuccess(
        CancellationToken cancellationToken)
    {
        const string stateExchange = "workflow.state";
        await Bus.DeclareExchangeAsync(stateExchange, cancellationToken);

        var stateTcs = new TaskCompletionSource<WorkflowStateMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var _ = cancellationToken.Register(
            () => stateTcs.TrySetCanceled(cancellationToken));

        await using var subscription = await Bus.SubscribeToExchangeAsync<WorkflowStateMessage>(
            stateExchange,
            (msg, _) =>
            {
                stateTcs.TrySetResult(msg);
                return Task.CompletedTask;
            },
            cancellationToken);

        var command = new RunWorkflowCommand(
            CommandId:     Guid.NewGuid(),
            WorkflowType:  "pull-request-code-review",
            WorkflowPackageUri: CodeReviewWorkflowEnvironment.HarnessImageName,
            Context: new Dictionary<string, string>
            {
                ["WORKFLOW_NAME"] = "pull-request-code-review",
                ["FAKE_MODE"]     = "happy-path"
            });

        await Bus.PublishAsync("workflow.run-commands", command, cancellationToken);

        var completed = await Task.WhenAny(
            stateTcs.Task,
            Task.Delay(TimeSpan.FromSeconds(90), CancellationToken.None));

        Assert.That(completed, Is.EqualTo(stateTcs.Task),
            "No WorkflowStateMessage received within 90 seconds.");

        var stateMsg = stateTcs.Task.Result;
        Assert.That(stateMsg.State, Is.EqualTo(WorkflowState.Success),
            $"Workflow ended with state {stateMsg.State}. Error: {stateMsg.ErrorMessage}");
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task WhenCodeReviewWorkflowTriggered_WriteBackFailure_ReportsFailed(
        CancellationToken cancellationToken)
    {
        const string stateExchange = "workflow.state";
        await Bus.DeclareExchangeAsync(stateExchange, cancellationToken);

        var stateTcs = new TaskCompletionSource<WorkflowStateMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var _ = cancellationToken.Register(
            () => stateTcs.TrySetCanceled(cancellationToken));

        await using var subscription = await Bus.SubscribeToExchangeAsync<WorkflowStateMessage>(
            stateExchange,
            (msg, _) =>
            {
                stateTcs.TrySetResult(msg);
                return Task.CompletedTask;
            },
            cancellationToken);

        var command = new RunWorkflowCommand(
            CommandId:     Guid.NewGuid(),
            WorkflowType:  "pull-request-code-review",
            WorkflowPackageUri: CodeReviewWorkflowEnvironment.HarnessImageName,
            Context: new Dictionary<string, string>
            {
                ["WORKFLOW_NAME"] = "pull-request-code-review",
                ["FAKE_MODE"]     = "write-back-failure"
            });

        await Bus.PublishAsync("workflow.run-commands", command, cancellationToken);

        var completed = await Task.WhenAny(
            stateTcs.Task,
            Task.Delay(TimeSpan.FromSeconds(90), CancellationToken.None));

        Assert.That(completed, Is.EqualTo(stateTcs.Task),
            "No WorkflowStateMessage received within 90 seconds.");

        var stateMsg = stateTcs.Task.Result;
        Assert.That(stateMsg.State, Is.EqualTo(WorkflowState.Failed),
            $"Expected WorkflowState.Failed but got {stateMsg.State}.");
    }
}
