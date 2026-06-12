using Microsoft.AspNetCore.Components;

namespace Auxilia.BackendService.Dashboard;

/// <summary>One dashboard renderer plug-in: a Blazor component bound to a renderer key.</summary>
public sealed record ViewRendererRegistration(string RendererKey, Type ComponentType);

/// <summary>
/// Resolves the component for a <c>ViewRendering.Custom</c> descriptor's renderer key
/// (ARCHITECTURE §15): workflows ship only the key string; the component is a dashboard-side
/// plug-in registered via <see cref="ViewRendererServiceCollectionExtensions.AddViewRenderer{TComponent}"/>.
/// </summary>
public sealed class ViewRendererRegistry(IEnumerable<ViewRendererRegistration> registrations)
{
    private readonly Dictionary<string, Type> _renderers =
        registrations.ToDictionary(r => r.RendererKey, r => r.ComponentType);

    /// <summary>The component registered for <paramref name="rendererKey"/>, or null when not installed.</summary>
    public Type? Resolve(string? rendererKey)
        => rendererKey is not null && _renderers.TryGetValue(rendererKey, out var type) ? type : null;
}

public static class ViewRendererServiceCollectionExtensions
{
    public static IServiceCollection AddViewRenderer<TComponent>(
        this IServiceCollection services, string rendererKey)
        where TComponent : IComponent
    {
        ArgumentException.ThrowIfNullOrEmpty(rendererKey);
        services.AddSingleton(new ViewRendererRegistration(rendererKey, typeof(TComponent)));
        return services;
    }
}
