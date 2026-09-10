using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.AdminConsole.Support;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The editor's provider-eligibility filter (tool-provision matching): a catalog provider is
/// offered for a slot only when every tool it declares in <c>RequiredTools</c> appears in the
/// workflow schema's <c>ProvidedTools</c> (case-insensitively); a provider requiring no tools
/// matches on contract alone. Hermetic — no network.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class WorkflowEditorTests
{
    private const string Type = "impl";
    private const string Contract = "coding-agent";

    private static BunitContext NewContext(FakeCoreClient core)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ICoreClient>(core);
        ctx.Services.AddSingleton(new ViewRendererRegistry([]));
        ctx.Services.AddSingleton(new StepUpFlow(core));
        ctx.Services.AddSingleton(new SharingDirectory(core));
        return ctx;
    }

    private static FakeCoreClient CoreWithSchema(params string[] providedTools)
    {
        var core = new FakeCoreClient();
        core.WorkflowTypes.Add(new WorkflowTypeDto(Type, "1.0.0", "Ephemeral", null, []));
        core.WorkflowSchemas[Type] = new WorkflowSchemaDto(
            Type, "1.0.0", "1", "Ephemeral", [],
            [new WorkflowSlotDto("agent", Contract, null, false, null)],
            [], [], [], [], null, "{}")
        {
            ProvidedTools = providedTools
        };
        return core;
    }

    private static ProviderCatalogEntry Provider(string providerType, params string[] requiredTools)
        => new(providerType, true, "capability", [], [Contract], null)
        {
            RequiredTools = requiredTools
        };

    private static IRenderedComponent<WorkflowEditor> RenderWithTypePicked(BunitContext ctx)
    {
        var cut = ctx.Render<WorkflowEditor>();
        cut.FindAll("select")[0].Change(Type); // workflow type → fetches the schema
        return cut;
    }

    // selects: [0] workflow type, [1] scope, [2] the slot's provider picker
    private static string ProviderPickerHtml(IRenderedComponent<WorkflowEditor> cut)
        => cut.FindAll("select")[2].InnerHtml;

    [Test]
    public void Editor_ProviderWhoseRequiredToolsAreAllProvided_IsOffered()
    {
        var core = CoreWithSchema("claude-cli", "git");
        core.ProviderCatalog.Add(Provider("claude-code-cli", "claude-cli", "git"));
        using var ctx = NewContext(core);

        var cut = RenderWithTypePicked(ctx);

        Assert.That(ProviderPickerHtml(cut), Does.Contain("claude-code-cli"),
            "a provider whose every required tool is in the image's ProvidedTools is offered");
    }

    [Test]
    public void Editor_ProviderRequiringAToolTheImageLacks_IsHidden()
    {
        var core = CoreWithSchema("git");
        core.ProviderCatalog.Add(Provider("plain-agent"));                 // keeps the picker rendered
        core.ProviderCatalog.Add(Provider("claude-code-cli", "claude-cli"));
        using var ctx = NewContext(core);

        var cut = RenderWithTypePicked(ctx);

        Assert.That(ProviderPickerHtml(cut), Does.Contain("plain-agent").And.Not.Contain("claude-code-cli"),
            "a provider requiring a tool the workflow image does not bundle is never offered");
    }

    [Test]
    public void Editor_CaseVariantToolNames_StillMatch()
    {
        var core = CoreWithSchema("Claude-CLI");
        core.ProviderCatalog.Add(Provider("claude-code-cli", "claude-cli"));
        using var ctx = NewContext(core);

        var cut = RenderWithTypePicked(ctx);

        Assert.That(ProviderPickerHtml(cut), Does.Contain("claude-code-cli"),
            "tool-name matching is case-insensitive — vocabularies are open, casing is not load-bearing");
    }

    [Test]
    public void Editor_StoredSecretSetting_RendersEmptyWithAKeepPlaceholder()
    {
        var core = CoreWithSchema();
        core.ProviderCatalog.Add(new ProviderCatalogEntry(
            "local-agent", true, "capability",
            [new ProviderSettingDescriptor("apiKey", "API key", "Secret", true, null, null, null, false)],
            [Contract], null));
        var configurationId = Guid.NewGuid();
        // The Core masks a stored secret: the key is present, the value empty.
        core.Configurations.Add(new RunConfiguration(
            configurationId, "cfg", Type, new Dictionary<string, string>(),
            [new SlotBinding("agent", "local-agent", Settings: new Dictionary<string, string> { ["apiKey"] = "" })],
            true, DateTimeOffset.UtcNow));
        using var ctx = NewContext(core);

        var cut = ctx.Render<WorkflowEditor>(p => p.Add(e => e.ConfigurationId, configurationId));

        var secretInput = cut.Find("input[type=password]");
        Assert.Multiple(() =>
        {
            Assert.That(secretInput.GetAttribute("value") ?? "", Is.Empty,
                "no secret value is ever rendered into the page");
            Assert.That(secretInput.GetAttribute("placeholder"), Does.Contain("leave empty to keep"),
                "an empty save keeps the stored secret — the editor says so");
        });
    }

    [Test]
    public void Editor_ProviderWithNoRequiredTools_IsOffered_EvenWhenTheSchemaProvidesNone()
    {
        var core = CoreWithSchema(); // no ProvidedTools at all
        core.ProviderCatalog.Add(Provider("plain-agent"));
        using var ctx = NewContext(core);

        var cut = RenderWithTypePicked(ctx);

        Assert.That(ProviderPickerHtml(cut), Does.Contain("plain-agent"),
            "no declared tool requirement means the provider matches on contract alone");
    }
}
