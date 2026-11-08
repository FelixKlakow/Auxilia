using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows.Views;

/// <summary>
/// Bus-backed view publisher: validates the view is declared, assigns the run-wide sequence, and
/// publishes to the <see cref="ViewDataMessage.ExchangeName"/> exchange (platform topology,
/// declared by the runner — a workload identity cannot declare exchanges).
/// <para>
/// ONE counter across all of the run's views (1, 2, 3, …), assigned and published under one gate:
/// the bus carries the items in sequence order and a consumer that misses some can tell exactly
/// which — the Core resumes a client's stream from it. A publish that throws still consumes its
/// sequence: a reused number would let a second item overwrite the first wherever the sequence is
/// a key, while a gap is detected and reported.
/// </para>
/// </summary>
public sealed class DefaultViewPublisher(
    IMessageBusClient messageBus,
    Guid instanceId,
    IReadOnlyList<ViewDescriptor> declaredViews) : IViewPublisher
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _sequence;

    /// <summary>The last sequence assigned — the number of items published (or attempted) so far.</summary>
    public long LastSequence => Interlocked.Read(ref _sequence);

    public async Task PublishAsync<TItem>(string viewName, TItem item, CancellationToken ct = default)
    {
        if (!declaredViews.Any(v => v.Name == viewName))
            throw new InvalidOperationException(
                $"View '{viewName}' is not declared by this workflow. Declare it via DeclaresView.");

        var payload = JsonSerializer.Serialize(item);
        await _gate.WaitAsync(ct);
        try
        {
            var sequence = Interlocked.Increment(ref _sequence);
            await messageBus.PublishToTopicExchangeAsync(ViewDataMessage.ExchangeName,
                ViewDataMessage.RoutingKeyFor(instanceId),
                new ViewDataMessage(instanceId, viewName, sequence, payload), ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
