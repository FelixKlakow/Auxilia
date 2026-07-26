using Auxilia.Core.Client;
using Auxilia.Core.Contracts;

namespace Auxilia.Adapters.Email;

/// <summary>
/// Studio-side dispatcher: drives the run through the Core Run API. A configured trigger runs its
/// stored configuration on behalf of the trigger's principal; an unconfigured one uses the ad-hoc
/// run path (a registered workflow type — the Core resolves the package). The run then passes
/// exactly the same Core policy checks as a manual run.
/// </summary>
public sealed class CoreClientRunDispatcher(ICoreClient coreClient) : ITaskSourceRunDispatcher
{
    public async Task<Guid> DispatchAsync(
        Guid? configurationId, string? workflowType,
        IReadOnlyDictionary<string, string> context, Guid? runAsPrincipalId,
        CancellationToken ct = default)
    {
        var accepted = configurationId is { } configId
            ? await coreClient.RunConfigurationAsync(configId, onBehalfOf: runAsPrincipalId, context: context, ct)
            : await coreClient.RunAsync(
                new RunRequest(workflowType ?? string.Empty, context, RequestedBy: runAsPrincipalId),
                ct);
        return accepted.CommandId;
    }
}
