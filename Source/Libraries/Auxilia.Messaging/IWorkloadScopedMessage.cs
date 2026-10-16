namespace Auxilia.Messaging;

/// <summary>
///     A message that speaks for ONE workload instance. Broker implementations attribute every
///     delivery to the identity that published it and drop a scoped message whose publisher is a
///     workload identity other than the instance it names (<see cref="WorkloadBusIdentity" />) —
///     a workload can only ever speak for itself, whatever its body claims.
/// </summary>
public interface IWorkloadScopedMessage
{
    Guid WorkflowInstanceId { get; }
}
