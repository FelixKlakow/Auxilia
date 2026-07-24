using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.WorkflowStudio;
using Auxilia.WorkflowStudio.Data;
using Auxilia.WorkflowStudio.Services;

namespace Auxilia.WorkflowStudio.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class WorkflowTypeCatalogTests
{
    private static WorkflowTypeCatalog New() => new(new InMemoryDataAccess<StudioWorkflowTypeRecord>());

    [Test]
    public async Task RegisterThenGet_RoundTrips()
    {
        var catalog = New();
        await catalog.RegisterAsync(new RegisterWorkflowType(
            "wt", "Workflow", "docker://img",
            new List<DeclaredSlot> { new("scm", "ISourceControl") },
            new List<string> { "REPO" }), CancellationToken.None);

        var got = await catalog.GetByNameAsync("wt", CancellationToken.None);
        Assert.That(got, Is.Not.Null);
        Assert.That(got!.PackageUri, Is.EqualTo("docker://img"));
        Assert.That(got.Slots.Single().Name, Is.EqualTo("scm"));
        Assert.That(got.ContextKeys.Single(), Is.EqualTo("REPO"));
    }

    [Test]
    public async Task Register_IsIdempotentByName_LastWins()
    {
        var catalog = New();
        await catalog.RegisterAsync(new RegisterWorkflowType(
            "wt", "A", "docker://img", new List<DeclaredSlot>(), new List<string>()), CancellationToken.None);
        await catalog.RegisterAsync(new RegisterWorkflowType(
            "wt", "B", "docker://img2", new List<DeclaredSlot>(), new List<string>()), CancellationToken.None);

        var list = await catalog.ListAsync(CancellationToken.None);
        Assert.That(list.Count(t => t.Name == "wt"), Is.EqualTo(1));
        Assert.That((await catalog.GetByNameAsync("wt", CancellationToken.None))!.DisplayName, Is.EqualTo("B"));
    }
}
