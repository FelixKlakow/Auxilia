using System.Net;
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
    private ProviderCatalogService _providerCatalog = null!;

    private (RunService Service, FakeMessageBusClient Bus, RunConfigurationService Configs,
        WorkflowTypeRegistryService Registry, RunnerLivenessTracker Liveness) New(bool allowDispatchWithoutRunner = true)
    {
        var bus = new FakeMessageBusClient();
        var configs = new RunConfigurationService(
            new InMemoryDataAccess<CoreRunConfigurationRecord>(), TimeProvider.System);
        var protector = new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32));
        var connectorStore = new InMemoryDataAccess<CoreConnectorRecord>();
        var connectors = new ConnectorService(connectorStore, protector, TimeProvider.System);
        var delegatedTokens = new DelegatedTokenStore(
            new InMemoryDataAccess<DelegatedUserTokenRecord>(), protector, TimeProvider.System);
        var resolver = new SlotCredentialResolver(
            new InMemoryDataAccess<CoreRunResolutionRecord>(), connectors, delegatedTokens,
            new NullDelegatedTokenExchange(),
            new AuditLog(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System),
            TimeProvider.System, Options.Create(new CoreApiSettings()));
        var accessPolicy = new ConnectorAccessPolicy(connectorStore, new InMemoryDataAccess<PrincipalRecord>());
        var liveness = new RunnerLivenessTracker();
        var typeStore = new InMemoryDataAccess<CoreWorkflowTypeRecord>();
        var registry = new WorkflowTypeRegistryService(
            typeStore,
            new StubHttpClientFactory(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
            Options.Create(new CoreApiSettings()),
            new AuditLog(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System),
            TimeProvider.System, NullLogger<WorkflowTypeRegistryService>.Instance);
        var providerCatalog = _providerCatalog = new ProviderCatalogService(
            new InMemoryDataAccess<SlotProviderRecord>(),
            new InMemoryDataAccess<ProviderCatalogRecord>(),
            new AuditLog(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System));
        var service = new RunService(
            bus, configs, registry, new WorkflowSchemaReadService(typeStore),
            providerCatalog, resolver, accessPolicy, connectors, liveness,
            TimeProvider.System,
            Options.Create(new CoreApiSettings { AllowDispatchWithoutRunner = allowDispatchWithoutRunner }),
            NullLogger<RunService>.Instance);
        return (service, bus, configs, registry, liveness);
    }

    private static Task SeedActiveTypeAsync(
        WorkflowTypeRegistryService registry, string type, string packageUri, string? schemaJson = null)
        => registry.EnsureSeededAsync(
            new StaticWorkflowType { WorkflowType = type, PackageUri = packageUri, SchemaJson = schemaJson },
            CancellationToken.None);

    /// <summary>A schema whose coding-agent slot narrows the admissible provider types.</summary>
    private static string NarrowedSchemaJson() => System.Text.Json.JsonSerializer.Serialize(
        new Auxilia.Workflows.WorkflowSchema(
            "wt",
            [new Auxilia.Workflows.SlotDefinition("coding-agent", null)
                { Contract = "ICodingAgent", ProviderTypes = ["claude-code-cli"] }],
            []));

    [Test]
    public async Task RunInline_PublishesCommand_WithRegistryPackage_WithoutRequestedBy()
    {
        var (service, bus, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img");

        await service.RunInlineAsync(
            new RunRequest("wt",
                new Dictionary<string, string> { ["K"] = "V" }, RequestedBy: Guid.NewGuid()),
            triggeredBy: Guid.NewGuid(), CancellationToken.None);

        var command = bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Single();
        Assert.That(command.WorkflowType, Is.EqualTo("wt"));
        Assert.That(command.WorkflowPackageUri, Is.EqualTo("docker://img"),
            "The registry — not the caller — supplies the package coordinate.");
        Assert.That(command.RequestedBy, Is.Null,
            "The Core is the auth authority and dispatches with RequestedBy=null.");
        Assert.That(command.ResolutionToken, Is.Not.Null.And.Not.Empty,
            "Every dispatch mints a run-scoped resolution token for JIT credential resolution.");
    }

    [Test]
    public void RunInline_UnregisteredType_Throws()
    {
        var (service, _, _, _, _) = New();
        Assert.ThrowsAsync<KeyNotFoundException>(() => service.RunInlineAsync(
            new RunRequest("ghost"), triggeredBy: null, CancellationToken.None));
    }

    [Test]
    public async Task RunInline_PendingType_Throws()
    {
        var (service, _, _, registry, _) = New();
        // A docker registration has no verifiable signature — it enters Pending, not Active.
        var outcome = await registry.RegisterAsync(
            new RegisterWorkflowTypeRequest("wt", "docker://img"), null, CancellationToken.None);
        Assert.That(outcome.Registration!.Status, Is.EqualTo(WorkflowTypeStatus.Pending));

        Assert.ThrowsAsync<InvalidOperationException>(() => service.RunInlineAsync(
            new RunRequest("wt"), triggeredBy: null, CancellationToken.None));
    }

    [Test]
    public async Task RunInline_BindingOutsideTheSlotsProviderTypeNarrowing_Throws()
    {
        var (service, _, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img", NarrowedSchemaJson());

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => service.RunInlineAsync(
            new RunRequest("wt",
                SlotBindings: [new SlotBinding("coding-agent", ProviderType: "github-copilot-cli")]),
            triggeredBy: null, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("does not admit provider 'github-copilot-cli'"));
    }

    [Test]
    public async Task RunInline_BindingWithinTheSlotsProviderTypeNarrowing_Dispatches()
    {
        var (service, bus, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img", NarrowedSchemaJson());

        await service.RunInlineAsync(
            new RunRequest("wt",
                SlotBindings: [new SlotBinding("coding-agent", ProviderType: "claude-code-cli")]),
            triggeredBy: null, CancellationToken.None);

        Assert.That(
            bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Count(),
            Is.EqualTo(1));
    }

    [Test]
    public async Task RunInline_EnvironmentComposingBindings_RideAsCapabilities_NotAsPlugins()
    {
        var (service, bus, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img");
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "dotnet-10", "environment", null, ["exec-env"], [], ComposesEnvironment: true),
            CancellationToken.None);

        await service.RunInlineAsync(
            new RunRequest("wt", SlotBindings:
            [
                new SlotBinding("environment", ProviderType: "dotnet-10"),
                new SlotBinding("environment", ProviderType: "dotnet-10"), // duplicates collapse
            ]),
            triggeredBy: null, CancellationToken.None);

        var command = bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(command.EnvironmentCapabilities, Is.EqualTo(new[] { "dotnet-10" }),
                "an environment binding is a capability selection, deduplicated");
            Assert.That(command.SlotProviderTypes, Is.Null.Or.Empty,
                "environment capabilities ship no slot plugin");
        });
    }

    [Test]
    public async Task RunConfiguration_ResolvesTypePackageAndContext()
    {
        var (service, bus, configs, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img");
        var config = await configs.CreateAsync(
            new CreateRunConfiguration("c", "wt",
                new Dictionary<string, string> { ["WORKFLOW_NAME"] = "x" }),
            CancellationToken.None);

        await service.RunConfigurationAsync(config.Id, null, null, CancellationToken.None);

        var command = bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Single();
        Assert.That(command.WorkflowType, Is.EqualTo("wt"));
        Assert.That(command.WorkflowPackageUri, Is.EqualTo("docker://img"));
        Assert.That(command.Context["WORKFLOW_NAME"], Is.EqualTo("x"));
    }

    [Test]
    public void RunConfiguration_Missing_Throws()
    {
        var (service, _, _, _, _) = New();
        Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.RunConfigurationAsync(Guid.NewGuid(), null, null, CancellationToken.None));
    }

    [Test]
    public async Task Dispatch_WithoutALiveRunner_FailsFast()
    {
        var (service, _, _, registry, _) = New(allowDispatchWithoutRunner: false);
        await SeedActiveTypeAsync(registry, "wt", "docker://img");

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => service.RunInlineAsync(
            new RunRequest("wt"), triggeredBy: null, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("no live Core.Runner"));
    }

    [Test]
    public async Task Dispatch_WithAFreshRunnerHeartbeat_Succeeds()
    {
        var (service, bus, _, registry, liveness) = New(allowDispatchWithoutRunner: false);
        await SeedActiveTypeAsync(registry, "wt", "docker://img");
        liveness.Record(Guid.NewGuid(), TimeProvider.System.GetUtcNow());

        await service.RunInlineAsync(new RunRequest("wt"), triggeredBy: null, CancellationToken.None);

        Assert.That(bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Count(), Is.EqualTo(1));
    }
}
