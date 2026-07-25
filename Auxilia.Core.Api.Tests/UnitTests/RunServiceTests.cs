using System.Security.Cryptography;
using Auxilia.Core.Api;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Api.Tests;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
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
        var connectorStore = new InMemoryDataAccess<CoreConnectorRecord>();
        var connectors = new ConnectorService(
            connectorStore, new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32)), TimeProvider.System);
        var resolver = new SlotCredentialResolver(
            new InMemoryDataAccess<CoreRunResolutionRecord>(), connectors,
            new AuditLog(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System),
            TimeProvider.System, Options.Create(new CoreApiSettings()));
        var accessPolicy = new ConnectorAccessPolicy(connectorStore, new InMemoryDataAccess<PrincipalRecord>());
        var service = new RunService(
            bus, configs, resolver, accessPolicy, Options.Create(new CoreApiSettings()),
            NullLogger<RunService>.Instance);
        return (service, bus, configs);
    }

    [Test]
    public async Task RunInline_PublishesCommand_WithoutRequestedBy()
    {
        var (service, bus, _) = New();

        await service.RunInlineAsync(
            new RunRequest("wt", "docker://img",
                new Dictionary<string, string> { ["K"] = "V" }, RequestedBy: Guid.NewGuid()),
            triggeredBy: Guid.NewGuid(), CancellationToken.None);

        var command = bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Single();
        Assert.That(command.WorkflowType, Is.EqualTo("wt"));
        Assert.That(command.WorkflowPackageUri, Is.EqualTo("docker://img"));
        Assert.That(command.RequestedBy, Is.Null,
            "The Core is the auth authority and dispatches with RequestedBy=null.");
        Assert.That(command.ResolutionToken, Is.Not.Null.And.Not.Empty,
            "Every dispatch mints a run-scoped resolution token for JIT credential resolution.");
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
