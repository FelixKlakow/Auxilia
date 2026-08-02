using System.Net;
using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using WorkflowsPage = Auxilia.AdminConsole.Components.Pages.Workflows;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The Workflows page lists stored configurations from <c>QueryConfigurationsAsync</c> and runs one via
/// <c>RunConfigurationAsync</c> as the signed-in user (no on-behalf-of). The editor lists workflow types,
/// fetches a picked type's schema, and saves a <c>CreateRunConfiguration</c> whose slot is bound to a
/// connector. Hermetic — no network.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class WorkflowsPageTests
{
    private static BunitContext NewContext(FakeCoreClient core)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ICoreClient>(core);
        ctx.Services.AddSingleton(new ViewRendererRegistry([]));
        return ctx;
    }

    private static RunConfiguration Config(Guid id, string name, string type, bool enabled)
        => new(id, name, type, new Dictionary<string, string>(), [], enabled, DateTimeOffset.UtcNow);

    [Test]
    public void Workflows_ListsConfigurations()
    {
        var core = new FakeCoreClient();
        core.Configurations.Add(Config(Guid.NewGuid(), "Nightly review", "code-review", true));
        using var ctx = NewContext(core);

        var cut = ctx.Render<WorkflowsPage>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Nightly review"));
            Assert.That(cut.Markup, Does.Contain("code-review"));
        });
    }

    [Test]
    public void Workflows_Run_CallsRunConfigurationAsSignedInUser()
    {
        var id = Guid.NewGuid();
        var core = new FakeCoreClient();
        core.Configurations.Add(Config(id, "Nightly review", "code-review", true));
        using var ctx = NewContext(core);
        var cut = ctx.Render<WorkflowsPage>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Run").Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.RunConfigurationCalls, Has.Count.EqualTo(1), "Run calls RunConfigurationAsync");
            Assert.That(core.RunConfigurationCalls[0].Id, Is.EqualTo(id));
            Assert.That(core.RunConfigurationCalls[0].OnBehalfOf, Is.Null, "runs as the signed-in user — no on-behalf-of");
        });
    }

    [Test]
    public void Workflows_ShowsAccessDenied_OnForbidden()
    {
        var core = new FakeCoreClient
        {
            ConfigurationsError = new CoreApiException(HttpStatusCode.Forbidden, "config.read required", "denied")
        };
        using var ctx = NewContext(core);

        var cut = ctx.Render<WorkflowsPage>();

        Assert.That(cut.Markup, Does.Contain("Access denied"));
    }

    [Test]
    public void Editor_ListsWorkflowTypes()
    {
        var core = new FakeCoreClient();
        core.WorkflowTypes.Add(new WorkflowTypeDto("code-review", "1.0.0", "Ephemeral", "Reviews PRs", []));
        using var ctx = NewContext(core);

        var cut = ctx.Render<WorkflowEditor>();

        Assert.That(cut.Markup, Does.Contain("code-review"));
    }

    [Test]
    public void Editor_SelectingType_FetchesSchemaAndShowsSlots()
    {
        var core = new FakeCoreClient();
        core.WorkflowTypes.Add(new WorkflowTypeDto("code-review", "1.0.0", "Ephemeral", null, []));
        core.WorkflowSchemas["code-review"] = Schema("code-review",
            new WorkflowSlotDto("source", "ISourceControlAccess", "The repo", false, null));
        using var ctx = NewContext(core);
        var cut = ctx.Render<WorkflowEditor>();

        cut.Find("select").Change("code-review");

        Assert.That(cut.Markup, Does.Contain("source"), "the picked type's slot is rendered");
    }

    [Test]
    public void Editor_BindSlotToConnector_AndSave_CallsCreateWithSlotBinding()
    {
        var connectorId = Guid.NewGuid();
        var core = new FakeCoreClient();
        core.WorkflowTypes.Add(new WorkflowTypeDto("code-review", "1.0.0", "Ephemeral", null, []));
        core.WorkflowSchemas["code-review"] = Schema("code-review",
            new WorkflowSlotDto("source", "ISourceControlAccess", null, false, null));
        core.Connectors.Add(new Connector(
            connectorId, "Team ADO", "azure-devops", ["token"], DateTimeOffset.UtcNow, ResourceScope.Company));
        using var ctx = NewContext(core);
        var cut = ctx.Render<WorkflowEditor>();

        cut.Find("input[placeholder='e.g. Nightly code review']").Change("My review");
        cut.FindAll("select")[0].Change("code-review");        // workflow type → fetches schema
        // selects: [0] workflow type, [1] scope, [2] the slot's connector picker
        cut.FindAll("select")[2].Change(connectorId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Create configuration").Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.LastCreatedConfiguration, Is.Not.Null, "save calls CreateConfigurationAsync");
            Assert.That(core.LastCreatedConfiguration!.Name, Is.EqualTo("My review"));
            Assert.That(core.LastCreatedConfiguration.WorkflowType, Is.EqualTo("code-review"));
            Assert.That(core.LastCreatedConfiguration.SlotBindings, Is.Not.Null.And.Count.EqualTo(1),
                "the slot binding is present");
            Assert.That(core.LastCreatedConfiguration.SlotBindings![0].SlotName, Is.EqualTo("source"));
            Assert.That(core.LastCreatedConfiguration.SlotBindings[0].ConnectorId, Is.EqualTo(connectorId));
        });
    }

    [Test]
    public void Editor_GenericBinding_NarrowsProvidersByContract_AndSavesProviderTypeWithSettings()
    {
        var core = new FakeCoreClient();
        core.WorkflowTypes.Add(new WorkflowTypeDto("impl", "1.0.0", "Ephemeral", null, []));
        core.WorkflowSchemas["impl"] = Schema("impl",
            new WorkflowSlotDto("agent", "coding-agent", null, false, null));
        core.ProviderCatalog.Add(CatalogEntry("claude-code-cli", ["coding-agent"],
            descriptors: [new ProviderSettingDescriptor("model", "Model", "Choice", false, null, "sonnet", ["sonnet", "opus"], false)]));
        core.ProviderCatalog.Add(CatalogEntry("smtp-mail", ["email-intake"]));
        using var ctx = NewContext(core);
        var cut = ctx.Render<WorkflowEditor>();

        cut.Find("input[placeholder='e.g. Nightly code review']").Change("Agent run");
        cut.FindAll("select")[0].Change("impl");
        // selects: [0] workflow type, [1] scope, [2] the slot's PROVIDER picker
        var providerSelect = cut.FindAll("select")[2];
        Assert.That(providerSelect.InnerHtml, Does.Contain("claude-code-cli").And.Not.Contain("smtp-mail"),
            "only providers serving the slot's contract are offered");
        providerSelect.Change("claude-code-cli");

        // The provider's declared settings render; the Choice descriptor becomes a select.
        var modelSelect = cut.FindAll("select").First(s => s.InnerHtml.Contains("opus"));
        modelSelect.Change("opus");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Create configuration").Click();

        var binding = core.LastCreatedConfiguration!.SlotBindings!.Single();
        Assert.Multiple(() =>
        {
            Assert.That(binding.SlotName, Is.EqualTo("agent"));
            Assert.That(binding.ProviderType, Is.EqualTo("claude-code-cli"));
            Assert.That(binding.ConnectorId, Is.Null, "no connector was picked — inline provider binding");
            Assert.That(binding.Settings!["model"], Is.EqualTo("opus"));
        });
    }

    [Test]
    public void Editor_EnvironmentSlot_MultiSelect_ConstrainedToOneBase()
    {
        var core = new FakeCoreClient();
        core.WorkflowTypes.Add(new WorkflowTypeDto("session", "1.0.0", "Ephemeral", null, []));
        core.WorkflowSchemas["session"] = Schema("session",
            new WorkflowSlotDto("environment", "environment-capability", null, true, null, AllowMultiple: true));
        core.ProviderCatalog.Add(EnvironmentLayer("dotnet-10", "linux"));
        core.ProviderCatalog.Add(EnvironmentLayer("node-22", "linux"));
        core.ProviderCatalog.Add(EnvironmentLayer("windows-vs", "windows"));
        using var ctx = NewContext(core);
        var cut = ctx.Render<WorkflowEditor>();

        cut.Find("input[placeholder='e.g. Nightly code review']").Change("Session");
        cut.FindAll("select")[0].Change("session");

        // Environment slots render as checkboxes (one per layer), not a connector select.
        cut.FindAll("input[type=checkbox]").First(c => c.Parent!.TextContent.Contains("dotnet-10")).Change(true);

        cut.WaitForAssertion(() =>
        {
            var windowsBox = cut.FindAll("input[type=checkbox]").First(c => c.Parent!.TextContent.Contains("windows-vs"));
            Assert.That(windowsBox.HasAttribute("disabled"), Is.True,
                "picking a linux layer disables layers of any other base");
        });
        cut.FindAll("input[type=checkbox]").First(c => c.Parent!.TextContent.Contains("node-22")).Change(true);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Create configuration").Click();

        var layers = core.LastCreatedConfiguration!.SlotBindings!.Where(b => b.SlotName == "environment").ToList();
        Assert.That(layers.Select(b => b.ProviderType), Is.EquivalentTo(new[] { "dotnet-10", "node-22" }),
            "each selected layer becomes its own ProviderType-only binding");
    }

    [Test]
    public void Editor_ChoiceInput_RendersChoiceLabels()
    {
        var core = new FakeCoreClient();
        core.WorkflowTypes.Add(new WorkflowTypeDto("impl", "1.0.0", "Ephemeral", null, []));
        core.WorkflowSchemas["impl"] = new WorkflowSchemaDto("impl", "1.0.0", "1", "Ephemeral", [], [],
            [new WorkflowInputDto("effort", "Effort", false, null, InputKinds.Choice,
                Choices: ["low", "high"], ChoiceLabels: new Dictionary<string, string> { ["low"] = "Low effort", ["high"] = "High effort" })],
            [], [], [], null, "{}");
        using var ctx = NewContext(core);
        var cut = ctx.Render<WorkflowEditor>();

        cut.FindAll("select")[0].Change("impl");

        Assert.That(cut.Markup, Does.Contain("Low effort").And.Contain("High effort"),
            "a Choice input renders its declared labels, not the raw values");
    }

    private static ProviderCatalogEntry CatalogEntry(
        string providerType, IReadOnlyList<string> contracts,
        IReadOnlyList<ProviderSettingDescriptor>? descriptors = null)
        => new(providerType, true, "capability", descriptors ?? [], contracts, null);

    private static ProviderCatalogEntry EnvironmentLayer(string providerType, string environmentBase)
        => new(providerType, true, "environment", [], ["environment-capability"], null,
            ComposesEnvironment: true, EnvironmentBase: environmentBase);

    private static WorkflowSchemaDto Schema(string type, params WorkflowSlotDto[] slots)
        => new(type, "1.0.0", "1", "Ephemeral", [], slots, [], [], [], [], null, "{}");
}
