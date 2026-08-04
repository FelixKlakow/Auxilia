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
    /// <summary>Service ID of the Core.Runner owning this run (failover detection).</summary>
    public Guid? OwnerServiceId { get; init; }
    /// <summary>"OneShot" or "LongLiving" — drives drain-and-replace behaviour.</summary>
    public string Lifetime { get; init; } = "OneShot";
    /// <summary>The manifest's declared outputs as JSON, recorded at registration for artifact persistence.</summary>
    public string? OutputsJson { get; init; }
    /// <summary>The manifest's declared views as JSON, recorded at registration for view persistence and replay.</summary>
    public string? ViewsJson { get; init; }
    /// <summary>The originating dispatch command as JSON, kept for policy-driven re-dispatch.</summary>
    public string? DispatchCommandJson { get; init; }
    /// <summary>The named workflow configuration this run was dispatched from, when any.</summary>
    public Guid? WorkflowConfigurationId { get; init; }
    /// <summary>Natural-key name of the dispatching configuration, for run-history grouping.</summary>
    public string? WorkflowConfigurationName { get; init; }

    /// <summary>
    /// "host:port" of the run's published interactive web terminal (ttyd), set at launch for
    /// workflows declaring one; the dashboard proxies it — authenticated — to the run owner.
    /// </summary>
    public string? TerminalEndpoint { get; init; }

    /// <summary>
    /// Docker container id, persisted at creation so a restarted runner can RE-ADOPT a still
    /// running container (re-attach its exit watcher) instead of killing it.
    /// </summary>
    public string? ContainerId { get; init; }

    /// <summary>
    /// The instance token, protected via <see cref="Protection.ISettingsProtector"/> — restored
    /// into the in-memory token registry on re-adoption so the in-container SDK's registration
    /// and JIT slot activations keep validating across a runner restart. Never stored plaintext.
    /// </summary>
    public string? ProtectedInstanceToken { get; init; }
}
