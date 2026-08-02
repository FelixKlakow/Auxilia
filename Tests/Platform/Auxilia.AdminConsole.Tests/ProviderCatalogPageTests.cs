using System.Net;
using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The provider-catalog page renders the provider half from the fake <see cref="ICoreClient"/> and
/// toggles a provider's deny-by-default availability via <c>SetProviderAvailabilityAsync</c>. The
/// workflows half is a Workflow Studio placeholder (no Core endpoint).
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
    public void ProviderCatalog_DisplaysProviders_AndStudioPlaceholder()
    {
        var core = WithProvider();
        using var ctx = NewContext(core);

        var cut = ctx.Render<AdminProviderCatalog>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("github.pat"));
            Assert.That(cut.Markup, Does.Contain("Workflow Studio"), "the workflows half is a labelled placeholder");
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
