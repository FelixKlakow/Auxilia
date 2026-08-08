using System.Net;
using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.AdminConsole.Support;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The provider-catalog page renders providers from the fake <see cref="ICoreClient"/>, toggles a
/// provider's deny-by-default availability via <c>SetProviderAvailabilityAsync</c>, and keeps
/// environment-composing entries in their OWN section — never mixed into the provider table.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class ProviderCatalogPageTests
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

    private static FakeCoreClient WithProvider()
    {
        var core = new FakeCoreClient();
        core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "github.pat", Available: false, Category: "SourceControl",
            Descriptors: [], Contracts: ["ISourceControl"], Description: "GitHub PAT provider"));
        return core;
    }

    [Test]
    public void ProviderCatalog_DisplaysProviders_AndLinksWorkflowTypes()
    {
        var core = WithProvider();
        using var ctx = NewContext(core);

        var cut = ctx.Render<AdminProviderCatalog>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("github.pat"));
            Assert.That(cut.Markup, Does.Contain("admin/workflow-types"),
                "workflow availability points at the registry page — the Studio is retired");
            Assert.That(cut.Markup, Does.Not.Contain("Workflow Studio"));
        });
    }

    [Test]
    public void ProviderCatalog_ExcludesEnvironmentLayers_AndLinksToTheirOwnTab()
    {
        var core = WithProvider();
        core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "dotnet-10", Available: true, Category: "Environment",
            Descriptors: [], Contracts: [], Description: ".NET 10 SDK layer",
            ComposesEnvironment: true, EnvironmentBase: "linux", EnvironmentBaseVersion: "24.04"));
        using var ctx = NewContext(core);

        var cut = ctx.Render<AdminProviderCatalog>();

        var providerTable = cut.FindAll("section")[0].TextContent;
        Assert.Multiple(() =>
        {
            Assert.That(providerTable, Does.Contain("github.pat"));
            Assert.That(cut.Markup, Does.Not.Contain("dotnet-10"),
                "an environment layer is not a slot provider — it belongs to the Environments tab");
            Assert.That(cut.Markup, Does.Contain("admin/environments"),
                "and the page points at the tab that does manage it");
        });
    }

    [Test]
    public void ProviderCatalog_ToggleAvailability_CallsSetAvailability()
    {
        var core = WithProvider();
        using var ctx = NewContext(core);
        var cut = ctx.Render<AdminProviderCatalog>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Make available").Click();

        Assert.That(core.AvailabilityChanges, Does.Contain(("github.pat", true)),
            "toggling calls SetProviderAvailabilityAsync(providerType, true)");
    }

    [Test]
    public void ProviderCatalog_ShowsAccessDenied_OnForbidden()
    {
        var core = new FakeCoreClient
        {
            ProviderCatalogError = new CoreApiException(HttpStatusCode.Forbidden, "provider-catalog.manage required", "denied")
        };
        using var ctx = NewContext(core);

        var cut = ctx.Render<AdminProviderCatalog>();

        Assert.That(cut.Markup, Does.Contain("Access denied"));
    }
}
