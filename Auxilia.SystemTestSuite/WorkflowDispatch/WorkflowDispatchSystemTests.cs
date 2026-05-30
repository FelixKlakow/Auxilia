using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Testing.DummyWorkflows;

namespace Auxilia.SystemTestSuite.WorkflowDispatch;

/// <summary>
/// System tests for the workflow dispatch pipeline.
/// Publishes a <see cref="RunWorkflowCommand"/> to the live SteeringInstance and asserts that
/// the workflow container runs to completion, producing a <see cref="WorkflowStateMessage"/>
/// with <see cref="WorkflowState.Success"/>.
///
/// This test exercises the entire path end-to-end inside real Docker containers:
/// <c>RunWorkflowCommand → WorkflowDispatcher → DockerWorkflowLauncher → Docker API →
/// workflow container → WorkflowAnnouncementHandler → WorkflowDirective(Run) →
/// WorkflowRegistrationHandler → WorkflowConfigurationResponse → WorkflowStateMessage</c>
/// </summary>
[TestFixture]
[Category("System")]
public class WorkflowDispatchSystemTests
{
    private IMessageBusClient Bus => WorkflowDispatchEnvironment.MessageBusClient;

    [Test]
    [CancelAfter(120_000)]
    public async Task WhenSimpleGitCommitWorkflowTriggered_WorkflowRunsAndReportsSuccess(
        CancellationToken cancellationToken)
    {
        // ── Arrange ─────────────────────────────────────────────────────────
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

        // ── Act ──────────────────────────────────────────────────────────────
        var command = new RunWorkflowCommand(
            CommandId:     Guid.NewGuid(),
            WorkflowType:  SimpleGitCommitWorkflow.WorkflowName,
            WorkflowImage: SimpleGitCommitWorkflow.ImageName,
            Context: new Dictionary<string, string>
            {
                // Tells Program.cs which dummy workflow to run.
                ["WORKFLOW_NAME"] = SimpleGitCommitWorkflow.WorkflowName
            });

        await Bus.PublishAsync("workflow.run-commands", command, cancellationToken);

        // ── Assert ───────────────────────────────────────────────────────────
        // Give the workflow up to 90 s to start, run, and report state.
        var completed = await Task.WhenAny(
            stateTcs.Task,
            Task.Delay(TimeSpan.FromSeconds(90), CancellationToken.None));

        Assert.That(completed, Is.EqualTo(stateTcs.Task),
            "No WorkflowStateMessage received within 90 seconds. " +
            "Check SteeringInstance logs and ensure the dummy-workflows image was built.");

        var stateMsg = stateTcs.Task.Result;
        Assert.That(stateMsg.State, Is.EqualTo(WorkflowState.Success),
            $"Workflow ended with state {stateMsg.State}. Error: {stateMsg.ErrorMessage}");
    }
}

