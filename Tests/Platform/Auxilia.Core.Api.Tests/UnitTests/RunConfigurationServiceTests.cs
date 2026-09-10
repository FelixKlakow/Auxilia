using Auxilia.Core.Api;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using System.Security.Cryptography;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
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
        => NewService(out store, out memberships, out _, out _);

    private static RunConfigurationService NewService(
        out InMemoryDataAccess<CoreRunConfigurationRecord> store,
        out InMemoryDataAccess<GroupMembershipRecord> memberships,
        out ProviderCatalogService catalog,
        out AesGcmSettingsProtector protector)
    {
        store = new InMemoryDataAccess<CoreRunConfigurationRecord>();
        memberships = new InMemoryDataAccess<GroupMembershipRecord>();
        protector = new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32));
        var grants = new AccessGrantEvaluator(new InMemoryDataAccess<PrincipalRecord>(), memberships);
        catalog = new ProviderCatalogService(
            new InMemoryDataAccess<SlotProviderRecord>(),
            new InMemoryDataAccess<ProviderCatalogRecord>(),
            new AuditLog(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System));
        var connectors = new ConnectorService(
            new InMemoryDataAccess<CoreConnectorRecord>(), protector, grants, TimeProvider.System);
        var workspaces = new WorkspaceResourceService(
            new InMemoryDataAccess<CoreWorkspaceRecord>(), grants, TimeProvider.System);
        return new RunConfigurationService(
            store, grants, new SlotBindingSecrets(catalog, connectors, workspaces, protector), TimeProvider.System);
    }

    private static ConfigurationViewer Viewer(Guid? principal, bool seesAll = false) => new(principal, seesAll);

    private static Task RegisterSecretProviderAsync(ProviderCatalogService catalog)
        => catalog.RegisterAsync("test", new RegisterSlotProvider(
            "local-agent", "coding-agent", null, ["ICodingAgent"],
            [
                new RegisterProviderSetting("apiKey", "API key", "Secret", Required: true),
                new RegisterProviderSetting("model", "Model", "Text"),
            ]), CancellationToken.None);

    private static CreateRunConfiguration WithSecret(string apiKey) => new("cfg", "wt",
        SlotBindings: [new SlotBinding("agent", "local-agent", Settings: new Dictionary<string, string>
        {
            ["apiKey"] = apiKey, ["model"] = "m1"
        })]);

    [Test]
    public async Task Create_ProtectsInlineSecretSettings_AndMasksThemOnEveryRead()
    {
        var service = NewService(out var store, out _, out var catalog, out var protector);
        await RegisterSecretProviderAsync(catalog);

        var created = await service.CreateAsync(WithSecret("sk-plain"), ownerPrincipalId: null, CancellationToken.None);

        var record = (await store.ReadAsync()).Single();
        Assert.That(record.SlotBindingsJson, Does.Not.Contain("sk-plain"), "never stored in the clear");
        Assert.Multiple(async () =>
        {
            Assert.That(created.SlotBindings[0].Settings!["apiKey"], Is.EqualTo(SlotBindingSecrets.Masked),
                "the create response is a read: key present, value masked");
            Assert.That(created.SlotBindings[0].Settings!["model"], Is.EqualTo("m1"));
            var read = await service.GetAsync(created.Id, CancellationToken.None);
            Assert.That(read!.SlotBindings[0].Settings!["apiKey"], Is.EqualTo(SlotBindingSecrets.Masked));
            var listed = await service.QueryAsync(new ConfigurationQuery(), Viewer(null, true), CancellationToken.None);
            Assert.That(listed.Items[0].SlotBindings[0].Settings!["apiKey"], Is.EqualTo(SlotBindingSecrets.Masked));
            var dispatch = await service.GetForDispatchAsync(created.Id, CancellationToken.None);
            Assert.That(protector.Unprotect(dispatch!.SlotBindings[0].Settings!["apiKey"]), Is.EqualTo("sk-plain"),
                "the dispatch read carries the protected value the resolver decrypts just-in-time");
        });
    }

    [Test]
    public async Task Update_EmptySecretKeepsTheStoredValue_NonEmptyReplacesIt()
    {
        var service = NewService(out _, out _, out var catalog, out var protector);
        await RegisterSecretProviderAsync(catalog);
        var created = await service.CreateAsync(WithSecret("sk-one"), ownerPrincipalId: null, CancellationToken.None);

        // An editor echoes the masked read back: empty secret = keep, other settings replaced.
        await service.UpdateAsync(created.Id, new UpdateRunConfiguration(
            SlotBindings: [new SlotBinding("agent", "local-agent", Settings: new Dictionary<string, string>
            {
                ["apiKey"] = "", ["model"] = "m2"
            })]), CancellationToken.None);
        var kept = await service.GetForDispatchAsync(created.Id, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(protector.Unprotect(kept!.SlotBindings[0].Settings!["apiKey"]), Is.EqualTo("sk-one"),
                "an empty Secret-kind value keeps the stored secret");
            Assert.That(kept.SlotBindings[0].Settings!["model"], Is.EqualTo("m2"));
        });

        // Omitting the key entirely (the editor drops empty values) keeps it too.
        await service.UpdateAsync(created.Id, new UpdateRunConfiguration(
            SlotBindings: [new SlotBinding("agent", "local-agent", Settings: new Dictionary<string, string>
            {
                ["model"] = "m3"
            })]), CancellationToken.None);
        var omitted = await service.GetForDispatchAsync(created.Id, CancellationToken.None);
        Assert.That(protector.Unprotect(omitted!.SlotBindings[0].Settings!["apiKey"]), Is.EqualTo("sk-one"));

        // A new value rotates the secret.
        await service.UpdateAsync(created.Id, new UpdateRunConfiguration(
            SlotBindings: [new SlotBinding("agent", "local-agent", Settings: new Dictionary<string, string>
            {
                ["apiKey"] = "sk-two"
            })]), CancellationToken.None);
        var rotated = await service.GetForDispatchAsync(created.Id, CancellationToken.None);
        Assert.That(protector.Unprotect(rotated!.SlotBindings[0].Settings!["apiKey"]), Is.EqualTo("sk-two"));
    }

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
