using System.Text.Json;

namespace Auxilia.Workflows;

/// <summary>
/// A file or folder the workflow saves while it runs (<see cref="IWorkflowBuilder.DeclaresCheckpoint"/>):
/// each <see cref="IRunCheckpoints.SaveAsync"/> replaces the run's previous copy, so a crash keeps the
/// last one. <paramref name="RelativePath"/> is inside the run's output directory (a folder is
/// archived as .tar.gz); <paramref name="LifetimeHours"/> is the workflow's lifetime for it, applied
/// where the platform lets workflows set one. Checkpoints are encrypted at rest and downloadable —
/// and deletable — by the principals the run belongs to.
/// </summary>
public sealed record WorkflowCheckpointDescriptor(
    string Name, string RelativePath, string? Description = null, double? LifetimeHours = null)
{
    /// <summary>True for a path inside the output directory: relative, no <c>..</c>, no empty segment.</summary>
    public static bool IsValidRelativePath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && !Path.IsPathRooted(path)
           && path.Replace('\\', '/').Split('/') is var segments
           && segments.All(s => s.Length > 0 && s != "." && s != "..");
}

/// <summary>The latest saved copy of a checkpoint — the run's artifact of kind <c>checkpoint</c>.</summary>
public sealed record RunCheckpoint(
    Guid Id, int Version, long SizeBytes, string ContentHash, DateTimeOffset? ExpiresUtc);

/// <summary>Saves the run's declared checkpoints.</summary>
public interface IRunCheckpoints
{
    /// <summary>
    /// Saves the declared checkpoint <paramref name="name"/> as it is in the output directory now,
    /// replacing the run's previous copy. An undeclared name throws; so does a save the platform
    /// could not complete — nothing half-saved replaces a good copy.
    /// </summary>
    Task<RunCheckpoint> SaveAsync(string name, CancellationToken ct = default);
}

/// <summary>The resource the runner serves checkpoint saves under (the audited resource proxy).</summary>
public static class RunCheckpointsResource
{
    public const string Name = "run-checkpoints";
    public const string SaveOperation = "save";

    /// <summary>The request payload: which declared checkpoint to save.</summary>
    public sealed record SaveRequest(string Name);
}

/// <summary>Saves checkpoints through the runner's audited resource proxy.</summary>
public sealed class ResourceProxyRunCheckpoints(
    ResourceProxyClient proxy, IReadOnlyList<WorkflowCheckpointDescriptor> declared) : IRunCheckpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // One save at a time: each must replace the one before it, never race it.
    private readonly SemaphoreSlim _saving = new(1, 1);

    public async Task<RunCheckpoint> SaveAsync(string name, CancellationToken ct = default)
    {
        if (declared.All(c => c.Name != name))
            throw new InvalidOperationException(
                $"Checkpoint '{name}' is not declared by this workflow. Declare it via DeclaresCheckpoint.");
        await _saving.WaitAsync(ct);
        try
        {
            var result = await proxy.CallAsync(
                RunCheckpointsResource.Name, RunCheckpointsResource.SaveOperation,
                JsonSerializer.Serialize(new RunCheckpointsResource.SaveRequest(name), Json), ct);
            return JsonSerializer.Deserialize<RunCheckpoint>(result, Json)
                   ?? throw new InvalidOperationException($"The platform did not confirm checkpoint '{name}'.");
        }
        finally
        {
            _saving.Release();
        }
    }
}
