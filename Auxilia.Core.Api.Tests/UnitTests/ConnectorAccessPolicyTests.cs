using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class ConnectorAccessPolicyTests
{
    private InMemoryDataAccess<CoreConnectorRecord> _connectors = null!;
    private InMemoryDataAccess<PrincipalRecord> _principals = null!;
    private ConnectorAccessPolicy _policy = null!;

    [SetUp]
    public void SetUp()
    {
        _connectors = new InMemoryDataAccess<CoreConnectorRecord>();
        _principals = new InMemoryDataAccess<PrincipalRecord>();
        _policy = new ConnectorAccessPolicy(_connectors, _principals);
    }

    [TearDown]
    public void TearDown()
    {
        _connectors.Dispose();
        _principals.Dispose();
    }

    private async Task<Guid> AddConnectorAsync(string scope, Guid? owner = null, params ConnectorGrant[] grants)
    {
        var id = Guid.NewGuid();
        await _connectors.SaveAsync(new CoreConnectorRecord
        {
            Id = id,
            Name = $"c-{id:N}",
            ProviderType = "github",
            Scope = scope,
            OwnerPrincipalId = owner,
            GrantsJson = JsonSerializer.Serialize(grants)
        });
        return id;
    }

    private async Task<Guid> AddPrincipalAsync(params string[] directoryGroups)
    {
        var id = Guid.NewGuid();
        await _principals.SaveAsync(new PrincipalRecord
        {
            Id = id,
            Kind = "Human",
            DisplayName = "u",
            Status = "Active",
            DirectoryGroupsJson = JsonSerializer.Serialize(directoryGroups)
        });
        return id;
    }

    private Task<bool> CanUseAsync(Guid connector, Guid? principal)
        => _policy.CanUseAsync(connector, principal, CancellationToken.None);

    [Test]
    public async Task CompanyConnector_IsUsableByAnyone()
        => Assert.That(await CanUseAsync(await AddConnectorAsync(ConnectorScope.Company), Guid.NewGuid()), Is.True);

    [Test]
    public async Task MissingConnector_IsNotAnAccessFailure()
        => Assert.That(await CanUseAsync(Guid.NewGuid(), Guid.NewGuid()), Is.True,
            "A missing connector is the resolver's not-found case, not an eligibility denial.");

    [Test]
    public async Task PersonalConnector_IsUsableByItsOwner()
    {
        var owner = await AddPrincipalAsync();
        Assert.That(await CanUseAsync(await AddConnectorAsync(ConnectorScope.Personal, owner), owner), Is.True);
    }

    [Test]
    public async Task PersonalConnector_IsDeniedToAStranger()
    {
        var connector = await AddConnectorAsync(ConnectorScope.Personal, await AddPrincipalAsync());
        Assert.That(await CanUseAsync(connector, await AddPrincipalAsync()), Is.False);
    }

    [Test]
    public async Task PersonalConnector_IsUsableByADirectPrincipalGrant()
    {
        var friend = await AddPrincipalAsync();
        var connector = await AddConnectorAsync(ConnectorScope.Personal, await AddPrincipalAsync(),
            new ConnectorGrant(ConnectorGrantKind.Principal, friend.ToString("D")));
        Assert.That(await CanUseAsync(connector, friend), Is.True);
    }

    [Test]
    public async Task PersonalConnector_IsUsableByAMemberOfAGrantedDirectoryGroup()
    {
        var teammate = await AddPrincipalAsync("group-devs");
        var connector = await AddConnectorAsync(ConnectorScope.Personal, await AddPrincipalAsync(),
            new ConnectorGrant(ConnectorGrantKind.DirectoryGroup, "group-devs"));
        Assert.That(await CanUseAsync(connector, teammate), Is.True,
            "The AD cascade: directory-group membership from sign-in admits the principal.");
    }

    [Test]
    public async Task PersonalConnector_IsDeniedToANonMemberOfTheGrantedGroup()
    {
        var outsider = await AddPrincipalAsync("group-other");
        var connector = await AddConnectorAsync(ConnectorScope.Personal, await AddPrincipalAsync(),
            new ConnectorGrant(ConnectorGrantKind.DirectoryGroup, "group-devs"));
        Assert.That(await CanUseAsync(connector, outsider), Is.False);
    }

    [Test]
    public async Task PersonalConnector_WithoutAnIdentifiedPrincipal_IsDenied()
        => Assert.That(await CanUseAsync(await AddConnectorAsync(ConnectorScope.Personal, await AddPrincipalAsync()), null),
            Is.False);
}
