using System.Text.RegularExpressions;
using Auxilia.Workflows;
using Auxilia.Workflows.Network;

namespace Auxilia.Core.Runner.Workflows;

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
public sealed partial class NetworkPolicyResolver(
    ILogger<NetworkPolicyResolver> logger, ICoreCredentialClient? coreSettings = null)
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

    /// <summary>
    /// The endpoints a slot plugin's signed manifest declares, with <c>{SettingKey}</c> templates
    /// expanded per binding from the run's non-secret settings (manifest defaults fill gaps): a
    /// provider talking to a configurable server (a mailbox, an on-prem host) declares
    /// <c>tcp://{Host}:{Port}</c> and reaches exactly the server its binding names. A template
    /// over a Secret-kind setting, or one left unresolved, contributes nothing.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolvePluginEndpointsAsync(
        PluginManifest manifest, Guid coreRunId, string? resolutionToken, CancellationToken ct)
    {
        var declared = manifest.NetworkEndpoints ?? [];
        var endpoints = declared.Where(e => !e.Contains('{')).ToList();
        var secret = (manifest.Settings ?? [])
            .Where(s => s.Kind == SettingKind.Secret)
            .Select(s => s.Key)
            .ToHashSet(StringComparer.Ordinal);
        var templates = new List<string>();
        foreach (var template in declared.Where(e => e.Contains('{')))
        {
            if (TemplateKeys(template).FirstOrDefault(secret.Contains) is { } secretKey)
                logger.LogWarning(
                    "Plugin {ProviderType} declares endpoint template '{Template}' over the Secret setting '{Key}' — ignored.",
                    manifest.ProviderType, template, secretKey);
            else
                templates.Add(template);
        }
        if (templates.Count == 0 || coreSettings is null || string.IsNullOrEmpty(resolutionToken))
            return endpoints;

        var keys = templates.SelectMany(TemplateKeys).Distinct(StringComparer.Ordinal).ToList();
        var resolved = await coreSettings.ResolveSettingValuesAsync(
            coreRunId, resolutionToken, manifest.ProviderType, keys, ct);
        foreach (var values in resolved?.Bindings ?? [])
            foreach (var template in templates)
                if (Expand(template, values, manifest.Settings) is { } endpoint)
                    endpoints.Add(endpoint);
        return endpoints.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static IEnumerable<string> TemplateKeys(string template)
        => Placeholder().Matches(template).Select(m => m.Groups[1].Value);

    /// <summary>
    /// One template expanded with a binding's values (manifest defaults for gaps); null when a
    /// placeholder stays open or a value is anything but one name, address or port — a setting
    /// can never widen the entry (no wildcards, lists, ports or schemes smuggled in).
    /// </summary>
    internal static string? Expand(
        string template, IReadOnlyDictionary<string, string> values, IReadOnlyList<SettingDescriptor>? descriptors)
    {
        var open = false;
        var expanded = Placeholder().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            var value = values.TryGetValue(key, out var given) && given is { Length: > 0 }
                ? given
                : descriptors?.FirstOrDefault(d => d.Key == key)?.DefaultValue;
            if (value is null || !SingleToken().IsMatch(value))
            {
                open = true;
                return string.Empty;
            }
            return value;
        });
        return open ? null : expanded;
    }

    [GeneratedRegex(@"\{([A-Za-z0-9_.-]+)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?$")]
    private static partial Regex SingleToken();
}
