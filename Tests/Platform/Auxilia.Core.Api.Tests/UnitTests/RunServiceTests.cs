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
            new InMemoryDataAccess<CoreRunConfigurationRecord>(),
            new AccessGrantEvaluator(
                new InMemoryDataAccess<PrincipalRecord>(), new InMemoryDataAccess<GroupMembershipRecord>()),
            TimeProvider.System);
        var protector = new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32));
        var connectorStore = new InMemoryDataAccess<CoreConnectorRecord>();
        var connectors = new ConnectorService(connectorStore, protector, TimeProvider.System);
        var delegatedTokens = new DelegatedTokenStore(
            new InMemoryDataAccess<DelegatedUserTokenRecord>(), protector, TimeProvider.System);
        var resolver = new SlotCredentialResolver(
            new InMemoryDataAccess<CoreRunResolutionRecord>(), connectors,
            new ConnectorTokenRefresher(
                connectors,
                new ProviderCatalogService(
                    new InMemoryDataAccess<SlotProviderRecord>(),
                    new InMemoryDataAccess<ProviderCatalogRecord>(),
                    new AuditLog(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System)),
                new StubHttpClientFactory(new StubHttpMessageHandler(
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound))),
                TimeProvider.System,
                NullLogger<ConnectorTokenRefresher>.Instance),
            delegatedTokens,
            new NullDelegatedTokenExchange(),
            new AuditLog(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System),
            TimeProvider.System, Options.Create(new CoreApiSettings()));
        var accessPolicy = new ConnectorAccessPolicy(connectorStore,
            new AccessGrantEvaluator(
                new InMemoryDataAccess<PrincipalRecord>(), new InMemoryDataAccess<GroupMembershipRecord>()));
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
    public async Task RunInline_EnvironmentLayersOfMixedBases_FailTheDispatch()
    {
        var (service, _, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img");
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "dotnet-10", "environment", null, ["exec-env"], [],
            ComposesEnvironment: true, EnvironmentBase: "linux"), CancellationToken.None);
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "msbuild-17", "environment", null, ["exec-env"], [],
            ComposesEnvironment: true, EnvironmentBase: "windows"), CancellationToken.None);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => service.RunInlineAsync(
            new RunRequest("wt", SlotBindings:
            [
                new SlotBinding("environment", ProviderType: "dotnet-10"),
                new SlotBinding("environment", ProviderType: "msbuild-17"),
            ]),
            triggeredBy: null, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("mix incompatible bases")
            .And.Contain("dotnet-10").And.Contain("msbuild-17"),
            "One run composes one image on one base - a mixed selection fails fast at dispatch.");
    }

    [Test]
    public async Task RunInline_EnvironmentLayersOfOneBase_Dispatch()
    {
        var (service, bus, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img");
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "dotnet-10", "environment", null, ["exec-env"], [],
            ComposesEnvironment: true, EnvironmentBase: "linux"), CancellationToken.None);
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "node-22", "environment", null, ["exec-env"], [],
            ComposesEnvironment: true, EnvironmentBase: "Linux"), CancellationToken.None);

        await service.RunInlineAsync(
            new RunRequest("wt", SlotBindings:
            [
                new SlotBinding("environment", ProviderType: "dotnet-10"),
                new SlotBinding("environment", ProviderType: "node-22"),
            ]),
            triggeredBy: null, CancellationToken.None);

        var command = bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Single();
        Assert.That(command.EnvironmentCapabilities, Is.EquivalentTo(new[] { "dotnet-10", "node-22" }),
            "Same-base layers (case-insensitive) compose together.");
    }

    [Test]
    public async Task Rerun_RedispatchesWithFreshIdAndToken_AndRestashesTheBindings()
    {
        var (service, bus, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img");
        await service.RunInlineAsync(
            new RunRequest("wt", new Dictionary<string, string> { ["K"] = "V" }),
            triggeredBy: null, CancellationToken.None);
        var original = bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Single();

        var accepted = await service.RerunAsync(new Auxilia.Core.Api.Data.CoreRunRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "wt",
            State = "Failed",
            CommandId = original.CommandId,
            DispatchCommandJson = System.Text.Json.JsonSerializer.Serialize(original),
        }, triggeredBy: null, CancellationToken.None);

        var rerun = bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>()
            .Single(c => c.CommandId == accepted.RunId);
        Assert.Multiple(() =>
        {
            Assert.That(rerun.CommandId, Is.Not.EqualTo(original.CommandId));
            Assert.That(rerun.ResolutionToken, Is.Not.EqualTo(original.ResolutionToken),
                "reusing the old token against a new command id could never resolve credentials");
            Assert.That(rerun.Context["K"], Is.EqualTo("V"), "the original context is preserved");
        });
    }

    [Test]
    public async Task RunConfiguration_ResolvesTypePackageAndContext()
    {
        var (service, bus, configs, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img");
        var config = await configs.CreateAsync(
            new CreateRunConfiguration("c", "wt",
                new Dictionary<string, string> { ["WORKFLOW_NAME"] = "x" }), ownerPrincipalId: null, CancellationToken.None);

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
