namespace Auxilia.Messaging;

/// <summary>
///     A message only the platform may publish (commands to runners, responses to workloads,
///     lifecycle facts, audit). Broker implementations drop a delivery of such a message whose
///     publisher is a workload identity — or is unattributed — whatever exchange carried it.
/// </summary>
public interface IPlatformOnlyMessage;
