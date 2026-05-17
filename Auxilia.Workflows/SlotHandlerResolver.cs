namespace Auxilia.Workflows;

public sealed class SlotHandlerResolver : ISlotHandlerResolver
{
    private readonly Dictionary<string, ISlotHandler> _handlers = new();

    public void Register(string providerType, ISlotHandler handler)
        => _handlers[providerType] = handler;

    public ISlotHandler Resolve(string providerType)
    {
        if (_handlers.TryGetValue(providerType, out var handler))
            return handler;
        throw new KeyNotFoundException($"No slot handler registered for provider type '{providerType}'.");
    }
}
