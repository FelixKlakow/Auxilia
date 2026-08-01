using Auxilia.Workflows.Client.Authoring;
using Auxilia.Workflows.Client.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auxilia.Workflows.Client;

/// <summary>Engine pacing knobs; the defaults suit a server host.</summary>
public sealed class WorkflowClientOptions
{
    public int SchedulerIntervalSeconds { get; set; } = 10;
    public int StreamReconnectMaxBackoffSeconds { get; set; } = 30;
}

public static class WorkflowClientExtensions
{
    /// <summary>
    /// Registers the workflow-domain conveniences on top of an already-registered
    /// <c>ICoreClient</c> (<c>AddCoreClient</c>): authoring, the trigger store (in-memory
    /// unless the host registered its own), and both trigger engines. The engines are plain
    /// singletons — a desktop host drives them directly (<c>StartAsync</c>/<c>TickAsync</c>);
    /// a server host adds <see cref="AddWorkflowClientHosting"/> to run them as hosted services.
    /// </summary>
    public static IServiceCollection AddWorkflowClient(
        this IServiceCollection services, Action<WorkflowClientOptions>? configure = null)
    {
        services.AddOptions<WorkflowClientOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITriggerStore, InMemoryTriggerStore>();
        services.TryAddSingleton<WorkflowAuthoring>();
        services.TryAddSingleton<ScheduledTriggerEngine>();
        services.TryAddSingleton<ArtifactChainingEngine>();
        return services;
    }

    /// <summary>Runs both trigger engines as hosted services (server hosts).</summary>
    public static IServiceCollection AddWorkflowClientHosting(this IServiceCollection services)
    {
        services.AddHostedService(sp => sp.GetRequiredService<ScheduledTriggerEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<ArtifactChainingEngine>());
        return services;
    }
}
