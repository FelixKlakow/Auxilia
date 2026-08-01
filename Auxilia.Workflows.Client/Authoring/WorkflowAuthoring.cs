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

        return await core.CreateConfigurationAsync(new CreateRunConfiguration(
            request.Name, schema.WorkflowType, request.Context, request.SlotBindings,
            Tags: request.Tags), ct);
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
