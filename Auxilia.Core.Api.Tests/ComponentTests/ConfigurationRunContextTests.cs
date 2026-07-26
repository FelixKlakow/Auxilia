using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// The stored-configuration dispatch endpoint accepts an optional runtime context (the path
/// WorkflowStudio's triggers use to pass artifact/work-item/mail fields): the supplied context is
/// merged over the configuration's stored context and the dispatched run carries the result.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class ConfigurationRunContextTests : CoreApiComponentTestBase
{
    private const string DummyType = "simple-git-commit-workflow";
    private const string DummyImage = "docker://auxilia-dummy-workflows:system-test";

    private async Task<Guid> SeedConfigurationAsync()
    {
        var configurations = Factory.Services.GetRequiredService<RunConfigurationService>();
        var config = await configurations.CreateAsync(new CreateRunConfiguration(
            "cfg-" + Guid.NewGuid().ToString("N"), DummyType, DummyImage,
            new Dictionary<string, string> { ["WORKFLOW_NAME"] = DummyType }), CancellationToken.None);
        return config.Id;
    }

    [Test]
    public async Task RunWithContextBody_MergesContextIntoTheDispatchedRun()
    {
        var client = CreateClient();
        var configId = await SeedConfigurationAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/configurations/{configId}/run",
            new Dictionary<string, string> { ["ArtifactId"] = "artifact-42", ["WorkItemId"] = "WI-7" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var command = MessageBus.PublishedMessages.Select(m => m.Message)
            .OfType<RunWorkflowCommand>().Single();
        Assert.That(command.Context["ArtifactId"], Is.EqualTo("artifact-42"));
        Assert.That(command.Context["WorkItemId"], Is.EqualTo("WI-7"));
        Assert.That(command.Context["WORKFLOW_NAME"], Is.EqualTo(DummyType),
            "The configuration's stored context must survive the merge.");
    }

    [Test]
    public async Task RunWithoutContextBody_StillDispatches()
    {
        var client = CreateClient();
        var configId = await SeedConfigurationAsync();

        var response = await client.PostAsync($"/api/configurations/{configId}/run", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var command = MessageBus.PublishedMessages.Select(m => m.Message)
            .OfType<RunWorkflowCommand>().Single();
        Assert.That(command.Context["WORKFLOW_NAME"], Is.EqualTo(DummyType));
    }
}
