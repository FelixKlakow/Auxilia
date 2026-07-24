using Auxilia.Core.Api;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Api.Tests;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class RunServiceTests
{
    private static (RunService Service, FakeMessageBusClient Bus, RunConfigurationService Configs) New()
    {
        var bus = new FakeMessageBusClient();
        var configs = new RunConfigurationService(
            new InMemoryDataAccess<CoreRunConfigurationRecord>(), TimeProvider.System);
        var service = new RunService(
            bus, configs, Options.Create(new CoreApiSettings()), NullLogger<RunService>.Instance);
        return (service, bus, configs);
    }

    [Test]
    public async Task RunInline_PublishesCommand_WithoutRequestedBy()
    {
        var (service, bus, _) = New();

        await service.RunInlineAsync(
            new RunRequest("wt", "docker://img",
                new Dictionary<string, string> { ["K"] = "V" }, RequestedBy: Guid.NewGuid()),
            CancellationToken.None);

        var command = bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Single();
        Assert.That(command.WorkflowType, Is.EqualTo("wt"));
        Assert.That(command.WorkflowPackageUri, Is.EqualTo("docker://img"));
        Assert.That(command.RequestedBy, Is.Null,
            "The Core is the auth authority and dispatches with RequestedBy=null.");
    }

    [Test]
    public async Task RunConfiguration_ResolvesTypePackageAndContext()
    {
        var (service, bus, configs) = New();
        var config = await configs.CreateAsync(
            new CreateRunConfiguration("c", "wt", "docker://img",
                new Dictionary<string, string> { ["WORKFLOW_NAME"] = "x" }),
            CancellationToken.None);

        await service.RunConfigurationAsync(config.Id, null, CancellationToken.None);

        var command = bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Single();
        Assert.That(command.WorkflowType, Is.EqualTo("wt"));
        Assert.That(command.WorkflowPackageUri, Is.EqualTo("docker://img"));
        Assert.That(command.Context["WORKFLOW_NAME"], Is.EqualTo("x"));
    }

    [Test]
    public void RunConfiguration_Missing_Throws()
    {
        var (service, _, _) = New();
        Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.RunConfigurationAsync(Guid.NewGuid(), null, CancellationToken.None));
    }
}
