using Auxilia.Workflows.Testing.DummyWorkflows;

// Route to the correct dummy workflow by name.
// The WORKFLOW_NAME env var is injected via RunWorkflowCommand.Context by the dispatcher.
var workflowName = Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__WORKFLOW_NAME")
                   ?? throw new InvalidOperationException(
                       "WORKFLOW_CONTEXT__WORKFLOW_NAME environment variable is not set. " +
                       "Pass it in the RunWorkflowCommand.Context dictionary.");

await (workflowName switch
{
    SimpleGitCommitWorkflow.WorkflowName => SimpleGitCommitWorkflow.RunAsync(args),
    SleepingWorkflow.WorkflowName => SleepingWorkflow.RunAsync(args),
    EchoWorkflow.WorkflowName => EchoWorkflow.RunAsync(args),
    EchoDecisionWorkflow.WorkflowName => EchoDecisionWorkflow.RunAsync(args),
    EchoChoiceWorkflow.WorkflowName => EchoChoiceWorkflow.RunAsync(args),
    CredentialResolutionWorkflow.WorkflowName => CredentialResolutionWorkflow.RunAsync(args),
    CredentialRefreshWorkflow.WorkflowName => CredentialRefreshWorkflow.RunAsync(args),
    ViewSequenceWorkflow.WorkflowName => ViewSequenceWorkflow.RunAsync(args),
    IdleSessionWorkflow.WorkflowName => IdleSessionWorkflow.RunAsync(args),
    InputFileWorkflow.WorkflowName => InputFileWorkflow.RunAsync(args),
    CheckpointWorkflow.WorkflowName => CheckpointWorkflow.RunAsync(args),
    WorkspaceRepositoryWorkflow.WorkflowName => WorkspaceRepositoryWorkflow.RunAsync(args),
    PodScenarioWorkflow.WorkflowName => PodScenarioWorkflow.RunAsync(args),
    PodRestartWorkflow.WorkflowName => PodRestartWorkflow.RunAsync(args),
    ContainmentProbeWorkflow.WorkflowName => ContainmentProbeWorkflow.RunAsync(args),
    _ => throw new InvalidOperationException($"Unknown dummy workflow name: '{workflowName}'")
});

