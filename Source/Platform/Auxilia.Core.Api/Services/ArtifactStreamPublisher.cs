using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Bridges the bus artifact feed to the SSE <see cref="ArtifactStreamBroker"/> with SELECTIVE
/// ingest: subscribes to the <c>workflow.artifacts</c> topic exchange with no initial bindings
/// and, as <see cref="IArtifactStreamBindingListener"/>, binds/unbinds the artifact-type routing
/// keys this node's open streams filter on (an unfiltered stream binds the match-all key).
/// This is what lets chaining clients react to artifacts WITHOUT a bus subscription.
/// </summary>
public sealed class ArtifactStreamPublisher(
    IMessageBusClient bus,
    ArtifactStreamBroker broker,
    ILogger<ArtifactStreamPublisher> logger) : IHostedService, IArtifactStreamBindingListener
{
    private const string MatchAll = "#";

    private ITopicSubscription? _subscription;

    // Serializes all binding mutations and guards the audience refcounts (key = binding key).
    private readonly SemaphoreSlim _bindGate = new(1, 1);
    private readonly Dictionary<string, int> _audience = new(StringComparer.Ordinal);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await bus.DeclareTopicExchangeAsync(ArtifactPersistedEvent.ExchangeName, cancellationToken);
        _subscription = await bus.SubscribeToTopicExchangeAsync<ArtifactPersistedEvent>(
            ArtifactPersistedEvent.ExchangeName, [], HandleAsync, cancellationToken);
        broker.SetListener(this);
        logger.LogInformation("ArtifactStreamPublisher listening selectively on {Exchange}.",
            ArtifactPersistedEvent.ExchangeName);
    }

    public async Task ArtifactInterestAddedAsync(string? artifactType, CancellationToken ct)
    {
        var key = BindingKeyFor(artifactType);
        // A cancellation while waiting for the gate has registered NOTHING — the broker discards
        // the failed subscription without an unsubscribe callback, so there is nothing to undo.
        await _bindGate.WaitAsync(ct);
        try
        {
            var count = _audience.GetValueOrDefault(key) + 1;
            _audience[key] = count;
            if (count == 1)
            {
                try
                {
                    await _subscription!.AddBindingAsync(key, ct);
                }
                catch
                {
                    // ROLL BACK under the gate: the first audience of this key failed to bind, and
                    // the broker will not call the removed-callback for a failed subscribe.
                    _audience.Remove(key);
                    await TryRemoveBindingLockedAsync(key);
                    throw;
                }
            }
        }
        finally { _bindGate.Release(); }
    }

    private async Task TryRemoveBindingLockedAsync(string key)
    {
        try { await _subscription!.RemoveBindingAsync(key); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to roll back the '{Key}' artifact binding after a failed subscribe.", key);
        }
    }

    public async Task ArtifactInterestRemovedAsync(string? artifactType)
    {
        var key = BindingKeyFor(artifactType);
        try
        {
            await _bindGate.WaitAsync();
            try
            {
                var count = _audience.GetValueOrDefault(key) - 1;
                if (count > 0)
                {
                    _audience[key] = count;
                    return;
                }
                _audience.Remove(key);
                await _subscription!.RemoveBindingAsync(key);
            }
            finally { _bindGate.Release(); }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove the '{Key}' artifact binding.", key);
        }
    }

    private static string BindingKeyFor(string? artifactType)
        => artifactType is null ? MatchAll : ArtifactPersistedEvent.RoutingKeyFor(artifactType);

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
