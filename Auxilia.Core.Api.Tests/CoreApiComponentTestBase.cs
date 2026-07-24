using System.Net.Http.Headers;
using System.Security.Cryptography;
using Auxilia.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auxilia.Core.Api.Tests;

/// <summary>
/// Boots the Core API via <see cref="WebApplicationFactory{TEntryPoint}"/> with an in-memory
/// database, AES-GCM settings protection, a <see cref="FakeMessageBusClient"/>, and a bootstrap
/// Administrator API key. A fresh factory per test keeps published-message assertions isolated.
/// </summary>
public abstract class CoreApiComponentTestBase
{
    protected const string TestApiKey = "aux-test-key-0123456789abcdef";

    protected WebApplicationFactory<Program> Factory = null!;
    protected FakeMessageBusClient MessageBus = null!;

    [SetUp]
    public void CreateFactory()
    {
        MessageBus = new FakeMessageBusClient();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // UseSetting (not ConfigureAppConfiguration) so values are visible while Program.cs
            // binds settings during the WebApplicationBuilder phase.
            builder.UseSetting("PlatformData:Backend", "InMemory");
            builder.UseSetting("PlatformData:ProtectionKeyBase64",
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("CoreSecurity:BootstrapApiKey", TestApiKey);
            ConfigureHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMessageBusClient>();
                services.AddSingleton<IMessageBusClient>(MessageBus);
            });
        });

        // Force host start so hosted services (RunTrackingService) subscribe before the test acts.
        _ = Factory.Server;
    }

    /// <summary>Fixture hook to add host settings (e.g. static configurations) before build.</summary>
    protected virtual void ConfigureHost(IWebHostBuilder builder) { }

    [TearDown]
    public void DisposeFactory() => Factory.Dispose();

    /// <summary>An HTTP client authenticated as the bootstrap Administrator.</summary>
    protected HttpClient CreateClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestApiKey);
        return client;
    }

    /// <summary>An unauthenticated HTTP client (for auth-gate assertions).</summary>
    protected HttpClient CreateAnonymousClient() => Factory.CreateClient();
}
