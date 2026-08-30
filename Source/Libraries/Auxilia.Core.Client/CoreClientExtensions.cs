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

    /// <summary>First reconnect delay of a dropped stream; doubles per attempt.</summary>
    public int StreamReconnectInitialBackoffSeconds { get; set; } = 1;

    /// <summary>Reconnect backoff cap; the delay resets to the initial value on every received event.</summary>
    public int StreamReconnectMaxBackoffSeconds { get; set; } = 30;

    /// <summary>
    /// A stream silent for longer than this (no events AND no server keepalive pings) is treated
    /// as a dead connection and reconnected. Also bounds the connect phase: a connection that
    /// never produces response headers counts as silent and is retried. Must exceed the Core's
    /// SseKeepaliveSeconds; 0 disables.
    /// </summary>
    public int StreamIdleTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Per-call timeout of non-streaming requests. Replaces <see cref="HttpClient.Timeout"/>,
    /// which the client disables because it would sever long-lived SSE streams. 0 disables.
    /// </summary>
    public int UnaryTimeoutSeconds { get; set; } = 100;
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
            })
            .AddTypedClient<ICoreClient>(http => new CoreClient(http, options));
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
            .AddTypedClient<ICoreClient>(http => new CoreClient(http, options))
            .AddHttpMessageHandler(sp =>
                new CoreCallerTokenHandler(tokenProviderFactory(sp), options.ApiKey));
    }

    /// <summary>Wraps an existing (already-authenticated) HttpClient — for tests and simple hosts.</summary>
    public static ICoreClient Create(HttpClient http, CoreClientOptions? options = null)
        => new CoreClient(http, options);
}
