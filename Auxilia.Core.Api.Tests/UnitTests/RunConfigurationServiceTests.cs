using Auxilia.Core.Api;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class RunConfigurationServiceTests
{
    private static RunConfigurationService NewService(out InMemoryDataAccess<CoreRunConfigurationRecord> store)
    {
        store = new InMemoryDataAccess<CoreRunConfigurationRecord>();
        return new RunConfigurationService(store, TimeProvider.System);
    }

    [Test]
    public async Task CreateThenGet_RoundTripsContext()
    {
        var service = NewService(out _);

        var created = await service.CreateAsync(
            new CreateRunConfiguration("cfg", "wt", "pkg",
                new Dictionary<string, string> { ["k"] = "v" }),
            CancellationToken.None);

        var loaded = await service.GetAsync(created.Id, CancellationToken.None);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.WorkflowType, Is.EqualTo("wt"));
        Assert.That(loaded.PackageUri, Is.EqualTo("pkg"));
        Assert.That(loaded.Context["k"], Is.EqualTo("v"));
    }

    [Test]
    public async Task Ensure_IsIdempotentByName()
    {
        var service = NewService(out _);
        var seed = new StaticRunConfiguration
        {
            Name = "seeded", WorkflowType = "wt", PackageUri = "pkg"
        };

        await service.EnsureAsync(seed, CancellationToken.None);
        await service.EnsureAsync(seed, CancellationToken.None);

        var page = await service.QueryAsync(new ConfigurationQuery(), CancellationToken.None);
        Assert.That(page.Items.Count(c => c.Name == "seeded"), Is.EqualTo(1));
    }

    [Test]
    public async Task Query_FiltersByWorkflowType()
    {
        var service = NewService(out _);
        await service.CreateAsync(new CreateRunConfiguration("a", "type-a", "pkg"), CancellationToken.None);
        await service.CreateAsync(new CreateRunConfiguration("b", "type-b", "pkg"), CancellationToken.None);

        var page = await service.QueryAsync(new ConfigurationQuery(WorkflowType: "type-a"), CancellationToken.None);
        Assert.That(page.Items, Has.Count.EqualTo(1));
        Assert.That(page.Items[0].Name, Is.EqualTo("a"));
    }
}
