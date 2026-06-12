using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>Run lifecycle record; Id is the workflow instance ID assigned at launch.</summary>
public sealed record WorkflowInstanceRecord : IEntity
{
    public Guid Id { get; init; }
    public required string WorkflowType { get; init; }
    public required string State { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>Service ID of the Steering Instance owning this run (failover detection).</summary>
    public Guid? OwnerServiceId { get; init; }
    /// <summary>"OneShot" or "LongLiving" — drives drain-and-replace behaviour.</summary>
    public string Lifetime { get; init; } = "OneShot";
    /// <summary>The manifest's declared outputs as JSON, recorded at registration for artifact persistence.</summary>
    public string? OutputsJson { get; init; }
    /// <summary>The originating dispatch command as JSON, kept for policy-driven re-dispatch.</summary>
    public string? DispatchCommandJson { get; init; }
}
