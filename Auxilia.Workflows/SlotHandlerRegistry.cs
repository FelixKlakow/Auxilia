namespace Auxilia.Workflows;

public static class SlotHandlerRegistry
{
    private static readonly Dictionary<string, ISlotHandler> _handlers = new();

    public static void Register(string providerType, ISlotHandler handler)
        => _handlers[providerType] = handler;

    public static ISlotHandler Resolve(string providerType)
    {
        if (_handlers.TryGetValue(providerType, out var handler))
            return handler;
        throw new KeyNotFoundException($"No slot handler registered for provider type '{providerType}'.");
    }
}
