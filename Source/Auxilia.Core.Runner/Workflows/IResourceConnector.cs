namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Platform-side connector executing structured resource calls on behalf of workflows
/// (ARCHITECTURE §8). Implementations hold the credentials; workflows never do.
/// </summary>
public interface IResourceConnector
{
    /// <summary>Resource name workflows address in <c>ResourceRequest.ResourceName</c>.</summary>
    string ResourceName { get; }

    /// <summary>Executes one operation and returns its result JSON.</summary>
    Task<string> ExecuteAsync(string operation, string payloadJson, CancellationToken ct);
}
