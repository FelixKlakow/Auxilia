using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.AiAgent;

public static class AiAgentInfrastructureExtensions
{
    public static IServiceCollection AddAiAgentInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IAiAgentRegistry, AiAgentRegistry>();
        services.AddSingleton<IAiInference, DefaultAiInference>();
        return services;
    }
}
