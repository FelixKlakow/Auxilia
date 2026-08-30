using System.Security.Cryptography;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class ConnectorServiceTests
{
    private static ConnectorService New(out InMemoryDataAccess<CoreConnectorRecord> store)
        => New(out store, out _);

    private static ConnectorService New(
        out InMemoryDataAccess<CoreConnectorRecord> store,
        out InMemoryDataAccess<GroupMembershipRecord> memberships)
    {
        store = new InMemoryDataAccess<CoreConnectorRecord>();
        memberships = new InMemoryDataAccess<GroupMembershipRecord>();
        var protector = new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32));
        return new ConnectorService(
            store, protector,
            new AccessGrantEvaluator(new InMemoryDataAccess<PrincipalRecord>(), memberships),
            TimeProvider.System);
    }

    private static ConnectorViewer Viewer(Guid? principal, bool seesAll = false) => new(principal, seesAll);

    [Test]
    public async Task Create_StoresSettingsProtected_NotPlaintext()
    {
        var service = New(out var store);

        var dto = await service.CreateAsync(
            new CreateConnector("gh", "github",
                new Dictionary<string, string> { ["token"] = "secret-xyz" }),
            ownerPrincipalId: null, CancellationToken.None);

        Assert.That(dto.SettingKeys, Does.Contain("token"));
        var record = await store.ReadAsync(dto.Id, CancellationToken.None);
        Assert.That(record!.ProtectedSettingsJson, Does.Not.Contain("secret-xyz"),
            "Connector settings must be encrypted at rest.");
    }

    [Test]
    public async Task ResolveSettings_RoundTripsDecryptedValues()
    {
        var service = New(out _);

        var dto = await service.CreateAsync(
            new CreateConnector("gh", "github",
                new Dictionary<string, string> { ["token"] = "secret-xyz" }),
            ownerPrincipalId: null, CancellationToken.None);

        var resolved = await service.ResolveSettingsAsync(dto.Id, CancellationToken.None);
        Assert.That(resolved, Is.Not.Null);
        Assert.That(resolved!["token"], Is.EqualTo("secret-xyz"));
    }

    [Test]
    public async Task PersonalConnector_IsInvisibleToAStranger_ButVisibleToOwnerAndManager()
    {
        var service = New(out _);
        var owner = Guid.NewGuid();
        var created = await service.CreateAsync(
            new CreateConnector("mine", "github",
                new Dictionary<string, string> { ["token"] = "s" }, ResourceScope.Personal),
            owner, CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(await service.GetAsync(created.Id, Viewer(Guid.NewGuid()), CancellationToken.None), Is.Null,
                "A stranger reads a personal connector as not-found.");
            Assert.That(await service.GetAsync(created.Id, Viewer(owner), CancellationToken.None), Is.Not.Null);
            Assert.That(await service.GetAsync(created.Id, Viewer(Guid.NewGuid(), seesAll: true), CancellationToken.None),
                Is.Not.Null, "A connector manager sees every connector.");
            Assert.That((await service.QueryAsync(
                    new ConnectorQuery(), Viewer(Guid.NewGuid()), CancellationToken.None)).Items,
                Is.Empty, "Query filters invisible personal connectors out.");
        });
    }

    [Test]
    public async Task CompanyConnector_IsVisibleToEveryone()
    {
        var service = New(out _);
        await service.CreateAsync(
            new CreateConnector("shared", "github",
                new Dictionary<string, string> { ["token"] = "s" }, ResourceScope.Company),
            ownerPrincipalId: Guid.NewGuid(), CancellationToken.None);

        var page = await service.QueryAsync(new ConnectorQuery(), Viewer(Guid.NewGuid()), CancellationToken.None);
        Assert.That(page.Items.Single().Name, Is.EqualTo("shared"));
        Assert.That(page.Items.Single().OwnerPrincipalId, Is.Null, "Company connectors carry no owner.");
    }

    [Test]
    public async Task PersonalConnector_GrantAdmitsPrincipal_AndGroupMember()
    {
        var service = New(out _, out var memberships);
        var owner = Guid.NewGuid();
        var friend = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var member = Guid.NewGuid();
        await memberships.SaveAsync(new GroupMembershipRecord
        {
            Id = GroupMembershipRecord.IdFor(groupId, member), GroupId = groupId, PrincipalId = member
        });

        var created = await service.CreateAsync(
            new CreateConnector("mine", "github",
                new Dictionary<string, string> { ["token"] = "s" }, ResourceScope.Personal),
            owner, CancellationToken.None);
        await service.SetGrantsAsync(created.Id,
        [
            new AccessGrant(AccessGrantKind.Principal, friend.ToString("D")),
            new AccessGrant(AccessGrantKind.Group, groupId.ToString("D"))
        ], CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(await service.GetAsync(created.Id, Viewer(friend), CancellationToken.None), Is.Not.Null);
            Assert.That(await service.GetAsync(created.Id, Viewer(member), CancellationToken.None), Is.Not.Null);
            Assert.That(await service.GetAsync(created.Id, Viewer(Guid.NewGuid()), CancellationToken.None), Is.Null);
        });
    }
}
