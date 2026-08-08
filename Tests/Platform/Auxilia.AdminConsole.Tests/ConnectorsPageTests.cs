using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.AdminConsole.Support;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The connectors page lists connectors from <c>QueryConnectorsAsync</c>, creates one via
/// <c>CreateConnectorAsync</c> with the entered settings (secret included, from a catalog-driven form),
/// and edits personal-connector sharing via <c>SetConnectorGrantsAsync</c> from a side drawer.
/// Hermetic — no network.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class ConnectorsPageTests
{
    private static BunitContext NewContext(FakeCoreClient core)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ICoreClient>(core);
        ctx.Services.AddSingleton(new ViewRendererRegistry([]));
        ctx.Services.AddSingleton(new StepUpFlow(core));
        ctx.Services.AddSingleton(new SharingDirectory(core));
        return ctx;
    }

    private static Connector Connector(Guid id, string name, string provider, string scope)
        => new(id, name, provider, ["token"], DateTimeOffset.UtcNow, scope);

    [Test]
    public void Connectors_ListsConnectors()
    {
        var core = new FakeCoreClient();
        core.Connectors.Add(Connector(Guid.NewGuid(), "Team ADO", "azure-devops", ResourceScope.Company));
        using var ctx = NewContext(core);

        var cut = ctx.Render<Connectors>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Team ADO"));
            Assert.That(cut.Markup, Does.Contain("azure-devops"));
            Assert.That(cut.Markup, Does.Contain("token"), "the stored setting keys are shown (never values)");
        });
    }

    [Test]
    public void Connectors_Create_CallsCreateWithEnteredSecret()
    {
        var core = new FakeCoreClient();
        core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "azure-devops", true, "SourceControl",
            [new ProviderSettingDescriptor("token", "Personal access token", "Secret", true, null, null, null, false)],
            ["ISourceControlAccess"], null));
        using var ctx = NewContext(core);
        var cut = ctx.Render<Connectors>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "New connector").Click();
        cut.Find("input[placeholder='e.g. Team Azure DevOps']").Change("My ADO");
        cut.Find("input[list='provider-types']").Change("azure-devops"); // seeds the secret field from the catalog
        cut.Find("input[type='password']").Change("secret-pat-123");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Create connector").Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.LastCreatedConnector, Is.Not.Null, "create calls CreateConnectorAsync");
            Assert.That(core.LastCreatedConnector!.Name, Is.EqualTo("My ADO"));
            Assert.That(core.LastCreatedConnector.ProviderType, Is.EqualTo("azure-devops"));
            Assert.That(core.LastCreatedConnector.Settings["token"], Is.EqualTo("secret-pat-123"),
                "the entered secret is submitted in the settings");
        });
    }

    [Test]
    public void Connectors_SetGrants_CallsSetConnectorGrants()
    {
        var id = Guid.NewGuid();
        var core = new FakeCoreClient();
        core.Connectors.Add(Connector(id, "Personal ADO", "azure-devops", ResourceScope.Personal));
        using var ctx = NewContext(core);
        var cut = ctx.Render<Connectors>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Sharing").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add subject").Click();
        cut.Find(".drawer .grant-row input").Change("principal-123");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save sharing").Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.GrantCalls, Has.Count.EqualTo(1), "save calls SetConnectorGrantsAsync");
            Assert.That(core.GrantCalls[0].Id, Is.EqualTo(id));
            Assert.That(core.GrantCalls[0].Request.Grants[0].Id, Is.EqualTo("principal-123"));
        });
    }
}
