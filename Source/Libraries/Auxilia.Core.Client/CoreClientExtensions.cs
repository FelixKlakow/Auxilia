using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Client;

/// <summary>Configuration for the Core client — bindable from an app's configuration section.</summary>
public sealed class CoreClientOptions
{
    /// <summary>The Core API base address, e.g. <c>https://core.internal:8443</c>.</summary>
    public string BaseAddress { get; set; } = "";

    /// <summary>An API key issued by the Core for this integration (sent as a bearer token).</summary>
    public string ApiKey { get; set; } = "";
}

public static class CoreClientExtensions
{
    /// <summary>
    /// Registers <see cref="ICoreClient"/> against a Core API base address, authenticated with an
    /// API key issued by the Core. Returns the <see cref="IHttpClientBuilder"/> so the host can add
    /// resilience/handlers (e.g. <c>.AddStandardResilienceHandler()</c>).
    /// </summary>
    public static IHttpClientBuilder AddCoreClient(
        this IServiceCollection services, string baseAddress, string apiKey)
        => services.AddCoreClient(options =>
        {
            options.BaseAddress = baseAddress;
            options.ApiKey = apiKey;
        });

    /// <summary>Registers <see cref="ICoreClient"/> from <see cref="CoreClientOptions"/> (e.g. bound from config).</summary>
    public static IHttpClientBuilder AddCoreClient(
        this IServiceCollection services, Action<CoreClientOptions> configure)
    {
        var options = new CoreClientOptions();
        configure(options);
        return services.AddHttpClient<ICoreClient, CoreClient>(http =>
        {
            http.BaseAddress = new Uri(options.BaseAddress);
            if (!string.IsNullOrEmpty(options.ApiKey))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        });
    }

    /// <summary>
    /// Registers <see cref="ICoreClient"/> that authenticates <b>per request</b> as the current caller:
    /// a <see cref="CoreCallerTokenHandler"/> resolves the supplied <see cref="ICoreCallerTokenProvider"/>
    /// and sets a per-request bearer (e.g. the signed-in console user's Core-minted token). When the
    /// provider yields no token, it falls back to <see cref="CoreClientOptions.ApiKey"/> — so a host can
    /// use the same client for both delegated (user) and service (app-key) calls. The provider is
    /// resolved from DI via <paramref name="tokenProviderFactory"/>, letting it read per-request state
    /// (e.g. an <c>IHttpContextAccessor</c>).
    /// </summary>
    public static IHttpClientBuilder AddCoreClient(
        this IServiceCollection services,
        Action<CoreClientOptions> configure,
        Func<IServiceProvider, ICoreCallerTokenProvider> tokenProviderFactory)
    {
        var options = new CoreClientOptions();
        configure(options);
        return services.AddHttpClient<ICoreClient, CoreClient>(http =>
            {
                http.BaseAddress = new Uri(options.BaseAddress);
                // No static Authorization header — CoreCallerTokenHandler sets it per request.
            })
            .AddHttpMessageHandler(sp =>
                new CoreCallerTokenHandler(tokenProviderFactory(sp), options.ApiKey));
    }

    /// <summary>Wraps an existing (already-authenticated) HttpClient — for tests and simple hosts.</summary>
    public static ICoreClient Create(HttpClient http) => new CoreClient(http);
}
