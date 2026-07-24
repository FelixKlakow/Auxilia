using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Client;

public static class CoreClientExtensions
{
    /// <summary>
    /// Registers <see cref="ICoreClient"/> against a Core API base address, authenticated with an
    /// API key issued by the Core.
    /// </summary>
    public static IServiceCollection AddCoreClient(
        this IServiceCollection services, string baseAddress, string apiKey)
    {
        services.AddHttpClient<ICoreClient, CoreClient>(http =>
        {
            http.BaseAddress = new Uri(baseAddress);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        });
        return services;
    }

    /// <summary>Wraps an existing (already-authenticated) HttpClient — for tests and simple hosts.</summary>
    public static ICoreClient Create(HttpClient http) => new CoreClient(http);
}
