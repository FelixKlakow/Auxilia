using System.Net.Http.Headers;
using System.Net.Http.Json;
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
            // Component tests run against a fake bus with no runner heartbeats — queue freely.
            // Fixtures covering the no-runner guard flip this back to false.
            builder.UseSetting("CoreApi:AllowDispatchWithoutRunner", "true");
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

    /// <summary>
    /// Registers <paramref name="workflowType"/> as an Active registry entry (register + approve —
    /// a docker package has no signature, so approval is the trust act). Runs dispatch by type only,
    /// so most run-path tests need this before acting.
    /// </summary>
    protected async Task RegisterActiveTypeAsync(
        HttpClient adminClient, string workflowType, string packageUri = "docker://auxilia-dummy-workflows:system-test")
    {
        var register = await adminClient.PostAsJsonAsync(
            "/api/workflow-types",
            new Auxilia.Core.Contracts.RegisterWorkflowTypeRequest(workflowType, packageUri));
        Assert.That(register.IsSuccessStatusCode, Is.True,
            $"workflow-type registration failed: {await register.Content.ReadAsStringAsync()}");
        var approve = await adminClient.PostAsync(
            $"/api/workflow-types/{Uri.EscapeDataString(workflowType)}/approve", null);
        Assert.That(approve.IsSuccessStatusCode, Is.True,
            $"workflow-type approval failed: {await approve.Content.ReadAsStringAsync()}");
    }
}
