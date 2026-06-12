using Auxilia.Workflows.Network;

namespace Auxilia.SteeringInstance.Workflows;

public enum NetworkPolicyMode
{
    DefaultDeny,
    AllowAll
}

/// <summary>
/// The network policy effective for one run (ARCHITECTURE §10): computed at dispatch from
/// manifest baseline + run configuration, clamped by platform policy, and audited.
/// </summary>
public sealed record EffectiveNetworkPolicy(
    NetworkPolicyMode Mode,
    IReadOnlyList<string> AllowedEndpoints,
    string? Note = null);

/// <summary>
/// Resolves the effective network policy for a run: merges the manifest's endpoint baseline
/// with run-configuration extras (<c>NetworkAllow</c>), removes platform-blocked endpoints,
/// and clamps an <c>allow-all</c> request (<c>NetworkMode</c>) to default-deny when platform
/// policy forbids it.
/// </summary>
public sealed class NetworkPolicyResolver(ILogger<NetworkPolicyResolver> logger)
{
    private const string NetworkModeKey = "NetworkMode";
    private const string NetworkAllowKey = "NetworkAllow";
    private const string AllowAllValue = "allow-all";

    public EffectiveNetworkPolicy Resolve(
        IReadOnlyList<NetworkEndpointDeclaration> manifestEndpoints,
        IReadOnlyDictionary<string, string> runContext,
        WorkflowDispatcherSettings settings)
    {
        var endpoints = manifestEndpoints.Select(e => e.Endpoint);
        if (runContext.TryGetValue(NetworkAllowKey, out var extras))
            endpoints = endpoints.Concat(
                extras.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var blocked = new HashSet<string>(settings.BlockedEndpoints, StringComparer.OrdinalIgnoreCase);
        var allowedEndpoints = endpoints
            .Where(e => !blocked.Contains(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allowAllRequested = runContext.TryGetValue(NetworkModeKey, out var mode) &&
            string.Equals(mode, AllowAllValue, StringComparison.OrdinalIgnoreCase);

        if (allowAllRequested && !settings.AllowAllNetworkPermitted)
        {
            logger.LogWarning(
                "Run configuration requested allow-all network mode but platform policy forbids it — clamping to default-deny.");
            return new EffectiveNetworkPolicy(NetworkPolicyMode.DefaultDeny, allowedEndpoints,
                "allow-all requested but denied by platform policy");
        }

        return new EffectiveNetworkPolicy(
            allowAllRequested ? NetworkPolicyMode.AllowAll : NetworkPolicyMode.DefaultDeny,
            allowedEndpoints);
    }
}
