using System.Net;
using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The identity-sources admin page lists configured sources and connector types from the fake
/// <see cref="ICoreClient"/> and drives an import via <c>ImportIdentitySourceAsync</c>.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class IdentitySourcesPageTests
{
    private static BunitContext NewContext(FakeCoreClient core)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ICoreClient>(core);
        ctx.Services.AddSingleton(new ViewRendererRegistry([]));
        return ctx;
    }

    private static FakeCoreClient WithSource(out Guid sourceId)
    {
        var core = new FakeCoreClient();
        core.IdentityConnectors.Add(new IdentityConnectorDescriptorDto(
            "ldap", "LDAP / Active Directory", "Import from a directory server",
            [new IdentityConnectorSettingDto("host", "Host", "Text", true, null, null, false)]));
        sourceId = Guid.NewGuid();
        core.IdentitySources.Add(new IdentitySourceDto(
            sourceId, "Corporate AD", "ldap", false, "User",
            new Dictionary<string, string>(), new Dictionary<string, string> { ["host"] = "dc01" },
            ["bindPassword"], null, null));
        return core;
    }

    [Test]
    public void IdentitySources_DisplaysConfiguredSources()
    {
        var core = WithSource(out _);
        using var ctx = NewContext(core);

        var cut = ctx.Render<AdminIdentitySources>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Corporate AD"));
            Assert.That(cut.Markup, Does.Contain("LDAP / Active Directory"), "the connector display name is resolved");
        });
    }

    [Test]
    public void IdentitySources_ImportNow_CallsImport()
    {
        var core = WithSource(out var sourceId);
        using var ctx = NewContext(core);
        var cut = ctx.Render<AdminIdentitySources>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Import now").Click();

        Assert.That(core.ImportedSources, Does.Contain(sourceId), "Import now calls ImportIdentitySourceAsync with the source id");
    }

    [Test]
    public void IdentitySources_ShowsAccessDenied_OnForbidden()
    {
        var core = new FakeCoreClient
        {
            IdentitySourcesError = new CoreApiException(HttpStatusCode.Forbidden, "identity-source.manage required", "denied")
        };
        using var ctx = NewContext(core);

        var cut = ctx.Render<AdminIdentitySources>();

        Assert.That(cut.Markup, Does.Contain("Access denied"));
    }
}
