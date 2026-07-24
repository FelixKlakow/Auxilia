using System.Text.Json;
using Auxilia.PlatformData;
using Auxilia.UniversalDataAccess;
using Auxilia.WorkflowStudio.Data;

namespace Auxilia.WorkflowStudio.Services;

/// <summary>The Studio's catalog of workflow types (its own database).</summary>
public sealed class WorkflowTypeCatalog(IDataAccess<StudioWorkflowTypeRecord> store)
{
    public async Task<WorkflowTypeDto> RegisterAsync(RegisterWorkflowType request, CancellationToken ct)
    {
        var record = new StudioWorkflowTypeRecord
        {
            Id = DeterministicGuid.For("studio-workflow-type", request.Name),
            Name = request.Name,
            DisplayName = request.DisplayName,
            PackageUri = request.PackageUri,
            SlotsJson = JsonSerializer.Serialize(request.Slots),
            ContextKeysJson = JsonSerializer.Serialize(request.ContextKeys)
        };
        await store.SaveAsync(record, ct);
        return ToDto(record);
    }

    public async Task<WorkflowTypeDto?> GetByNameAsync(string name, CancellationToken ct)
        => await store.ReadAsync(DeterministicGuid.For("studio-workflow-type", name), ct) is { } r ? ToDto(r) : null;

    public async Task<IReadOnlyList<WorkflowTypeDto>> ListAsync(CancellationToken ct)
        => (await store.ReadAsync(ct)).AsEnumerable()
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto).ToList();

    private static WorkflowTypeDto ToDto(StudioWorkflowTypeRecord r) => new(
        r.Id, r.Name, r.DisplayName, r.PackageUri,
        JsonSerializer.Deserialize<List<DeclaredSlot>>(r.SlotsJson) ?? new List<DeclaredSlot>(),
        JsonSerializer.Deserialize<List<string>>(r.ContextKeysJson) ?? new List<string>());
}
