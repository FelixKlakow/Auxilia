using Auxilia.Core.Client;
using Auxilia.Core.Contracts;

namespace Auxilia.Workflows.Client.Authoring;

/// <summary>What to configure: a registered workflow type plus its slot bindings and context.</summary>
public sealed record WorkflowConfigurationRequest(
    string Name,
    string WorkflowType,
    IReadOnlyList<SlotBinding> SlotBindings,
    IReadOnlyDictionary<string, string>? Context = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>
/// The authoring convenience on top of the raw client: validates a configuration against the
/// workflow type's schema FROM THE CORE REGISTRY (declared slots, required slots, provider-type
/// narrowing, connector existence) before creating it — a bad configuration fails fast with a
/// precise error instead of a dispatch-time surprise. The Core re-validates on submit; this is
/// the client-side fast path, not the authority.
/// </summary>
public sealed class WorkflowAuthoring(ICoreClient core)
{
    /// <summary>Validates and creates the configuration.</summary>
    /// <exception cref="KeyNotFoundException">Unknown workflow type or connector.</exception>
    /// <exception cref="ArgumentException">A binding contradicts the schema.</exception>
    public async Task<RunConfiguration> ConfigureAsync(
        WorkflowConfigurationRequest request, CancellationToken ct = default)
    {
        var schema = await core.GetWorkflowSchemaAsync(request.WorkflowType, ct)
            ?? throw new KeyNotFoundException(
                $"workflow type '{request.WorkflowType}' is not registered in the Core.");

        foreach (var binding in request.SlotBindings)
        {
            var slot = schema.Slots.FirstOrDefault(s => s.SlotName == binding.SlotName)
                ?? throw new ArgumentException(
                    $"slot '{binding.SlotName}' is not declared by workflow type '{schema.WorkflowType}'.");

            if (slot.ProviderTypes is { Count: > 0 } narrowed
                && binding.ProviderType is { } provider
                && !narrowed.Contains(provider))
                throw new ArgumentException(
                    $"slot '{slot.SlotName}' only admits provider types [{string.Join(", ", narrowed)}], not '{provider}'.");

            if (binding.ConnectorId is { } connectorId
                && await core.GetConnectorAsync(connectorId, ct) is null)
                throw new KeyNotFoundException($"connector '{connectorId}' does not exist in the Core.");
        }

        var bound = request.SlotBindings.Select(b => b.SlotName).ToHashSet(StringComparer.Ordinal);
        var missing = schema.Slots
            .Where(s => !s.Optional && !bound.Contains(s.SlotName))
            .Select(s => s.SlotName).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"required slots not bound: {string.Join(", ", missing)}.");

        await ValidateEnvironmentBasesAsync(request.SlotBindings, ct);

        return await core.CreateConfigurationAsync(new CreateRunConfiguration(
            request.Name, schema.WorkflowType, request.Context, request.SlotBindings,
            Tags: request.Tags), ct);
    }

    /// <summary>
    /// One run composes ONE container image on ONE base: environment-composing bindings whose
    /// catalog entries declare different bases (linux vs windows) can never build together, so
    /// the mismatch fails here instead of at dispatch. The lookup runs only when the bindings
    /// name at least two distinct inline provider types.
    /// </summary>
    private async Task ValidateEnvironmentBasesAsync(
        IReadOnlyList<SlotBinding> bindings, CancellationToken ct)
    {
        var providerTypes = bindings
            .Where(b => b.ProviderType is { Length: > 0 })
            .Select(b => b.ProviderType!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (providerTypes.Count < 2)
            return;

        var catalog = (await core.QueryProviderCatalogAsync(new ProviderCatalogQuery(Take: 500), ct)).Items;
        var bases = catalog
            .Where(e => e.ComposesEnvironment
                        && e.EnvironmentBase is { Length: > 0 }
                        && providerTypes.Contains(e.ProviderType, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(e => e.ProviderType, e => e.EnvironmentBase!, StringComparer.OrdinalIgnoreCase);
        if (bases.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            throw new ArgumentException(
                "environment capabilities mix incompatible bases — "
                + string.Join(", ", bases.Select(b => $"'{b.Key}' ({b.Value})"))
                + ". One run composes one image on one base; pick layers of a single base.");
    }

    /// <summary>Validates, creates, and immediately dispatches the configuration.</summary>
    public async Task<(RunConfiguration Configuration, RunAccepted Run)> ConfigureAndRunAsync(
        WorkflowConfigurationRequest request, Guid? onBehalfOf = null, CancellationToken ct = default)
    {
        var configuration = await ConfigureAsync(request, ct);
        var run = await core.RunConfigurationAsync(configuration.Id, onBehalfOf: onBehalfOf, ct: ct);
        return (configuration, run);
    }
}
