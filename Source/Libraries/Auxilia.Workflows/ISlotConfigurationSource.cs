namespace Auxilia.Workflows;

/// <summary>
/// A slot's configuration fetched AGAIN while the run goes on: whatever the slot's connector
/// holds now — a credential its owner replaced, or one the Core refreshed — delivered exactly
/// like the first activation (resolved by the Core for this run only, encrypted for this
/// instance, audited). Registered for runs whose slots activate just-in-time; absent where
/// configurations arrive bundled (development hosts, tests), so a handler resolving it
/// optionally keeps the configuration it was registered with.
/// </summary>
public interface ISlotConfigurationSource
{
    Task<SlotConfiguration> FetchAsync(string slotName, CancellationToken ct = default);
}
