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
        _core.Schemas["implementation"] = Schema(
            new WorkflowSlotDto("coding-agent", null, null, Optional: false, null,
                ProviderTypes: ["claude-code-cli", "github-copilot-cli"]),
            new WorkflowSlotDto("work-items", null, null, Optional: false, null),
            new WorkflowSlotDto("notifier", null, null, Optional: true, null));
    }

    private static WorkflowSchemaDto Schema(params WorkflowSlotDto[] slots)
        => new("implementation", "1.0", "1", "LongLiving", [], slots, [], [], [], [], null, "{}");

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
    public void ProviderTypeOutsideTheNarrowing_IsRejected()
        => Assert.ThrowsAsync<ArgumentException>(() => _authoring.ConfigureAsync(Request(
            new SlotBinding("coding-agent", ProviderType: "rogue-agent"),
            new SlotBinding("work-items"))));

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

    [Test]
    public void EnvironmentLayersOfMixedBases_AreRejectedBeforeSubmit()
    {
        _core.Schemas["implementation"] = Schema(
            new WorkflowSlotDto("environment", null, null, Optional: true, null, AllowMultiple: true));
        _core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "dotnet-10", true, "environment", [], [], null,
            ComposesEnvironment: true, EnvironmentBase: "linux"));
        _core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "msbuild-17", true, "environment", [], [], null,
            ComposesEnvironment: true, EnvironmentBase: "windows"));

        var ex = Assert.ThrowsAsync<ArgumentException>(() => _authoring.ConfigureAsync(Request(
            new SlotBinding("environment", ProviderType: "dotnet-10"),
            new SlotBinding("environment", ProviderType: "msbuild-17"))));
        Assert.That(ex!.Message, Does.Contain("mix incompatible bases")
            .And.Contain("dotnet-10").And.Contain("msbuild-17"));
    }

    [Test]
    public void EnvironmentLayersOfMixedBaseVersions_AreRejectedBeforeSubmit()
    {
        _core.Schemas["implementation"] = Schema(
            new WorkflowSlotDto("environment", null, null, Optional: true, null, AllowMultiple: true));
        _core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "dotnet-10", true, "environment", [], [], null,
            ComposesEnvironment: true, EnvironmentBase: "linux", EnvironmentBaseVersion: "ubuntu-22.04"));
        _core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "node-22", true, "environment", [], [], null,
            ComposesEnvironment: true, EnvironmentBase: "linux", EnvironmentBaseVersion: "ubuntu-24.04"));

        var ex = Assert.ThrowsAsync<ArgumentException>(() => _authoring.ConfigureAsync(Request(
            new SlotBinding("environment", ProviderType: "dotnet-10"),
            new SlotBinding("environment", ProviderType: "node-22"))));
        Assert.That(ex!.Message, Does.Contain("mix incompatible base versions")
            .And.Contain("ubuntu-22.04").And.Contain("ubuntu-24.04"));
    }

    [Test]
    public async Task EnvironmentLayersOfOneBase_Pass()
    {
        _core.Schemas["implementation"] = Schema(
            new WorkflowSlotDto("environment", null, null, Optional: true, null, AllowMultiple: true));
        _core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "dotnet-10", true, "environment", [], [], null,
            ComposesEnvironment: true, EnvironmentBase: "linux"));
        _core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "node-22", true, "environment", [], [], null,
            ComposesEnvironment: true, EnvironmentBase: "Linux"));

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
