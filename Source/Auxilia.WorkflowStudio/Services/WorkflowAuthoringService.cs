using Auxilia.Core.Client;
using Auxilia.Core.Contracts;

namespace Auxilia.WorkflowStudio.Services;

/// <summary>
/// Turns a domain workflow configuration (a workflow type + slot→connector bindings) into a
/// Core run configuration and dispatches it. The Studio never touches the bus or the Core
/// database — only the Core API through <see cref="ICoreClient"/>.
/// </summary>
public sealed class WorkflowAuthoringService(WorkflowTypeCatalog catalog, ICoreClient core)
{
    public async Task<ConfiguredWorkflowDto> ConfigureAsync(ConfigureWorkflow request, CancellationToken ct)
    {
        var type = await catalog.GetByNameAsync(request.WorkflowTypeName, ct)
                   ?? throw new KeyNotFoundException($"workflow type '{request.WorkflowTypeName}' is not registered");

        var connectorIds = (await core.QueryConnectorsAsync(new ConnectorQuery(Take: 500), ct))
            .Items.Select(c => c.Id).ToHashSet();

        foreach (var binding in request.SlotBindings)
        {
            if (type.Slots.All(s => s.Name != binding.SlotName))
                throw new ArgumentException(
                    $"slot '{binding.SlotName}' is not declared by workflow type '{type.Name}'.");
            if (!connectorIds.Contains(binding.ConnectorId))
                throw new ArgumentException($"connector '{binding.ConnectorId}' does not exist in the Core.");
        }

        var boundSlots = request.SlotBindings.Select(b => b.SlotName).ToHashSet(StringComparer.Ordinal);
        var missing = type.Slots.Where(s => !s.Optional && !boundSlots.Contains(s.Name)).Select(s => s.Name).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"required slots not bound: {string.Join(", ", missing)}.");

        var coreBindings = request.SlotBindings
            .Select(b => new SlotBinding(b.SlotName, ConnectorId: b.ConnectorId))
            .ToList();

        var coreConfig = await core.CreateConfigurationAsync(new CreateRunConfiguration(
            request.Name, type.Name, type.PackageUri, request.Context, coreBindings), ct);

        return new ConfiguredWorkflowDto(coreConfig.Id, coreConfig.Name, type.Name);
    }

    public Task<RunAccepted> RunAsync(Guid coreConfigurationId, CancellationToken ct)
        => core.RunConfigurationAsync(coreConfigurationId, ct: ct);
}
