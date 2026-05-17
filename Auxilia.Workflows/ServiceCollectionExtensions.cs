using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSlotHandlerResolver(this IServiceCollection services)
        => services.AddScoped<ISlotHandlerResolver, SlotHandlerResolver>();
}
