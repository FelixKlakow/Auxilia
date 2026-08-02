namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Abstracts the mechanism used to start a workflow process/container.
/// Production implementation uses Docker; tests can inject a fake.
/// </summary>
public interface IWorkflowLauncher
{
    Task<WorkflowLaunchResult> LaunchAsync(WorkflowLaunchRequest request, CancellationToken ct = default);
}

