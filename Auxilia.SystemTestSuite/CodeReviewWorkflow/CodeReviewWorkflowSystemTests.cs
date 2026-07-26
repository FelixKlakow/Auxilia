using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData.Entities;
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
            CommandId:          Guid.NewGuid(),
            WorkflowType:       "pull-request-code-review",
            WorkflowPackageUri: "docker://auxilia-code-review-workflow:system-test",
            Context:            new Dictionary<string, string>());

        await Bus.PublishAsync(CodeReviewWorkflowEnvironment.HappyCommandQueue, command, cancellationToken);

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
            CommandId:          Guid.NewGuid(),
            WorkflowType:       "pull-request-code-review",
            WorkflowPackageUri: "docker://auxilia-code-review-workflow:system-test",
            Context:            new Dictionary<string, string>());

        await Bus.PublishAsync(CodeReviewWorkflowEnvironment.EdgeCommandQueue, command, cancellationToken);

        var completed = await Task.WhenAny(
            stateTcs.Task,
            Task.Delay(TimeSpan.FromSeconds(90), CancellationToken.None));

        Assert.That(completed, Is.EqualTo(stateTcs.Task),
            "No WorkflowStateMessage received within 90 seconds.");

        var stateMsg = stateTcs.Task.Result;
        Assert.That(stateMsg.State, Is.EqualTo(WorkflowState.Failed),
            $"Expected WorkflowState.Failed but got {stateMsg.State}.");
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task WhenDispatchedViaNamedConfiguration_RunSucceeds_AndInstanceRecordCarriesConfigurationName(
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

        // Workflow type and package URI are intentionally omitted — the named configuration
        // seeded by the environment supplies them (#18).
        var command = new RunWorkflowCommand(
            CommandId:               Guid.NewGuid(),
            WorkflowType:            null,
            WorkflowPackageUri:      null,
            Context:                 new Dictionary<string, string>(),
            WorkflowConfigurationId: WorkflowConfigurationRecord.IdFor(
                CodeReviewWorkflowEnvironment.ConfigurationName));

        await Bus.PublishAsync(CodeReviewWorkflowEnvironment.HappyCommandQueue, command, cancellationToken);

        var completed = await Task.WhenAny(
            stateTcs.Task,
            Task.Delay(TimeSpan.FromSeconds(90), CancellationToken.None));

        Assert.That(completed, Is.EqualTo(stateTcs.Task),
            "No WorkflowStateMessage received within 90 seconds.");

        var stateMsg = stateTcs.Task.Result;
        Assert.That(stateMsg.State, Is.EqualTo(WorkflowState.Success),
            $"Workflow ended with state {stateMsg.State}. Error: {stateMsg.ErrorMessage}");

        // The instance record (JSON platform-data backend inside the runner container) must
        // carry the configuration ID and name so run history can group by configuration.
        var exec = await CodeReviewWorkflowEnvironment.HappyRunner.ExecAsync(
            ["cat", $"{CodeReviewWorkflowEnvironment.HappyPlatformDataDir}/WorkflowInstanceRecord.json"],
            cancellationToken);
        Assert.That(exec.ExitCode, Is.Zero,
            $"Could not read instance records from the runner container: {exec.Stderr}");

        var records = JsonSerializer.Deserialize<List<WorkflowInstanceRecord>>(
            exec.Stdout, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var record = records!.SingleOrDefault(r => r.Id == stateMsg.WorkflowInstanceId);
        Assert.That(record, Is.Not.Null, "Instance record for the run was not found.");
        Assert.Multiple(() =>
        {
            Assert.That(record!.WorkflowConfigurationName,
                Is.EqualTo(CodeReviewWorkflowEnvironment.ConfigurationName));
            Assert.That(record.WorkflowConfigurationId,
                Is.EqualTo(WorkflowConfigurationRecord.IdFor(
                    CodeReviewWorkflowEnvironment.ConfigurationName)));
            Assert.That(record.WorkflowType, Is.EqualTo("pull-request-code-review"));
        });
    }
}
