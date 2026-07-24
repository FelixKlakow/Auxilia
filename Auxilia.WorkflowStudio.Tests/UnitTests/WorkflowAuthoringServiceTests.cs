using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.WorkflowStudio;
using Auxilia.WorkflowStudio.Data;
using Auxilia.WorkflowStudio.Services;

namespace Auxilia.WorkflowStudio.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class WorkflowAuthoringServiceTests
{
    private static (WorkflowAuthoringService Service, FakeCoreClient Core, WorkflowTypeCatalog Catalog) New()
    {
        var core = new FakeCoreClient();
        var catalog = new WorkflowTypeCatalog(new InMemoryDataAccess<StudioWorkflowTypeRecord>());
        return (new WorkflowAuthoringService(catalog, core), core, catalog);
    }

    private static Task RegisterAsync(WorkflowTypeCatalog catalog, params DeclaredSlot[] slots)
        => catalog.RegisterAsync(
            new RegisterWorkflowType("codereview", "Code Review", "docker://cr", slots, new List<string>()),
            CancellationToken.None);

    [Test]
    public async Task Configure_MapsDomainConfigToCoreRunConfiguration()
    {
        var (service, core, catalog) = New();
        await RegisterAsync(catalog, new DeclaredSlot("scm", "ISourceControl"));
        var connector = core.AddConnector();

        var result = await service.ConfigureAsync(new ConfigureWorkflow(
            "cfg", "codereview",
            new List<StudioSlotBinding> { new("scm", connector.Id) },
            new Dictionary<string, string> { ["K"] = "V" }), CancellationToken.None);

        Assert.That(result.WorkflowTypeName, Is.EqualTo("codereview"));
        var created = core.CreatedConfigurations.Single();
        Assert.That(created.WorkflowType, Is.EqualTo("codereview"));
        Assert.That(created.PackageUri, Is.EqualTo("docker://cr"));
        Assert.That(created.SlotBindings!.Single().ConnectorId, Is.EqualTo(connector.Id));
        Assert.That(created.Context!["K"], Is.EqualTo("V"));
    }

    [Test]
    public void Configure_UnknownType_Throws()
    {
        var (service, _, _) = New();
        Assert.ThrowsAsync<KeyNotFoundException>(() => service.ConfigureAsync(
            new ConfigureWorkflow("c", "nope", new List<StudioSlotBinding>()), CancellationToken.None));
    }

    [Test]
    public async Task Configure_UnknownSlot_Throws()
    {
        var (service, core, catalog) = New();
        await RegisterAsync(catalog, new DeclaredSlot("scm", "ISourceControl"));
        var connector = core.AddConnector();
        Assert.ThrowsAsync<ArgumentException>(() => service.ConfigureAsync(
            new ConfigureWorkflow("c", "codereview",
                new List<StudioSlotBinding> { new("nope", connector.Id) }), CancellationToken.None));
    }

    [Test]
    public async Task Configure_MissingRequiredSlot_Throws()
    {
        var (service, _, catalog) = New();
        await RegisterAsync(catalog, new DeclaredSlot("scm", "ISourceControl", Optional: false));
        Assert.ThrowsAsync<ArgumentException>(() => service.ConfigureAsync(
            new ConfigureWorkflow("c", "codereview", new List<StudioSlotBinding>()), CancellationToken.None));
    }

    [Test]
    public async Task Configure_UnknownConnector_Throws()
    {
        var (service, _, catalog) = New();
        await RegisterAsync(catalog, new DeclaredSlot("scm", "ISourceControl"));
        Assert.ThrowsAsync<ArgumentException>(() => service.ConfigureAsync(
            new ConfigureWorkflow("c", "codereview",
                new List<StudioSlotBinding> { new("scm", Guid.NewGuid()) }), CancellationToken.None));
    }

    [Test]
    public async Task Configure_OptionalSlotMayStayUnbound()
    {
        var (service, _, catalog) = New();
        await RegisterAsync(catalog, new DeclaredSlot("scm", "ISourceControl", Optional: true));
        var result = await service.ConfigureAsync(
            new ConfigureWorkflow("c", "codereview", new List<StudioSlotBinding>()), CancellationToken.None);
        Assert.That(result.CoreConfigurationId, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task Run_DelegatesToCore()
    {
        var (service, core, _) = New();
        var id = Guid.NewGuid();
        await service.RunAsync(id, CancellationToken.None);
        Assert.That(core.RunConfigurationIds.Single(), Is.EqualTo(id));
    }
}
