using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Client;
using Auxilia.Messaging;
using Auxilia.WorkflowStudio;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auxilia.WorkflowStudio.Tests.ComponentTests;

/// <summary>
/// Boots the Studio via WebApplicationFactory with an in-memory database and a fake Core client,
/// proving the register-type → configure → dispatch HTTP flow drives the Core.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class StudioEndpointTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private FakeCoreClient _core = null!;

    [SetUp]
    public void SetUp()
    {
        _core = new FakeCoreClient();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("PlatformData:Backend", "InMemory");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICoreClient>();
                services.AddSingleton<ICoreClient>(_core);
                // Replace the real broker so booting the host (which starts the trigger schedulers)
                // never touches RabbitMQ.
                services.RemoveAll<IMessageBusClient>();
                services.AddSingleton<IMessageBusClient>(new FakeMessageBusClient());
            });
        });
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task RegisterType_ThenConfigure_ThenRun_DrivesTheCore()
    {
        var client = _factory.CreateClient();
        var connector = _core.AddConnector();

        var register = await client.PostAsJsonAsync("/api/workflow-types", new RegisterWorkflowType(
            "codereview", "Code Review", "docker://cr",
            new[] { new DeclaredSlot("scm", "ISourceControl") }, Array.Empty<string>()));
        register.EnsureSuccessStatusCode();

        var configure = await client.PostAsJsonAsync("/api/configure", new ConfigureWorkflow(
            "cfg", "codereview", new[] { new StudioSlotBinding("scm", connector.Id) }, null));
        Assert.That(configure.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var configured = await configure.Content.ReadFromJsonAsync<ConfiguredWorkflowDto>();
        Assert.That(configured!.WorkflowTypeName, Is.EqualTo("codereview"));

        var run = await client.PostAsync($"/api/configured/{configured.CoreConfigurationId}/run", null);
        run.EnsureSuccessStatusCode();
        Assert.That(_core.RunConfigurationIds.Single(), Is.EqualTo(configured.CoreConfigurationId));
    }

    [Test]
    public async Task Configure_UnknownType_Returns404()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/configure", new ConfigureWorkflow(
            "cfg", "does-not-exist", Array.Empty<StudioSlotBinding>(), null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
