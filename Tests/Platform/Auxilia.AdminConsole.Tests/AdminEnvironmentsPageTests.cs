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
/// Environments are their own admin surface: layers (base + pinned base version + software version
/// + setup script), the base-version catalog they pin to, availability, and per-layer sharing.
/// They used to be expressible only as checkboxes inside the workflow editor.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class AdminEnvironmentsPageTests
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

    private static EnvironmentLayerDto Layer(
        string name, string environmentBase = "linux", string? baseVersion = null,
        string? version = null, IReadOnlyList<AccessGrant>? grants = null)
        => new(name, $"{name} description",
            [new EnvironmentLayerVariant(environmentBase, "apt-get install -y thing", baseVersion)],
            version, DateTimeOffset.UtcNow) { Grants = grants ?? [] };

    [Test]
    public void Environments_ListsLayers_WithBasePinAndAccess()
    {
        var core = new FakeCoreClient();
        core.EnvironmentLayers.Add(Layer("dotnet-10", baseVersion: "ubuntu-24.04", version: "10.0.100"));
        core.EnvironmentLayers.Add(Layer("blender", grants: [new AccessGrant(AccessGrantKind.Principal, Guid.NewGuid().ToString())]));
        core.EnvironmentBases.Add(new EnvironmentBaseDto("linux", "ubuntu-24.04", "LTS", DateTimeOffset.UtcNow));
        using var ctx = NewContext(core);

        var cut = ctx.Render<AdminEnvironments>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("dotnet-10"));
            Assert.That(cut.Markup, Does.Contain("pinned to ubuntu-24.04"));
            Assert.That(cut.Markup, Does.Contain("10.0.100"));
            Assert.That(cut.Markup, Does.Contain("any version"), "an unpinned layer says so");
            Assert.That(cut.Markup, Does.Contain("1 subject"), "a restricted layer shows its access chip");
            Assert.That(cut.Markup, Does.Contain("admin-only (default)"),
                "an ungranted layer shows the restricted platform default");
        });
    }

    [Test]
    public void Environments_EmptyBaseCatalog_OffersCuratedSeeds_OneClickRegisters()
    {
        var core = new FakeCoreClient();
        using var ctx = NewContext(core);
        var cut = ctx.Render<AdminEnvironments>();

        Assert.That(cut.Markup, Does.Contain("ubuntu-24.04").And.Contain("server-2022"),
            "the curated seed list is offered while the catalog is empty");

        cut.FindAll("button").First(b => b.TextContent.Contains("linux / ubuntu-24.04")).Click();

        cut.WaitForAssertion(() => Assert.That(
            core.EnvironmentBases.Any(b => b.Name == "linux" && b.Version == "ubuntu-24.04"),
            Is.True, "one click registers the suggested base — the admin stays the curator"));
    }

    [Test]
    public void Environments_SeedSuggestions_AreFilteredByTheFleetPlatform()
    {
        var core = new FakeCoreClient();
        core.Runners.Add(new RunnerDto(
            Guid.NewGuid(), "Auxilia.Core.Runner", DateTimeOffset.UtcNow, Alive: true,
            HostPlatform: "linux", HostArchitecture: "x86_64"));
        using var ctx = NewContext(core);

        var cut = ctx.Render<AdminEnvironments>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("linux / ubuntu-24.04"));
            Assert.That(cut.Markup, Does.Not.Contain("server-2022"),
                "a linux-only fleet hides windows seed suggestions");
        });
    }

    [Test]
    public void Environments_UngrantedLayer_ReadsAsOpen_WhenThePlatformDefaultIsOpen()
    {
        var core = new FakeCoreClient();
        core.EnvironmentLayers.Add(Layer("dotnet-10"));
        core.PlatformSettings.Add(new PlatformSettingDto(
            PlatformSettingKeys.DefaultResourceAccess, DefaultResourceAccessModes.Open, DateTimeOffset.UtcNow));
        using var ctx = NewContext(core);

        var cut = ctx.Render<AdminEnvironments>();

        Assert.That(cut.Markup, Does.Contain("everyone (default open)"),
            "the open posture is reflected on ungranted layers");
    }

    [Test]
    public void Environments_CreateLayer_UpsertsWithScriptAndBase()
    {
        var core = new FakeCoreClient();
        core.EnvironmentBases.Add(new EnvironmentBaseDto("linux", "ubuntu-24.04", null, DateTimeOffset.UtcNow));
        using var ctx = NewContext(core);
        var cut = ctx.Render<AdminEnvironments>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "New layer").Click();
        cut.Find("input[placeholder='e.g. dotnet-10']").Change("blender");
        cut.Find("textarea").Change("apt-get install -y blender");
        cut.FindAll("select").First(s => s.TextContent.Contains("Any version")).Change("ubuntu-24.04");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Create layer").Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.UpsertedLayers, Has.Count.EqualTo(1));
            Assert.That(core.UpsertedLayers[0].ProviderType, Is.EqualTo("blender"));
            Assert.That(core.UpsertedLayers[0].SetupScript, Is.EqualTo("apt-get install -y blender"));
            Assert.That(core.UpsertedLayers[0].BaseVersion, Is.EqualTo("ubuntu-24.04"));
        });
    }

    [Test]
    public void Environments_EditLayer_PrefillsAndKeepsTheNameImmutable()
    {
        var core = new FakeCoreClient();
        core.EnvironmentLayers.Add(Layer("dotnet-10", version: "10.0.100"));
        using var ctx = NewContext(core);
        var cut = ctx.Render<AdminEnvironments>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();

        var nameInput = cut.Find("input[placeholder='e.g. dotnet-10']");
        Assert.Multiple(() =>
        {
            Assert.That(nameInput.GetAttribute("value"), Is.EqualTo("dotnet-10"));
            Assert.That(nameInput.HasAttribute("disabled"), Is.True,
                "a layer's name is what workflows request — renaming would silently break them");
            Assert.That(cut.Find("textarea").GetAttribute("value"), Does.Contain("apt-get"));
        });

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save variant").Click();
        Assert.That(core.UpsertedLayers[0].ProviderType, Is.EqualTo("dotnet-10"));
    }

    [Test]
    public void Environments_EditLayer_SwitchingTheBase_EditsThatVariantInPlace()
    {
        var core = new FakeCoreClient();
        core.EnvironmentLayers.Add(new EnvironmentLayerDto("dotnet-10", null,
            [
                new EnvironmentLayerVariant("linux", "apt-get install -y dotnet"),
                new EnvironmentLayerVariant("windows", "choco install -y dotnet"),
            ],
            "10.0", DateTimeOffset.UtcNow));
        using var ctx = NewContext(core);
        var cut = ctx.Render<AdminEnvironments>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        Assert.That(cut.Find("textarea").GetAttribute("value"), Does.Contain("apt-get"));

        cut.FindAll("select").First(s => s.TextContent.Contains("windows")).Change("windows");
        Assert.That(cut.Find("textarea").GetAttribute("value"), Does.Contain("choco"),
            "each base carries its own setup script — switching the base swaps the variant being edited");

        cut.Find("textarea").Change("winget install dotnet");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save variant").Click();
        Assert.Multiple(() =>
        {
            Assert.That(core.UpsertedLayers.Single().BaseEnvironment, Is.EqualTo("windows"));
            Assert.That(core.UpsertedLayers.Single().SetupScript, Is.EqualTo("winget install dotnet"));
        });
    }

    [Test]
    public void Environments_RemoveVariant_GoesThroughTheVariantEndpoint_AfterConfirm()
    {
        var core = new FakeCoreClient();
        core.EnvironmentLayers.Add(new EnvironmentLayerDto("dotnet-10", null,
            [
                new EnvironmentLayerVariant("linux", "apt-get install -y dotnet"),
                new EnvironmentLayerVariant("windows", "choco install -y dotnet"),
            ],
            "10.0", DateTimeOffset.UtcNow));
        using var ctx = NewContext(core);
        var cut = ctx.Render<AdminEnvironments>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Remove variant").Click();
        Assert.That(core.DeletedLayerVariants, Is.Empty, "arming must not remove");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Yes, remove").Click();
        Assert.That(core.DeletedLayerVariants, Is.EqualTo(new[] { ("dotnet-10", "linux") }));
    }

    [Test]
    public void Environments_DeleteLayer_AsksFirst()
    {
        var core = new FakeCoreClient();
        core.EnvironmentLayers.Add(Layer("dotnet-10"));
        using var ctx = NewContext(core);
        var cut = ctx.Render<AdminEnvironments>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Delete").Click();
        Assert.That(core.DeletedLayers, Is.Empty, "arming must not delete");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Yes, delete").Click();
        Assert.That(core.DeletedLayers, Is.EqualTo(new[] { "dotnet-10" }));
    }

    [Test]
    public void Environments_AvailabilityToggle_GoesThroughTheCatalog()
    {
        var core = new FakeCoreClient();
        core.EnvironmentLayers.Add(Layer("dotnet-10"));
        core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "dotnet-10", Available: true, Category: "Environment", Descriptors: [], Contracts: [],
            Description: null, ComposesEnvironment: true));
        using var ctx = NewContext(core);
        var cut = ctx.Render<AdminEnvironments>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Make unavailable").Click();

        Assert.That(core.AvailabilityChanges, Is.EqualTo(new[] { ("dotnet-10", false) }),
            "availability lives on the catalog entry the layer shares its name with");
    }

    [Test]
    public void Environments_Sharing_RestrictsTheLayerToTheChosenSubjects()
    {
        var principalId = Guid.NewGuid();
        var core = new FakeCoreClient
        {
            SharingSubjects = new SharingSubjects([new SharingPrincipal(principalId, "Jane", "Human")], [])
        };
        core.EnvironmentLayers.Add(Layer("dotnet-10"));
        using var ctx = NewContext(core);
        var cut = ctx.Render<AdminEnvironments>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Sharing").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add subject").Click();
        cut.Find(".drawer .grant-row select[aria-label='Principal']").Change(principalId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save sharing").Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.LayerGrantCalls, Has.Count.EqualTo(1));
            Assert.That(core.LayerGrantCalls[0].ProviderType, Is.EqualTo("dotnet-10"));
            Assert.That(core.LayerGrantCalls[0].Grants[0].Id, Is.EqualTo(principalId.ToString()));
        });
    }

    [Test]
    public void Environments_Sharing_EmptyList_ReopensTheLayer()
    {
        var core = new FakeCoreClient();
        core.EnvironmentLayers.Add(Layer("dotnet-10",
            grants: [new AccessGrant(AccessGrantKind.Principal, Guid.NewGuid().ToString())]));
        using var ctx = NewContext(core);
        var cut = ctx.Render<AdminEnvironments>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Sharing").Click();
        cut.FindAll(".drawer button").First(b => b.TextContent.Trim() == "Remove").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save sharing").Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.LayerGrantCalls[0].Grants, Is.Empty);
            Assert.That(cut.Markup, Does.Contain("follows the platform default access again"));
        });
    }

    [Test]
    public void Environments_AddBaseVersion_RegistersIt()
    {
        var core = new FakeCoreClient();
        using var ctx = NewContext(core);
        var cut = ctx.Render<AdminEnvironments>();

        cut.Find("input[placeholder='e.g. linux']").Change("linux");
        cut.Find("input[placeholder='e.g. ubuntu-24.04']").Change("ubuntu-24.04");
        cut.Find("input[placeholder='optional']").Change("LTS");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add base version").Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.UpsertedBases, Has.Count.EqualTo(1));
            Assert.That(core.UpsertedBases[0].Name, Is.EqualTo("linux"));
            Assert.That(core.UpsertedBases[0].Version, Is.EqualTo("ubuntu-24.04"));
            Assert.That(core.UpsertedBases[0].Description, Is.EqualTo("LTS"));
        });
    }

    [Test]
    public void Environments_BaseVersion_ShowsHowManyLayersPinIt_AndDeletesWithConfirm()
    {
        var core = new FakeCoreClient();
        core.EnvironmentBases.Add(new EnvironmentBaseDto("linux", "ubuntu-24.04", null, DateTimeOffset.UtcNow));
        core.EnvironmentLayers.Add(Layer("dotnet-10", baseVersion: "ubuntu-24.04"));
        using var ctx = NewContext(core);
        var cut = ctx.Render<AdminEnvironments>();

        Assert.That(cut.Markup, Does.Contain("1 layer"), "deleting a pinned base is not a blind action");

        // Scope to the bases section — the layers table has a Delete of its own.
        var basesSection = cut.FindAll("section")[1];
        basesSection.QuerySelectorAll("button").First(b => b.TextContent.Trim() == "Delete").Click();
        cut.FindAll("section")[1].QuerySelectorAll("button")
            .First(b => b.TextContent.Trim() == "Yes, delete").Click();

        Assert.That(core.DeletedBases, Is.EqualTo(new[] { ("linux", "ubuntu-24.04") }));
    }

    [Test]
    public void Environments_ShowsAccessDenied_OnForbidden()
    {
        var core = new FakeCoreClient
        {
            EnvironmentsError = new CoreApiException(
                HttpStatusCode.Forbidden, "provider-catalog.manage required", "denied")
        };
        using var ctx = NewContext(core);

        var cut = ctx.Render<AdminEnvironments>();

        Assert.That(cut.Markup, Does.Contain("Access denied"));
    }

    [Test]
    public void Environments_EmptyCatalog_OffersTheFirstLayer()
    {
        using var ctx = NewContext(new FakeCoreClient());

        var cut = ctx.Render<AdminEnvironments>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("No environment layers"));
            Assert.That(cut.Markup, Does.Contain("Create your first layer"));
        });
    }
}
