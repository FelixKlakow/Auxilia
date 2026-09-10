using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Protection;
using Auxilia.Workflows;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Guards a slot binding's INLINE settings of the provider's <see cref="SettingKind.Secret"/>
/// descriptors: protected the moment they enter the Core (a stored configuration, an inline run
/// request), masked on every read (key present, value empty), unprotected only for JIT credential
/// delivery. Which keys are secret comes from the provider catalog — the same descriptors the
/// editors render — resolved through the binding's provider type, its connector's, or its
/// workspace's. Non-secret settings pass through untouched.
/// </summary>
public sealed class SlotBindingSecrets(
    ProviderCatalogService catalog,
    ConnectorService connectors,
    WorkspaceResourceService workspaces,
    ISettingsProtector protector)
{
    /// <summary>The value every read DTO carries for a stored secret — never the ciphertext.</summary>
    public const string Masked = "";

    /// <summary>Protects every non-empty Secret-kind value; an empty one is dropped (nothing stored).</summary>
    public Task<IReadOnlyList<SlotBinding>> ProtectAsync(
        IReadOnlyList<SlotBinding> bindings, CancellationToken ct)
        => ProtectAsync(bindings, stored: [], ct);

    /// <summary>
    /// Protects the incoming bindings against the stored ones: a Secret-kind value left EMPTY
    /// (or absent) keeps the stored value of the same slot + provider — "leave empty to keep" —
    /// while a non-empty one replaces it.
    /// </summary>
    public async Task<IReadOnlyList<SlotBinding>> ProtectAsync(
        IReadOnlyList<SlotBinding> incoming, IReadOnlyList<SlotBinding> stored, CancellationToken ct)
    {
        var result = new List<SlotBinding>(incoming.Count);
        foreach (var binding in incoming)
        {
            var secretKeys = await SecretKeysAsync(binding, ct);
            if (secretKeys.Count == 0 || binding.Settings is null)
            {
                result.Add(binding);
                continue;
            }
            var previous = stored.FirstOrDefault(s =>
                s.SlotName == binding.SlotName
                && string.Equals(s.ProviderType, binding.ProviderType, StringComparison.OrdinalIgnoreCase)
                && s.ConnectorId == binding.ConnectorId
                && s.WorkspaceId == binding.WorkspaceId);
            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in binding.Settings)
            {
                if (!secretKeys.Contains(key))
                {
                    settings[key] = value;
                    continue;
                }
                if (value is { Length: > 0 })
                    settings[key] = protector.Protect(value);
                else if (previous?.Settings?.TryGetValue(key, out var kept) == true && kept is { Length: > 0 })
                    settings[key] = kept;
            }
            // Keys the caller omitted entirely keep their stored secret too — a read DTO never
            // carried the value, so a client cannot echo it back.
            foreach (var key in secretKeys)
                if (!settings.ContainsKey(key)
                    && previous?.Settings?.TryGetValue(key, out var kept) == true && kept is { Length: > 0 })
                    settings[key] = kept;
            result.Add(binding with { Settings = settings });
        }
        return result;
    }

    /// <summary>Read shape: every Secret-kind key stays present with <see cref="Masked"/> as its value.</summary>
    public async Task<IReadOnlyList<SlotBinding>> MaskAsync(
        IReadOnlyList<SlotBinding> bindings, CancellationToken ct)
    {
        var result = new List<SlotBinding>(bindings.Count);
        foreach (var binding in bindings)
        {
            var secretKeys = await SecretKeysAsync(binding, ct);
            if (secretKeys.Count == 0 || binding.Settings is null)
            {
                result.Add(binding);
                continue;
            }
            result.Add(binding with
            {
                Settings = binding.Settings.ToDictionary(
                    kv => kv.Key, kv => secretKeys.Contains(kv.Key) ? Masked : kv.Value, StringComparer.Ordinal)
            });
        }
        return result;
    }

    /// <summary>The binding's inline settings with every Secret-kind value decrypted — JIT delivery only.</summary>
    public async Task<IReadOnlyDictionary<string, string>> UnprotectAsync(SlotBinding binding, CancellationToken ct)
    {
        if (binding.Settings is null)
            return new Dictionary<string, string>();
        var secretKeys = await SecretKeysAsync(binding, ct);
        return binding.Settings.ToDictionary(
            kv => kv.Key,
            kv => secretKeys.Contains(kv.Key) ? protector.Unprotect(kv.Value) : kv.Value,
            StringComparer.Ordinal);
    }

    /// <summary>Keys of the binding's provider that its manifest declares as secrets.</summary>
    public async Task<IReadOnlySet<string>> SecretKeysAsync(SlotBinding binding, CancellationToken ct)
    {
        var providerType = binding.ProviderType;
        if (string.IsNullOrEmpty(providerType) && binding.ConnectorId is { } connectorId)
            providerType = (await connectors.GetAsync(connectorId, ct))?.ProviderType;
        if (string.IsNullOrEmpty(providerType) && binding.WorkspaceId is { } workspaceId)
            providerType = (await workspaces.GetAsync(workspaceId, ct))?.ProviderType;
        if (string.IsNullOrEmpty(providerType)
            || await catalog.FindAsync(providerType, ct) is not { } entry)
            return new HashSet<string>(StringComparer.Ordinal);
        return entry.Descriptors
            .Where(d => string.Equals(d.Kind, nameof(SettingKind.Secret), StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Key)
            .ToHashSet(StringComparer.Ordinal);
    }
}
