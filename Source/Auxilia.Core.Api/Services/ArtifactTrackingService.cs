using Auxilia.Core.Api.Data;
using Auxilia.Messaging;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Mirrors each <see cref="ArtifactPersistedEvent"/> into the Core's own
/// <see cref="CoreArtifactRecord"/> store (its own exclusive queue — the live
/// <see cref="ArtifactStreamPublisher"/> gets its own copy), so the client-surface artifact
/// query works without the Core ever reading the runner's index. Keyed by artifact id —
/// re-delivery is an idempotent upsert.
/// </summary>
public sealed class ArtifactTrackingService(
    IMessageBusClient bus,
    IDataAccess<CoreArtifactRecord> artifacts,
    ILogger<ArtifactTrackingService> logger) : IHostedService
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await bus.DeclareExchangeAsync(ArtifactPersistedEvent.ExchangeName, cancellationToken);
        _subscription = await bus.SubscribeToExchangeAsync<ArtifactPersistedEvent>(
            ArtifactPersistedEvent.ExchangeName, HandleAsync, cancellationToken);
        logger.LogInformation("ArtifactTrackingService listening on {Exchange}.",
            ArtifactPersistedEvent.ExchangeName);
    }

    private Task HandleAsync(ArtifactPersistedEvent persisted, CancellationToken ct)
        => artifacts.SaveAsync(new CoreArtifactRecord
        {
            Id = persisted.ArtifactId,
            ArtifactType = persisted.ArtifactType,
            WorkflowType = persisted.WorkflowType,
            WorkItemId = persisted.WorkItemId,
            RunInstanceId = persisted.RunInstanceId,
            Version = persisted.Version,
            ContentHash = persisted.ContentHash,
            SizeBytes = persisted.SizeBytes,
            CreatedUtc = persisted.TimestampUtc
        }, ct);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}
