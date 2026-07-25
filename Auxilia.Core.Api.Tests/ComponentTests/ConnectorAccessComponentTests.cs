using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// End-to-end gating of identity-linked connectors: a run is dispatched only when its triggering
/// principal may use every connector its slots bind. The Administrator bootstrap principal has
/// WorkflowTrigger, so a 403 here is the connector gate, not a missing permission.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class ConnectorAccessComponentTests : CoreApiComponentTestBase
{
    private sealed record MeResponse(Guid PrincipalId, string? DisplayName, string[] Roles);

    private async Task<Guid> SeedConnectorAsync(string scope, Guid? owner = null, params ConnectorGrant[] grants)
    {
        var id = Guid.NewGuid();
        await Factory.Services.GetRequiredService<IDataAccess<CoreConnectorRecord>>().SaveAsync(new CoreConnectorRecord
        {
            Id = id,
            Name = $"c-{id:N}",
            ProviderType = "github",
            Scope = scope,
            OwnerPrincipalId = owner,
            GrantsJson = JsonSerializer.Serialize(grants)
        }, CancellationToken.None);
        return id;
    }

    private static object RunBinding(Guid connectorId) => new
    {
        workflowType = "wt",
        packageUri = "docker://img",
        slotBindings = new[] { new { slotName = "sc", connectorId } }
    };

    private static async Task<Guid> PrincipalIdAsync(HttpClient client)
        => (await client.GetFromJsonAsync<MeResponse>("/auth/me"))!.PrincipalId;

    [Test]
    public async Task Dispatch_IsDenied_ForAPersonalConnectorOfAnotherPrincipal()
    {
        var admin = CreateClient();
        var strangersConnector = await SeedConnectorAsync(ConnectorScope.Personal, owner: Guid.NewGuid());

        var response = await admin.PostAsJsonAsync("/api/runs", RunBinding(strangersConnector));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
            "The triggering principal neither owns nor is granted the personal connector.");
    }

    [Test]
    public async Task Dispatch_IsAllowed_ForACompanyConnector()
    {
        var response = await CreateClient().PostAsJsonAsync(
            "/api/runs", RunBinding(await SeedConnectorAsync(ConnectorScope.Company)));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Dispatch_IsAllowed_ForTheConnectorsOwner()
    {
        var admin = CreateClient();
        var mine = await SeedConnectorAsync(ConnectorScope.Personal, owner: await PrincipalIdAsync(admin));

        var response = await admin.PostAsJsonAsync("/api/runs", RunBinding(mine));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Dispatch_IsAllowed_ViaADirectPrincipalGrant()
    {
        var admin = CreateClient();
        var granted = await SeedConnectorAsync(ConnectorScope.Personal, owner: Guid.NewGuid(),
            new ConnectorGrant(ConnectorGrantKind.Principal, (await PrincipalIdAsync(admin)).ToString("D")));

        var response = await admin.PostAsJsonAsync("/api/runs", RunBinding(granted));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task CreatePersonalConnector_IsOwnedByTheCreator()
    {
        var admin = CreateClient();
        var adminId = await PrincipalIdAsync(admin);

        var created = await (await admin.PostAsJsonAsync("/api/connectors",
                new { name = "my-ado", providerType = "ado", settings = new { pat = "secret" }, scope = "Personal" }))
            .Content.ReadFromJsonAsync<Connector>();

        Assert.Multiple(() =>
        {
            Assert.That(created!.Scope, Is.EqualTo("Personal"));
            Assert.That(created.OwnerPrincipalId, Is.EqualTo(adminId));
        });
    }

    [Test]
    public async Task SetGrants_ThenGet_ReflectsTheGrant()
    {
        var admin = CreateClient();
        var created = await (await admin.PostAsJsonAsync("/api/connectors",
                new { name = "my-ado", providerType = "ado", settings = new { pat = "secret" }, scope = "Personal" }))
            .Content.ReadFromJsonAsync<Connector>();

        var granted = await admin.PostAsJsonAsync($"/api/connectors/{created!.Id}/grants",
            new { grants = new[] { new { kind = "DirectoryGroup", id = "group-devs" } } });
        Assert.That(granted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        var fetched = await admin.GetFromJsonAsync<Connector>($"/api/connectors/{created.Id}");
        Assert.That(fetched!.Grants, Has.One.Matches<ConnectorGrant>(
            g => g is { Kind: "DirectoryGroup", Id: "group-devs" }));
    }
}
