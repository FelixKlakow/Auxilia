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

        ValidateInputValues(schema, request.Context, storedConfiguration: true);
        await ValidateEnvironmentBasesAsync(request.SlotBindings, ct);

        return await core.CreateConfigurationAsync(new CreateRunConfiguration(
            request.Name, schema.WorkflowType, request.Context, request.SlotBindings,
            Tags: request.Tags), ct);
    }

    /// <summary>
    /// One run composes ONE container image on ONE base — but a layer may carry a variant per
    /// base. Environment-composing bindings whose catalog entries share no base (or whose
    /// version pins disagree on every shared base) can never build together, so the mismatch
    /// fails here instead of at dispatch — mirroring the Core's check. The lookup runs only when
    /// the bindings name at least two distinct inline provider types.
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
                        && providerTypes.Contains(e.ProviderType, StringComparer.OrdinalIgnoreCase)
                        && e.EnvironmentBases is { Count: > 0 })
            .ToDictionary(e => e.ProviderType, e => e.EnvironmentBases!, StringComparer.OrdinalIgnoreCase);
        if (bases.Count == 0)
            return;

        var candidateBases = bases.Values
            .Select(refs => refs.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase))
            .Aggregate((intersection, next) =>
            {
                intersection.IntersectWith(next);
                return intersection;
            });
        bool VersionsAgree(string baseName) => bases.Values
            .Select(refs => refs.First(r =>
                string.Equals(r.Name, baseName, StringComparison.OrdinalIgnoreCase)).Version)
            .Where(v => v is { Length: > 0 })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() <= 1;
        if (!candidateBases.Any(VersionsAgree))
            throw new ArgumentException(
                "environment capabilities have no common base — "
                + string.Join(", ", bases.Select(b =>
                    $"'{b.Key}' ({string.Join("|", b.Value.Select(r => r.Version is { Length: > 0 } ? $"{r.Name}/{r.Version}" : r.Name))})"))
                + ". One run composes one image on one base; every selected layer must share "
                + "a base whose version pins agree.");
    }

    /// <summary>
    /// Validates, creates, and immediately dispatches the configuration.
    /// <paramref name="perRunContext"/> carries the values that are different every run by
    /// nature (declared <c>PerRun</c> inputs — never storable in the configuration); they
    /// overlay the stored context at dispatch.
    /// </summary>
    public async Task<(RunConfiguration Configuration, RunAccepted Run)> ConfigureAndRunAsync(
        WorkflowConfigurationRequest request, Guid? onBehalfOf = null,
        IReadOnlyDictionary<string, string>? perRunContext = null, CancellationToken ct = default)
    {
        if (perRunContext is { Count: > 0 }
            && await core.GetWorkflowSchemaAsync(request.WorkflowType, ct) is { } schema)
            ValidateInputValues(schema, perRunContext, storedConfiguration: false);
        var configuration = await ConfigureAsync(request, ct);
        var run = await core.RunConfigurationAsync(
            configuration.Id, onBehalfOf: onBehalfOf, context: perRunContext, ct: ct);
        return (configuration, run);
    }

    /// <summary>
    /// Declared-input value validation, mirroring what dispatch UIs enforce: kind checks on
    /// every provided value (choice membership, number/boolean parse) and — for a STORED
    /// configuration — the rule that <c>PerRun</c> inputs are never fixable, only suppliable
    /// at dispatch. Unknown context keys pass untouched: platform keys (<c>pod-bases</c>,
    /// trigger extras) are legitimate context that is not a declared input.
    /// </summary>
    private static void ValidateInputValues(
        WorkflowSchemaDto schema, IReadOnlyDictionary<string, string>? context, bool storedConfiguration)
    {
        if (context is not { Count: > 0 })
            return;
        var problems = new List<string>();
        foreach (var input in schema.Inputs)
        {
            if (!context.TryGetValue(input.Name, out var value))
                continue;
            if (storedConfiguration && input.PerRun)
            {
                problems.Add(
                    $"input '{input.Name}' is per-run — it is asked at dispatch and can never "
                    + "be fixed in a stored configuration");
                continue;
            }
            switch (input.Kind)
            {
                case InputKinds.Choice when input.Choices is { Count: > 0 } choices
                                            && !choices.Contains(value, StringComparer.Ordinal):
                    problems.Add(
                        $"input '{input.Name}' must be one of [{string.Join(", ", choices)}], not '{value}'");
                    break;
                case InputKinds.Number when !double.TryParse(
                    value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _):
                    problems.Add($"input '{input.Name}' must be a number, not '{value}'");
                    break;
                case InputKinds.Boolean when !bool.TryParse(value, out _):
                    problems.Add($"input '{input.Name}' must be true or false, not '{value}'");
                    break;
            }
        }
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems));
    }
}
