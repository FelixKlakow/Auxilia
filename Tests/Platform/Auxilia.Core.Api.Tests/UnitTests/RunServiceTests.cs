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
    private WorkspaceResourceService _repositories = null!;
    private ConnectorService _connectors = null!;
    private InMemoryDataAccess<CoreRunRecord> _runs = null!;

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
        var connectors = _connectors = new ConnectorService(
            connectorStore, protector,
            new AccessGrantEvaluator(
                new InMemoryDataAccess<PrincipalRecord>(), new InMemoryDataAccess<GroupMembershipRecord>()),
            TimeProvider.System);
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
        var repositories = _repositories = new WorkspaceResourceService(
            new InMemoryDataAccess<CoreWorkspaceRecord>(),
            new AccessGrantEvaluator(
                new InMemoryDataAccess<PrincipalRecord>(), new InMemoryDataAccess<GroupMembershipRecord>()),
            TimeProvider.System);
        var service = new RunService(
            bus, configs, registry, new WorkflowSchemaReadService(typeStore),
            providerCatalog, resolver, accessPolicy, connectors, repositories,
            new AccessGrantEvaluator(
                new InMemoryDataAccess<PrincipalRecord>(), new InMemoryDataAccess<GroupMembershipRecord>()),
            TestResourceAccess.EmptyPrincipalDirectory(),
            TestResourceAccess.Open,
            liveness,
            new EnvironmentBaseService(
                new InMemoryDataAccess<EnvironmentBaseRecord>(),
                new AuditLog(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System),
                TimeProvider.System),
            _runs = new InMemoryDataAccess<CoreRunRecord>(),
            new Auxilia.Workflows.Messaging.WorkflowStatusPublisher(bus, TimeProvider.System),
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

    /// <summary>A schema whose image provides only the claude tool.</summary>
    private static string ToolSchemaJson() => System.Text.Json.JsonSerializer.Serialize(
        new Auxilia.Workflows.WorkflowSchema(
            "wt",
            [new Auxilia.Workflows.SlotDefinition("coding-agent", null) { Contract = "ICodingAgent" }],
            [])
        {
            ProvidedTools = ["claude"]
        });

    private Task RegisterAgentProviderAsync(string providerType, params string[] requiredTools)
        => _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            providerType, "coding-agent", null, ["ICodingAgent"], [],
            RequiredTools: requiredTools), CancellationToken.None);

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
    public async Task RunInline_CarriesTheRegistrySchemaOnTheCommand()
    {
        var (service, bus, _, registry, _) = New();
        var schemaJson = ToolSchemaJson();
        await SeedActiveTypeAsync(registry, "wt", "docker://img", schemaJson);

        await service.RunInlineAsync(
            new RunRequest("wt", new Dictionary<string, string>()),
            triggeredBy: null, CancellationToken.None);

        var command = bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Single();
        Assert.That(command.SchemaJson, Is.EqualTo(schemaJson),
            "the registry's inspected schema must ride the dispatch so a fresh runner's "
            + "first run of the type already decides terminal/network/repository questions from it");
    }

    [Test]
    public async Task RunInline_WithoutARegistrySchema_DispatchesANullSchema()
    {
        var (service, bus, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img");

        await service.RunInlineAsync(
            new RunRequest("wt", new Dictionary<string, string>()),
            triggeredBy: null, CancellationToken.None);

        var command = bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Single();
        Assert.That(command.SchemaJson, Is.Null);
    }

    [Test]
    public async Task RunInline_WritesTheDispatchedRecord_BeforePublishingTheCommand()
    {
        var (service, bus, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img");

        var accepted = await service.RunInlineAsync(
            new RunRequest("wt", new Dictionary<string, string>()),
            triggeredBy: null, CancellationToken.None);

        var record = await _runs.ReadAsync(accepted.RunId);
        Assert.Multiple(() =>
        {
            Assert.That(record, Is.Not.Null, "The run must exist from the moment it is accepted.");
            Assert.That(record!.State, Is.EqualTo(RunStates.Dispatched));
            Assert.That(record.CommandId, Is.EqualTo(accepted.RunId));
            Assert.That(record.DispatchCommandJson, Is.Not.Null.And.Not.Empty,
                "The stored command is what the claim-timeout sweep re-dispatches from.");
        });

        var messages = bus.PublishedMessages.Select(m => m.Message).ToList();
        var statusIndex = messages.FindIndex(m =>
            m is WorkflowStatusEvent e && e.State == RunStates.Dispatched);
        var commandIndex = messages.FindIndex(m => m is RunWorkflowCommand);
        Assert.Multiple(() =>
        {
            Assert.That(statusIndex, Is.GreaterThanOrEqualTo(0),
                "Dispatch must surface a Dispatched status event for pre-subscribed watchers.");
            Assert.That(commandIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(statusIndex, Is.LessThan(commandIndex),
                "The record + status precede the bus command: a lost publish still leaves a sweepable row.");
        });
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
    public async Task RunInline_ProviderRequiringAToolTheImageLacks_Throws()
    {
        var (service, _, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img", ToolSchemaJson());
        await RegisterAgentProviderAsync("github-copilot-cli", "copilot");

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => service.RunInlineAsync(
            new RunRequest("wt",
                SlotBindings: [new SlotBinding("coding-agent", ProviderType: "github-copilot-cli")]),
            triggeredBy: null, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("copilot"),
            "the missing tool — not a provider whitelist — is the rejection reason");
    }

    [Test]
    public async Task RunInline_ProviderWhoseToolsTheImageProvides_Dispatches()
    {
        var (service, bus, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img", ToolSchemaJson());
        await RegisterAgentProviderAsync("claude-code-cli", "claude");

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
            ComposesEnvironment: true, EnvironmentBases: [new EnvironmentBaseRef("linux")]), CancellationToken.None);
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "msbuild-17", "environment", null, ["exec-env"], [],
            ComposesEnvironment: true, EnvironmentBases: [new EnvironmentBaseRef("windows")]), CancellationToken.None);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => service.RunInlineAsync(
            new RunRequest("wt", SlotBindings:
            [
                new SlotBinding("environment", ProviderType: "dotnet-10"),
                new SlotBinding("environment", ProviderType: "msbuild-17"),
            ]),
            triggeredBy: null, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("no common base")
            .And.Contain("dotnet-10").And.Contain("msbuild-17"),
            "One run composes one image on one base - a mixed selection fails fast at dispatch.");
    }

    [Test]
    public async Task RunInline_MultiBaseLayer_ComposesWithASingleBaseSibling()
    {
        var (service, bus, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img");
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "dotnet-10", "environment", null, ["exec-env"], [],
            ComposesEnvironment: true,
            EnvironmentBases: [new EnvironmentBaseRef("linux"), new EnvironmentBaseRef("windows")]),
            CancellationToken.None);
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "msbuild-17", "environment", null, ["exec-env"], [],
            ComposesEnvironment: true, EnvironmentBases: [new EnvironmentBaseRef("windows")]),
            CancellationToken.None);

        await service.RunInlineAsync(
            new RunRequest("wt", SlotBindings:
            [
                new SlotBinding("environment", ProviderType: "dotnet-10"),
                new SlotBinding("environment", ProviderType: "msbuild-17"),
            ]),
            triggeredBy: null, CancellationToken.None);

        Assert.That(
            bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Count(),
            Is.EqualTo(1),
            "a layer carrying variants for several bases composes wherever a shared base remains");
    }

    [Test]
    public async Task RunInline_WorkspaceReference_ExpandsToTheStoredResource()
    {
        var (service, bus, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img");
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "git-repository", "workspace", null, ["src-ctl"],
            [
                new RegisterProviderSetting("CloneUrl", "Repository", "Text", Required: true, Role: "clone-url"),
                new RegisterProviderSetting("Branch", "Branch", "Text", Role: "branch"),
                new RegisterProviderSetting("SetupScript", "Setup script", "Text", Role: "setup-script"),
            ],
            MountsIntoWorkspace: true), CancellationToken.None);
        var repository = await _repositories.CreateAsync(new CreateWorkspaceResource(
                "Main repo", "git-repository",
                new Dictionary<string, string>
                {
                    ["CloneUrl"] = "https://example.test/main.git",
                    ["SetupScript"] = "dotnet restore",
                },
                Scope: ResourceScope.Company),
            ownerPrincipalId: null, CancellationToken.None);

        await service.RunInlineAsync(
            new RunRequest("wt", SlotBindings:
            [
                // The binding names ONLY the repository; a binding-level setting overrides per use.
                new SlotBinding("repo", Settings: new Dictionary<string, string> { ["Branch"] = "feature/x" },
                    WorkspaceId: repository.Id),
            ]),
            triggeredBy: null, CancellationToken.None);

        var command = bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Single();
        var mount = command.WorkspaceMounts!.Single();
        Assert.Multiple(() =>
        {
            Assert.That(mount.ProviderType, Is.EqualTo("git-repository"),
                "the stored resource supplies the provider type");
            Assert.That(mount.SettingsByRole["clone-url"], Is.EqualTo("https://example.test/main.git"));
            Assert.That(mount.SettingsByRole["setup-script"], Is.EqualTo("dotnet restore"),
                "the repository's setup script rides its reference — configured once, applied everywhere");
            Assert.That(mount.SettingsByRole["branch"], Is.EqualTo("feature/x"),
                "binding-level settings override the stored resource per use");
        });
    }

    [Test]
    public async Task RunInline_PersonalWorkspaceOfAnotherUser_FailsTheDispatch()
    {
        var (service, _, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img");
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "git-repository", "workspace", null, ["src-ctl"],
            [new RegisterProviderSetting("CloneUrl", "Repository", "Text", Required: true, Role: "clone-url")],
            MountsIntoWorkspace: true), CancellationToken.None);
        var repository = await _repositories.CreateAsync(new CreateWorkspaceResource(
                "Private repo", "git-repository",
                new Dictionary<string, string> { ["CloneUrl"] = "https://example.test/private.git" }),
            ownerPrincipalId: Guid.NewGuid(), CancellationToken.None);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => service.RunInlineAsync(
            new RunRequest("wt", SlotBindings: [new SlotBinding("repo", WorkspaceId: repository.Id)]),
            triggeredBy: Guid.NewGuid(), CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("not permitted").And.Contain("Private repo"),
            "a personal repository admits only its owner and granted subjects — like connectors");
    }

    [Test]
    public async Task RunInline_EnvironmentLayersOfMixedBaseVersions_FailTheDispatch()
    {
        var (service, _, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img");
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "dotnet-10", "environment", null, ["exec-env"], [],
            ComposesEnvironment: true, EnvironmentBases: [new EnvironmentBaseRef("linux", "ubuntu-22.04")]),
            CancellationToken.None);
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "node-22", "environment", null, ["exec-env"], [],
            ComposesEnvironment: true, EnvironmentBases: [new EnvironmentBaseRef("linux", "ubuntu-24.04")]),
            CancellationToken.None);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => service.RunInlineAsync(
            new RunRequest("wt", SlotBindings:
            [
                new SlotBinding("environment", ProviderType: "dotnet-10"),
                new SlotBinding("environment", ProviderType: "node-22"),
            ]),
            triggeredBy: null, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("no common base")
            .And.Contain("ubuntu-22.04").And.Contain("ubuntu-24.04"),
            "Layers pinning different base versions can never build into one image.");
    }

    [Test]
    public async Task RunInline_PinnedAndUnpinnedBaseVersions_Dispatch()
    {
        var (service, bus, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img");
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "dotnet-10", "environment", null, ["exec-env"], [],
            ComposesEnvironment: true, EnvironmentBases: [new EnvironmentBaseRef("linux", "ubuntu-24.04")]),
            CancellationToken.None);
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "node-22", "environment", null, ["exec-env"], [],
            ComposesEnvironment: true, EnvironmentBases: [new EnvironmentBaseRef("linux")]), CancellationToken.None);

        await service.RunInlineAsync(
            new RunRequest("wt", SlotBindings:
            [
                new SlotBinding("environment", ProviderType: "dotnet-10"),
                new SlotBinding("environment", ProviderType: "node-22"),
            ]),
            triggeredBy: null, CancellationToken.None);

        Assert.That(
            bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Count(),
            Is.EqualTo(1), "an unpinned layer composes with any version of its base");
    }

    [Test]
    public async Task RunInline_EnvironmentLayersOfOneBase_Dispatch()
    {
        var (service, bus, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img");
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "dotnet-10", "environment", null, ["exec-env"], [],
            ComposesEnvironment: true, EnvironmentBases: [new EnvironmentBaseRef("linux")]), CancellationToken.None);
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "node-22", "environment", null, ["exec-env"], [],
            ComposesEnvironment: true, EnvironmentBases: [new EnvironmentBaseRef("Linux")]), CancellationToken.None);

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

    /// <summary>The stored run row a rerun starts from, as RunTrackingService would have left it.</summary>
    private static Auxilia.Core.Api.Data.CoreRunRecord RunRecordFor(RunWorkflowCommand original) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowType = original.WorkflowType ?? "wt",
        State = "Failed",
        CommandId = original.CommandId,
        DispatchCommandJson = System.Text.Json.JsonSerializer.Serialize(original),
    };

    [Test]
    public async Task RunInline_ConnectorResolvedProviderRequiringAToolTheImageLacks_Throws()
    {
        var (service, _, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img", ToolSchemaJson());
        await RegisterAgentProviderAsync("github-copilot-cli", "copilot");
        var connector = await _connectors.CreateAsync(
            new CreateConnector("copilot-account", "github-copilot-cli",
                new Dictionary<string, string> { ["token"] = "secret" }, ResourceScope.Company),
            ownerPrincipalId: null, CancellationToken.None);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => service.RunInlineAsync(
            new RunRequest("wt",
                // The binding names ONLY the connector — the provider type resolves from the store.
                SlotBindings: [new SlotBinding("coding-agent", ConnectorId: connector.Id)]),
            triggeredBy: null, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("copilot"),
            "tool matching must cover connector-resolved providers, not only inline provider types");
    }

    [Test]
    public async Task RunInline_ConnectorResolvedProviderWhoseToolsTheImageProvides_Dispatches()
    {
        var (service, bus, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img", ToolSchemaJson());
        await RegisterAgentProviderAsync("claude-code-cli", "claude");
        var connector = await _connectors.CreateAsync(
            new CreateConnector("claude-account", "claude-code-cli",
                new Dictionary<string, string> { ["token"] = "secret" }, ResourceScope.Company),
            ownerPrincipalId: null, CancellationToken.None);

        await service.RunInlineAsync(
            new RunRequest("wt",
                SlotBindings: [new SlotBinding("coding-agent", ConnectorId: connector.Id)]),
            triggeredBy: null, CancellationToken.None);

        Assert.That(
            bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Count(),
            Is.EqualTo(1));
    }

    [Test]
    public async Task RunInline_WorkspaceResolvedProviderRequiringAToolTheImageLacks_Throws()
    {
        var (service, _, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img", ToolSchemaJson());
        await _providerCatalog.RegisterAsync("test", new RegisterSlotProvider(
            "svn-repository", "workspace", null, ["src-ctl"],
            [new RegisterProviderSetting("CloneUrl", "Repository", "Text", Required: true, Role: "clone-url")],
            MountsIntoWorkspace: true, RequiredTools: ["subversion"]), CancellationToken.None);
        var repository = await _repositories.CreateAsync(new CreateWorkspaceResource(
                "Legacy repo", "svn-repository",
                new Dictionary<string, string> { ["CloneUrl"] = "https://example.test/legacy" },
                Scope: ResourceScope.Company),
            ownerPrincipalId: null, CancellationToken.None);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => service.RunInlineAsync(
            // The binding names ONLY the workspace — the provider type appears on expansion.
            new RunRequest("wt", SlotBindings: [new SlotBinding("repo", WorkspaceId: repository.Id)]),
            triggeredBy: null, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("subversion"),
            "tool matching runs on the EXPANDED bindings, so workspace-resolved providers fail at dispatch");
    }

    [Test]
    public async Task RunInline_CaseVariantProviderType_IsToolValidatedAgainstTheCatalogEntry()
    {
        var (service, _, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img", ToolSchemaJson());
        await RegisterAgentProviderAsync("github-copilot-cli", "copilot");

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => service.RunInlineAsync(
            new RunRequest("wt",
                SlotBindings: [new SlotBinding("coding-agent", ProviderType: "GitHub-Copilot-CLI")]),
            triggeredBy: null, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("copilot"),
            "a case-variant provider type resolves the SAME catalog entry (ids derive from the "
            + "lowercased type) instead of silently skipping validation and dying at the runner");
    }

    [Test]
    public async Task RunInline_CaseVariantProviderTypeWhoseToolsTheImageProvides_Dispatches()
    {
        var (service, bus, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img", ToolSchemaJson());
        await RegisterAgentProviderAsync("claude-code-cli", "claude");

        await service.RunInlineAsync(
            new RunRequest("wt",
                SlotBindings: [new SlotBinding("coding-agent", ProviderType: "Claude-Code-CLI")]),
            triggeredBy: null, CancellationToken.None);

        Assert.That(
            bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Count(),
            Is.EqualTo(1), "provider types are case-insensitive end-to-end");
    }

    [Test]
    public async Task Rerun_ProviderWhoseRequiredToolsAreNoLongerSatisfied_Throws()
    {
        var (service, bus, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img", ToolSchemaJson());
        await RegisterAgentProviderAsync("claude-code-cli", "claude");
        await service.RunInlineAsync(
            new RunRequest("wt",
                SlotBindings: [new SlotBinding("coding-agent", ProviderType: "claude-code-cli")]),
            triggeredBy: null, CancellationToken.None);
        var original = bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Single();

        // The provider's manifest changed since the original dispatch: it now needs a tool the
        // workflow's image does not provide.
        await RegisterAgentProviderAsync("claude-code-cli", "copilot");

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => service.RerunAsync(
            RunRecordFor(original), triggeredBy: null, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("copilot"),
            "a rerun is a new run, not a replay of old trust — tool matching re-runs on the fresh state");
    }

    [Test]
    public async Task Rerun_WithoutAGrantForACatalogGatedProvider_Throws()
    {
        var (service, bus, _, registry, _) = New();
        await SeedActiveTypeAsync(registry, "wt", "docker://img", ToolSchemaJson());
        await RegisterAgentProviderAsync("claude-code-cli", "claude");
        await service.RunInlineAsync(
            new RunRequest("wt",
                SlotBindings: [new SlotBinding("coding-agent", ProviderType: "claude-code-cli")]),
            triggeredBy: null, CancellationToken.None);
        var original = bus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>().Single();

        // The catalog entry got granted to somebody else since the original dispatch.
        await _providerCatalog.SetGrantsAsync("test", "claude-code-cli",
            [new AccessGrant(AccessGrantKind.Principal, Guid.NewGuid().ToString("D"))], CancellationToken.None);

        var ex = Assert.ThrowsAsync<RunAccessDeniedException>(() => service.RerunAsync(
            RunRecordFor(original), triggeredBy: Guid.NewGuid(), CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("claude-code-cli"),
            "the catalog grant gate applies to a rerun's principal like to any dispatch");
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
