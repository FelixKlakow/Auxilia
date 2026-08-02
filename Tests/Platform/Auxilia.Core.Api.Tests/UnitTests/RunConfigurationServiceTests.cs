using Auxilia.Core.Api;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class RunConfigurationServiceTests
{
    private static RunConfigurationService NewService(out InMemoryDataAccess<CoreRunConfigurationRecord> store)
        => NewService(out store, out _);

    private static RunConfigurationService NewService(
        out InMemoryDataAccess<CoreRunConfigurationRecord> store,
        out InMemoryDataAccess<GroupMembershipRecord> memberships)
    {
        store = new InMemoryDataAccess<CoreRunConfigurationRecord>();
        memberships = new InMemoryDataAccess<GroupMembershipRecord>();
        return new RunConfigurationService(
            store,
            new AccessGrantEvaluator(new InMemoryDataAccess<PrincipalRecord>(), memberships),
            TimeProvider.System);
    }

    private static ConfigurationViewer Viewer(Guid? principal, bool seesAll = false) => new(principal, seesAll);

    [Test]
    public async Task CreateThenGet_RoundTripsContext()
    {
        var service = NewService(out _);

        var created = await service.CreateAsync(
            new CreateRunConfiguration("cfg", "wt",
                new Dictionary<string, string> { ["k"] = "v" }), ownerPrincipalId: null, CancellationToken.None);

        var loaded = await service.GetAsync(created.Id, CancellationToken.None);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.WorkflowType, Is.EqualTo("wt"));
        Assert.That(loaded.Context["k"], Is.EqualTo("v"));
    }

    [Test]
    public async Task Ensure_IsIdempotentByName()
    {
        var service = NewService(out _);
        var seed = new StaticRunConfiguration
        {
            Name = "seeded", WorkflowType = "wt"
        };

        await service.EnsureAsync(seed, CancellationToken.None);
        await service.EnsureAsync(seed, CancellationToken.None);

        var page = await service.QueryAsync(new ConfigurationQuery(), new ConfigurationViewer(null, true), CancellationToken.None);
        Assert.That(page.Items.Count(c => c.Name == "seeded"), Is.EqualTo(1));
    }

    [Test]
    public async Task Query_FiltersByWorkflowType()
    {
        var service = NewService(out _);
        await service.CreateAsync(new CreateRunConfiguration("a", "type-a"), ownerPrincipalId: null, CancellationToken.None);
        await service.CreateAsync(new CreateRunConfiguration("b", "type-b"), ownerPrincipalId: null, CancellationToken.None);

        var page = await service.QueryAsync(new ConfigurationQuery(WorkflowType: "type-a"), new ConfigurationViewer(null, true), CancellationToken.None);
        Assert.That(page.Items, Has.Count.EqualTo(1));
        Assert.That(page.Items[0].Name, Is.EqualTo("a"));
    }

    [Test]
    public async Task PersonalConfiguration_IsInvisibleToAStranger_ButVisibleToOwnerAndManager()
    {
        var service = NewService(out _);
        var owner = Guid.NewGuid();
        var created = await service.CreateAsync(
            new CreateRunConfiguration("mine", "wt", Scope: ResourceScope.Personal), owner, CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(await service.GetAsync(created.Id, Viewer(Guid.NewGuid()), CancellationToken.None), Is.Null,
                "A stranger reads a personal configuration as not-found.");
            Assert.That(await service.GetAsync(created.Id, Viewer(owner), CancellationToken.None), Is.Not.Null);
            Assert.That(await service.GetAsync(created.Id, Viewer(Guid.NewGuid(), seesAll: true), CancellationToken.None),
                Is.Not.Null, "A configuration manager sees every configuration.");
            Assert.That((await service.QueryAsync(
                    new ConfigurationQuery(), Viewer(Guid.NewGuid()), CancellationToken.None)).Items,
                Is.Empty, "Query filters invisible personal configurations out.");
        });
    }

    [Test]
    public async Task CompanyConfiguration_IsVisibleToEveryone()
    {
        var service = NewService(out _);
        await service.CreateAsync(
            new CreateRunConfiguration("shared", "wt", Scope: ResourceScope.Company),
            ownerPrincipalId: Guid.NewGuid(), CancellationToken.None);

        var page = await service.QueryAsync(new ConfigurationQuery(), Viewer(Guid.NewGuid()), CancellationToken.None);
        Assert.That(page.Items.Single().Name, Is.EqualTo("shared"));
        Assert.That(page.Items.Single().OwnerPrincipalId, Is.Null, "Company configurations carry no owner.");
    }

    [Test]
    public async Task PersonalConfiguration_GrantAdmitsPrincipal_AndGroupMember()
    {
        var service = NewService(out _, out var memberships);
        var owner = Guid.NewGuid();
        var friend = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var member = Guid.NewGuid();
        await memberships.SaveAsync(new GroupMembershipRecord
        {
            Id = GroupMembershipRecord.IdFor(groupId, member), GroupId = groupId, PrincipalId = member
        });

        var created = await service.CreateAsync(
            new CreateRunConfiguration("mine", "wt", Scope: ResourceScope.Personal), owner, CancellationToken.None);
        await service.SetGrantsAsync(created.Id,
        [
            new AccessGrant(AccessGrantKind.Principal, friend.ToString("D")),
            new AccessGrant(AccessGrantKind.Group, groupId.ToString("D"))
        ], CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(await service.IsVisibleAsync(created.Id, Viewer(friend), CancellationToken.None), Is.True);
            Assert.That(await service.IsVisibleAsync(created.Id, Viewer(member), CancellationToken.None), Is.True);
            Assert.That(await service.IsVisibleAsync(created.Id, Viewer(Guid.NewGuid()), CancellationToken.None), Is.False);
        });
    }

    [Test]
    public async Task SetGrants_OnACompanyConfiguration_Throws()
    {
        var service = NewService(out _);
        var created = await service.CreateAsync(
            new CreateRunConfiguration("shared", "wt", Scope: ResourceScope.Company), null, CancellationToken.None);

        Assert.ThrowsAsync<InvalidOperationException>(() => service.SetGrantsAsync(
            created.Id, [new AccessGrant(AccessGrantKind.Principal, Guid.NewGuid().ToString("D"))],
            CancellationToken.None));
    }

    [Test]
    public async Task IsOwner_TrueOnlyForThePersonalOwner()
    {
        var service = NewService(out _);
        var owner = Guid.NewGuid();
        var personal = await service.CreateAsync(
            new CreateRunConfiguration("mine", "wt", Scope: ResourceScope.Personal), owner, CancellationToken.None);
        var company = await service.CreateAsync(
            new CreateRunConfiguration("shared", "wt", Scope: ResourceScope.Company), owner, CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(await service.IsOwnerAsync(personal.Id, owner, CancellationToken.None), Is.True);
            Assert.That(await service.IsOwnerAsync(personal.Id, Guid.NewGuid(), CancellationToken.None), Is.False);
            Assert.That(await service.IsOwnerAsync(company.Id, owner, CancellationToken.None), Is.False,
                "Company configurations have no owner — management rights come from the permission.");
        });
    }
}
