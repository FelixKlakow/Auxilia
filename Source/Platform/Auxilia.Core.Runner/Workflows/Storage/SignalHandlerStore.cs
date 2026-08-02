using System.Text.Json;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Runner.Workflows.Storage;

/// <summary>
/// Durable signal handler repository; one record per (workflow type, signal name).
/// Descriptors serialize polymorphically via the <see cref="ISignalHandlerDescriptor"/> contract.
/// </summary>
public sealed class SignalHandlerStore(IDataAccess<SignalHandlerRecord> dataAccess)
{
    public async Task<IReadOnlyList<StoredSignalHandlerConfiguration>> GetHandlersAsync(
        string workflowTypeName, CancellationToken ct = default)
    {
        var query = await dataAccess.ReadAsync(ct);
        return query
            .Where(r => r.WorkflowType == workflowTypeName)
            .ToList()
            .Select(r => new StoredSignalHandlerConfiguration(
                r.SignalName,
                JsonSerializer.Deserialize<ISignalHandlerDescriptor>(r.HandlerDescriptorJson)!))
            .ToList()
            .AsReadOnly();
    }

    public Task UpsertHandlerAsync(
        string workflowTypeName, StoredSignalHandlerConfiguration config, CancellationToken ct = default)
        => dataAccess.SaveAsync(new SignalHandlerRecord
        {
            Id = SignalHandlerRecord.IdFor(workflowTypeName, config.SignalName),
            WorkflowType = workflowTypeName,
            SignalName = config.SignalName,
            HandlerDescriptorJson = JsonSerializer.Serialize(config.HandlerDescriptor)
        }, ct);
}
