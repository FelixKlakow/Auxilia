using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.AdminConsole.Support;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The platform-settings surface: the default-resource-access posture reads from the stored
/// setting (unset = restricted) and saving writes the chosen mode through the client.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class AdminSettingsPageTests
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

    [Test]
    public void Settings_UnsetPosture_ReadsAsRestricted()
    {
        var core = new FakeCoreClient();
        using var ctx = NewContext(core);

        var cut = ctx.Render<AdminSettings>();

        var restricted = cut.Find($"input[value='{DefaultResourceAccessModes.Restricted}']");
        Assert.That(restricted.HasAttribute("checked"), Is.True,
            "an unset setting reads as the restricted maximum-security default");
    }

    [Test]
    public void Settings_SaveOpenPosture_WritesTheSetting()
    {
        var core = new FakeCoreClient();
        using var ctx = NewContext(core);
        var cut = ctx.Render<AdminSettings>();

        cut.Find($"input[value='{DefaultResourceAccessModes.Open}']").Change(
            DefaultResourceAccessModes.Open);
        cut.FindAll("button").First(b => b.TextContent.Contains("Save default access")).Click();

        cut.WaitForAssertion(() => Assert.That(
            core.PlatformSettings.SingleOrDefault(
                s => s.Key == PlatformSettingKeys.DefaultResourceAccess)?.Value,
            Is.EqualTo(DefaultResourceAccessModes.Open)));
        Assert.That(cut.Markup, Does.Contain("OPEN"), "the notice reflects the new posture");
    }

    [Test]
    public void Settings_StoredOpenPosture_PreselectsOpen()
    {
        var core = new FakeCoreClient();
        core.PlatformSettings.Add(new PlatformSettingDto(
            PlatformSettingKeys.DefaultResourceAccess, DefaultResourceAccessModes.Open,
            DateTimeOffset.UtcNow));
        using var ctx = NewContext(core);

        var cut = ctx.Render<AdminSettings>();

        Assert.That(cut.Find($"input[value='{DefaultResourceAccessModes.Open}']").HasAttribute("checked"),
            Is.True);
    }
}
