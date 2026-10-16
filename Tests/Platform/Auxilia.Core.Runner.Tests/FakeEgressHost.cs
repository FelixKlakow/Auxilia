using System.Collections.Concurrent;
using Auxilia.Core.Runner.Workflows.Egress;
using Docker.DotNet;

namespace Auxilia.Core.Runner.Tests;

/// <summary>In-memory egress host: records every planned topology and teardown; emits no events.</summary>
internal sealed class FakeEgressHost : IEgressHost
{
    public ConcurrentBag<EgressPlan> Materialized { get; } = [];
    public ConcurrentBag<Guid> TornDown { get; } = [];
    public string? TeardownLog { get; set; }

    /// <summary>The gateway address the fake reports on the run network.</summary>
    public const string GatewayAddress = "10.199.0.2";

    public Task<string?> MaterializeAsync(IDockerClient client, EgressPlan plan, Func<EgressEvent, Task> onEvent, CancellationToken ct)
    {
        Materialized.Add(plan);
        return Task.FromResult<string?>(GatewayAddress);
    }

    public Task ResumeAsync(Guid instanceId, Func<EgressEvent, Task> onEvent, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<string?> ResolveTerminalEndpointAsync(IDockerClient client, EgressPlan plan, CancellationToken ct)
        => Task.FromResult<string?>(plan.TerminalPort is { } port ? $"{plan.GatewayName}:{port}" : null);

    public Task<string?> TeardownAsync(Guid instanceId, CancellationToken ct = default)
    {
        TornDown.Add(instanceId);
        return Task.FromResult(TeardownLog);
    }

    public Task<int> SweepOrphanedAsync(IReadOnlySet<Guid> liveInstanceIds, CancellationToken ct = default)
        => Task.FromResult(0);
}
