using System.Security.Cryptography;
using Auxilia.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auxilia.Core.Api.Tests;

/// <summary>
/// Boots the Core API via <see cref="WebApplicationFactory{TEntryPoint}"/> with an in-memory
/// database, AES-GCM settings protection, and a <see cref="FakeMessageBusClient"/>. A fresh
/// factory per test keeps published-message assertions isolated.
/// </summary>
public abstract class CoreApiComponentTestBase
{
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

    protected HttpClient CreateClient() => Factory.CreateClient();
}
