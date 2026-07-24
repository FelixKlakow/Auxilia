using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.AspNetCore.Hosting;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Component test for the "statically configured" path: a configuration supplied through host
/// settings is seeded at startup and runnable through the same dispatch pipeline.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class StaticConfigurationTests : CoreApiComponentTestBase
{
    private const string DummyType = "simple-git-commit-workflow";
    private const string DummyImage = "docker://auxilia-dummy-workflows:system-test";

    protected override void ConfigureHost(IWebHostBuilder builder)
    {
        builder.UseSetting("CoreApi:StaticConfigurations:0:Name", "static-dummy");
        builder.UseSetting("CoreApi:StaticConfigurations:0:WorkflowType", DummyType);
        builder.UseSetting("CoreApi:StaticConfigurations:0:PackageUri", DummyImage);
        builder.UseSetting("CoreApi:StaticConfigurations:0:Context:WORKFLOW_NAME", DummyType);
    }

    [Test]
    public async Task StaticConfiguration_IsSeeded_AndRunnable()
    {
        var client = CreateClient();

        var page = await client.GetFromJsonAsync<PagedResult<RunConfiguration>>("/api/configurations");
        var config = page!.Items.SingleOrDefault(c => c.Name == "static-dummy");
        Assert.That(config, Is.Not.Null, "Static configuration must be seeded at startup.");
        Assert.That(config!.WorkflowType, Is.EqualTo(DummyType));

        var runResp = await client.PostAsync($"/api/configurations/{config.Id}/run", null);
        Assert.That(runResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var command = MessageBus.PublishedMessages
            .Select(m => m.Message).OfType<RunWorkflowCommand>().Single();
        Assert.That(command.WorkflowType, Is.EqualTo(DummyType));
        Assert.That(command.Context["WORKFLOW_NAME"], Is.EqualTo(DummyType));
    }
}
