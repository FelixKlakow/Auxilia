using System.Collections.Concurrent;
using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows.Views;

/// <summary>
/// Bus-backed view publisher: validates the view is declared, assigns the per-view
/// monotonic sequence, and publishes to the <see cref="ViewDataMessage.ExchangeName"/> exchange.
/// </summary>
public sealed class DefaultViewPublisher(
    IMessageBusClient messageBus,
    Guid instanceId,
    IReadOnlyList<ViewDescriptor> declaredViews) : IViewPublisher
{
    private readonly ConcurrentDictionary<string, long> _sequences = new();
    private bool _exchangeDeclared;

    public async Task PublishAsync<TItem>(string viewName, TItem item, CancellationToken ct = default)
    {
        if (!declaredViews.Any(v => v.Name == viewName))
            throw new InvalidOperationException(
                $"View '{viewName}' is not declared by this workflow. Declare it via DeclaresView.");

        if (!_exchangeDeclared)
        {
            await messageBus.DeclareExchangeAsync(ViewDataMessage.ExchangeName, ct);
            _exchangeDeclared = true;
        }

        var sequence = _sequences.AddOrUpdate(viewName, 1, (_, current) => current + 1);
        await messageBus.PublishToExchangeAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(instanceId, viewName, sequence, JsonSerializer.Serialize(item)), ct);
    }
}
