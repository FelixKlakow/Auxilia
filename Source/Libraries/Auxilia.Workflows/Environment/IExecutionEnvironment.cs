using Auxilia.Workflows.Capabilities;

namespace Auxilia.Workflows.Environment;

/// <summary>
/// Slot contract of an <c>environment</c> slot: each binding selects one environment capability
/// (an SDK, a runtime, an OS flavor) the run's container must provide. Bindings are pure
/// selections — no plugin, no credential; the runner composes the container image from them.
/// </summary>
public interface IExecutionEnvironment;

/// <summary>Capability requirement of an environment slot (currently unconstrained).</summary>
public sealed record ExecutionEnvironmentCapabilities : ICapability;
