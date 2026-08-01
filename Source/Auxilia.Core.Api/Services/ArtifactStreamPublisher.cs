using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Bridges the bus artifact feed to the SSE <see cref="ArtifactStreamBroker"/>: subscribes to
/// the <c>workflow.artifact-events</c> fanout (its own exclusive queue — the persisting
/// <see cref="ArtifactTrackingService"/> gets its own copy) and re-emits each event as an
/// <see cref="ArtifactStreamEvent"/>. Live push only; persistence stays with the tracker.
/// This is what lets chaining clients react to artifacts WITHOUT a bus subscription.
/// </summary>
public sealed class ArtifactStreamPublisher(
    IMessageBusClient bus,
    ArtifactStreamBroker broker,
    ILogger<ArtifactStreamPublisher> logger) : IHostedService
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await bus.DeclareExchangeAsync(ArtifactPersistedEvent.ExchangeName, cancellationToken);
        _subscription = await bus.SubscribeToExchangeAsync<ArtifactPersistedEvent>(
            ArtifactPersistedEvent.ExchangeName, HandleAsync, cancellationToken);
        logger.LogInformation("ArtifactStreamPublisher listening on {Exchange}.",
            ArtifactPersistedEvent.ExchangeName);
    }

    private Task HandleAsync(ArtifactPersistedEvent persisted, CancellationToken ct)
    {
        broker.Publish(new ArtifactStreamEvent(
            new ArtifactDto(
                persisted.ArtifactId, persisted.ArtifactType, persisted.WorkflowType,
                persisted.WorkItemId, persisted.RunInstanceId, persisted.Version,
                persisted.ContentHash, persisted.SizeBytes, persisted.TimestampUtc),
            persisted.TimestampUtc));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}
