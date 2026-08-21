using Auxilia.Core.Contracts;
using Auxilia.Workflows.Client.Authoring;

namespace Auxilia.Workflows.Client.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class WorkflowAuthoringTests
{
    private FakeCoreClient _core = null!;
    private WorkflowAuthoring _authoring = null!;

    [SetUp]
    public void SetUp()
    {
        _core = new FakeCoreClient();
        _authoring = new WorkflowAuthoring(_core);
        // The schema declares what the image bundles; each provider declares what it needs —
        // the whitelist is gone, matching is the intersection of the two.
        _core.Schemas["implementation"] = Schema(
            new WorkflowSlotDto("coding-agent", null, null, Optional: false, null),
            new WorkflowSlotDto("work-items", null, null, Optional: false, null),
            new WorkflowSlotDto("notifier", null, null, Optional: true, null));
        _core.ProviderCatalog.AddRange(
        [
            Provider("claude-code-cli", requiredTools: ["claude"]),
            Provider("github-copilot-cli", requiredTools: ["copilot"]),
            Provider("codex-cli", requiredTools: ["codex"]),
            Provider("tfs-account")
        ]);
    }

    private static WorkflowSchemaDto Schema(params WorkflowSlotDto[] slots)
        => new("implementation", "1.0", "1", "LongLiving", [], slots, [], [], [], [], null, "{}")
        {
            ProvidedTools = ["claude", "copilot"]
        };

    private static ProviderCatalogEntry Provider(string providerType, IReadOnlyList<string>? requiredTools = null)
        => new(providerType, Available: true, "coding-agent", [], [], null)
        {
            RequiredTools = requiredTools ?? []
        };

    private static WorkflowConfigurationRequest Request(params SlotBinding[] bindings)
        => new("My config", "implementation", bindings);

    [Test]
    public void UnknownWorkflowType_IsKeyNotFound()
        => Assert.ThrowsAsync<KeyNotFoundException>(() => _authoring.ConfigureAsync(
            new WorkflowConfigurationRequest("x", "no-such-type", [])));

    [Test]
    public void UndeclaredSlot_IsRejected()
        => Assert.ThrowsAsync<ArgumentException>(() => _authoring.ConfigureAsync(Request(
            new SlotBinding("coding-agent", ProviderType: "claude-code-cli"),
            new SlotBinding("work-items"),
            new SlotBinding("no-such-slot"))));

    [Test]
    public void ProviderRequiringAToolTheImageLacks_IsRejected()
    {
        var ex = Assert.ThrowsAsync<ArgumentException>(() => _authoring.ConfigureAsync(Request(
            new SlotBinding("coding-agent", ProviderType: "codex-cli"),
            new SlotBinding("work-items"))));
        Assert.That(ex!.Message, Does.Contain("codex"));
    }

    [Test]
    public async Task ProviderWithoutToolRequirements_MatchesOnContractAlone()
    {
        await _authoring.ConfigureAsync(Request(
            new SlotBinding("coding-agent", ProviderType: "claude-code-cli"),
            new SlotBinding("work-items", ProviderType: "tfs-account")));
        Assert.That(_core.CreatedConfigurations, Has.Count.EqualTo(1));
    }

    [Test]
    public void MissingRequiredSlot_IsRejected_OptionalMayStayUnbound()
    {
        var ex = Assert.ThrowsAsync<ArgumentException>(() => _authoring.ConfigureAsync(Request(
            new SlotBinding("coding-agent", ProviderType: "claude-code-cli"))));
        Assert.That(ex!.Message, Does.Contain("work-items").And.Not.Contain("notifier"));
    }

    [Test]
    public void UnknownConnector_IsKeyNotFound()
        => Assert.ThrowsAsync<KeyNotFoundException>(() => _authoring.ConfigureAsync(Request(
            new SlotBinding("coding-agent", ProviderType: "claude-code-cli", ConnectorId: Guid.NewGuid()),
            new SlotBinding("work-items"))));

    [Test]
    public async Task ValidRequest_CreatesTheConfiguration()
    {
        var connector = Guid.NewGuid();
        _core.KnownConnectors.Add(connector);

        var configuration = await _authoring.ConfigureAsync(Request(
            new SlotBinding("coding-agent", ProviderType: "claude-code-cli", ConnectorId: connector),
            new SlotBinding("work-items", ProviderType: "tfs-account")));

        Assert.Multiple(() =>
        {
            Assert.That(configuration.WorkflowType, Is.EqualTo("implementation"));
            Assert.That(_core.CreatedConfigurations.Single().SlotBindings, Has.Count.EqualTo(2));
        });
    }

    private void DeclareParametrisedSchema()
        => _core.Schemas["implementation"] = new WorkflowSchemaDto(
            "implementation", "1.0", "1", "Job", [], [],
            Inputs:
            [
                new WorkflowInputDto("greeting", "Greeting", false, null, InputKinds.Choice,
                    "hello", ["hello", "moin"], null, PerRun: false),
                new WorkflowInputDto("count", "Count", false, null, InputKinds.Number,
                    "3", null, null, PerRun: false),
                new WorkflowInputDto("shout", "Shout", false, null, InputKinds.Boolean,
                    "false", null, null, PerRun: false),
                new WorkflowInputDto("occasion", "Occasion", true, null, InputKinds.Text,
                    null, null, null, PerRun: true)
            ],
            [], [], [], null, "{}");

    [Test]
    public void InputValues_AreKindValidated_BeforeSubmit()
    {
        DeclareParametrisedSchema();

        var ex = Assert.ThrowsAsync<ArgumentException>(() => _authoring.ConfigureAsync(
            new WorkflowConfigurationRequest("cfg", "implementation", [],
                Context: new Dictionary<string, string>
                {
                    ["greeting"] = "howdy",
                    ["count"] = "many",
                    ["shout"] = "yep"
                })));

        Assert.That(ex!.Message,
            Does.Contain("hello, moin").And.Contain("count").And.Contain("shout"),
            "every bad value is reported at once, before anything is submitted");
        Assert.That(_core.CreatedConfigurations, Is.Empty);
    }

    [Test]
    public void PerRunInput_FixedInAStoredConfiguration_IsRejected()
    {
        DeclareParametrisedSchema();

        var ex = Assert.ThrowsAsync<ArgumentException>(() => _authoring.ConfigureAsync(
            new WorkflowConfigurationRequest("cfg", "implementation", [],
                Context: new Dictionary<string, string> { ["occasion"] = "always-the-same" })));

        Assert.That(ex!.Message, Does.Contain("per-run"));
    }

    [Test]
    public async Task ValidInputs_AndUndeclaredPlatformKeys_PassUntouched()
    {
        DeclareParametrisedSchema();

        await _authoring.ConfigureAsync(new WorkflowConfigurationRequest(
            "cfg", "implementation", [],
            Context: new Dictionary<string, string>
            {
                ["greeting"] = "moin",
                ["count"] = "4",
                ["pod-bases"] = "sim-machine"
            }));

        Assert.That(_core.CreatedConfigurations.Single().Context!["pod-bases"],
            Is.EqualTo("sim-machine"),
            "platform context keys are not declared inputs and must pass through");
    }

    [Test]
    public async Task ConfigureAndRun_CarriesThePerRunContext_ValidatedByKind()
    {
        DeclareParametrisedSchema();

        var (_, _) = await _authoring.ConfigureAndRunAsync(
            new WorkflowConfigurationRequest("cfg", "implementation", []),
            perRunContext: new Dictionary<string, string> { ["occasion"] = "release-day" });

        Assert.That(_core.ConfigurationRuns.Single().Context, Is.Not.Null.And.ContainKey("occasion"),
            "per-run values must reach the dispatch, not vanish between the two calls");

        Assert.ThrowsAsync<ArgumentException>(() => _authoring.ConfigureAndRunAsync(
            new WorkflowConfigurationRequest("cfg2", "implementation", []),
            perRunContext: new Dictionary<string, string> { ["count"] = "junk" }));
    }

    [Test]
    public void EnvironmentLayersOfMixedBases_AreRejectedBeforeSubmit()
    {
        _core.Schemas["implementation"] = Schema(
            new WorkflowSlotDto("environment", null, null, Optional: true, null, AllowMultiple: true));
        _core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "dotnet-10", true, "environment", [], [], null,
            ComposesEnvironment: true, EnvironmentBases: [new EnvironmentBaseRef("linux")]));
        _core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "msbuild-17", true, "environment", [], [], null,
            ComposesEnvironment: true, EnvironmentBases: [new EnvironmentBaseRef("windows")]));

        var ex = Assert.ThrowsAsync<ArgumentException>(() => _authoring.ConfigureAsync(Request(
            new SlotBinding("environment", ProviderType: "dotnet-10"),
            new SlotBinding("environment", ProviderType: "msbuild-17"))));
        Assert.That(ex!.Message, Does.Contain("no common base")
            .And.Contain("dotnet-10").And.Contain("msbuild-17"));
    }

    [Test]
    public void EnvironmentLayersOfMixedBaseVersions_AreRejectedBeforeSubmit()
    {
        _core.Schemas["implementation"] = Schema(
            new WorkflowSlotDto("environment", null, null, Optional: true, null, AllowMultiple: true));
        _core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "dotnet-10", true, "environment", [], [], null,
            ComposesEnvironment: true, EnvironmentBases: [new EnvironmentBaseRef("linux", "ubuntu-22.04")]));
        _core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "node-22", true, "environment", [], [], null,
            ComposesEnvironment: true, EnvironmentBases: [new EnvironmentBaseRef("linux", "ubuntu-24.04")]));

        var ex = Assert.ThrowsAsync<ArgumentException>(() => _authoring.ConfigureAsync(Request(
            new SlotBinding("environment", ProviderType: "dotnet-10"),
            new SlotBinding("environment", ProviderType: "node-22"))));
        Assert.That(ex!.Message, Does.Contain("no common base")
            .And.Contain("ubuntu-22.04").And.Contain("ubuntu-24.04"));
    }

    [Test]
    public async Task EnvironmentLayersOfOneBase_Pass()
    {
        _core.Schemas["implementation"] = Schema(
            new WorkflowSlotDto("environment", null, null, Optional: true, null, AllowMultiple: true));
        _core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "dotnet-10", true, "environment", [], [], null,
            ComposesEnvironment: true, EnvironmentBases: [new EnvironmentBaseRef("linux")]));
        _core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "node-22", true, "environment", [], [], null,
            ComposesEnvironment: true, EnvironmentBases: [new EnvironmentBaseRef("Linux")]));

        var configuration = await _authoring.ConfigureAsync(Request(
            new SlotBinding("environment", ProviderType: "dotnet-10"),
            new SlotBinding("environment", ProviderType: "node-22")));

        Assert.That(configuration.WorkflowType, Is.EqualTo("implementation"),
            "Same-base layers (case-insensitive) validate cleanly.");
    }

    [Test]
    public async Task ConfigureAndRun_DispatchesTheFreshConfiguration_OnBehalfOf()
    {
        var principal = Guid.NewGuid();

        var (configuration, run) = await _authoring.ConfigureAndRunAsync(Request(
            new SlotBinding("coding-agent", ProviderType: "claude-code-cli"),
            new SlotBinding("work-items")), onBehalfOf: principal);

        Assert.Multiple(() =>
        {
            Assert.That(run.RunId, Is.Not.EqualTo(Guid.Empty));
            var (dispatchedConfig, onBehalfOf, _) = _core.ConfigurationRuns.Single();
            Assert.That(dispatchedConfig, Is.EqualTo(configuration.Id));
            Assert.That(onBehalfOf, Is.EqualTo(principal));
        });
    }
}
