using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>Configured signal handler; one record per (workflow type, signal name).</summary>
public sealed record SignalHandlerRecord : IEntity
{
    public Guid Id { get; init; }
    public required string WorkflowType { get; init; }
    public required string SignalName { get; init; }
    public required string HandlerDescriptorJson { get; init; }

    public static Guid IdFor(string workflowType, string signalName)
        => DeterministicGuid.For("signal-handler", workflowType, "", signalName);
}
