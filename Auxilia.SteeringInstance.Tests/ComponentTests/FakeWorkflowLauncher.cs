using Auxilia.SteeringInstance.Workflows;

namespace Auxilia.SteeringInstance.Tests.ComponentTests;

/// <summary>
/// In-process <see cref="IWorkflowLauncher"/> that records calls without invoking Docker.
/// Thread-safe.
/// </summary>
public sealed class FakeWorkflowLauncher : IWorkflowLauncher
{
    private readonly List<WorkflowLaunchRequest> _calls = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public IReadOnlyList<WorkflowLaunchRequest> Calls => _calls;

    public async Task LaunchAsync(WorkflowLaunchRequest request, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { _calls.Add(request); }
        finally { _lock.Release(); }
    }
}

