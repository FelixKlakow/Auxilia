namespace Auxilia.Workflows;

public sealed class SlotHandlerResolver : ISlotHandlerResolver
{
    // Provider types are case-insensitive platform-wide (they key deterministic record ids
    // lowercase-invariant); the in-container resolver must agree with the Core and the runner.
    private readonly Dictionary<string, ISlotHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string providerType, ISlotHandler handler)
        => _handlers[providerType] = handler;

    public ISlotHandler Resolve(string providerType)
    {
        if (_handlers.TryGetValue(providerType, out var handler))
            return handler;
        throw new KeyNotFoundException($"No slot handler registered for provider type '{providerType}'.");
    }
}
