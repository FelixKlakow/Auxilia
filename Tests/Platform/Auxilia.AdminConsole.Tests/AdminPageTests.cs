using System.Net;
using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The Principals (Admin) page renders principals from a fake <see cref="ICoreClient"/>, offers revoke
/// only for Direct roles, and creates an AI/service principal — surfacing the one-time API key.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class AdminPageTests
{
    private static BunitContext NewContext(FakeCoreClient core)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ICoreClient>(core);
        ctx.Services.AddSingleton(new ViewRendererRegistry([]));
        return ctx;
    }

    private static PrincipalDto Human(string name, params PrincipalRoleDto[] roles)
        => new(Guid.NewGuid(), "Human", name, "Active", "jane@corp", roles, []);

    [Test]
    public void Admin_DisplaysPrincipals_AndRevokesOnlyDirectRoles()
    {
        var core = new FakeCoreClient();
        core.Principals.Add(Human("Jane Admin",
            new PrincipalRoleDto("Administrator", "Direct"),
            new PrincipalRoleDto("Auditor", "Group")));
        using var ctx = NewContext(core);

        var cut = ctx.Render<Admin>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Jane Admin"));
            Assert.That(cut.Markup, Does.Contain("Administrator"));
            Assert.That(cut.Markup, Does.Contain("Auditor"));
            // Exactly one revoke chip: the Direct role. The Group role is shown but not revocable.
            Assert.That(cut.FindAll(".chip-x"), Has.Count.EqualTo(1), "only Direct roles offer revoke");
        });
    }

    [Test]
    public void Admin_CreateAiPrincipal_ShowsOneTimeApiKey()
    {
        var core = new FakeCoreClient { CreatedApiKeyValue = "auxk_shown_once_123" };
        using var ctx = NewContext(core);
        var cut = ctx.Render<Admin>();

        cut.Find("input[placeholder='e.g. ci-bot']").Change("ci-bot");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Create principal").Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.LastCreatedApiKey, Is.Not.Null);
            Assert.That(core.LastCreatedApiKey!.DisplayName, Is.EqualTo("ci-bot"));
            Assert.That(cut.Markup, Does.Contain("auxk_shown_once_123"), "the one-time API key is shown");
        });
    }

    [Test]
    public void Admin_ElevationRequired_ShowsStepUpPrompt_AndRetriesAfterReAuth()
    {
        var core = new FakeCoreClient { RequireElevation = true };
        var admin = Human("Jane Admin", new PrincipalRoleDto("User", "Direct"));
        core.Principals.Add(admin);
        using var ctx = NewContext(core);
        var cut = ctx.Render<Admin>();

        // Disabling is elevation-gated: the raw error must NOT surface — the prompt appears.
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Disable").Click();
        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Re-authentication required"));
            Assert.That(cut.Markup, Does.Not.Contain("elevation-required"),
                "the raw error detail must be replaced by the prompt");
            Assert.That(core.EnabledChanges, Is.Empty);
        });

        // Re-authenticating retries the pending mutation automatically.
        cut.Find("input[type=password]").Input("my-own-secret");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Re-authenticate").Click();
        Assert.Multiple(() =>
        {
            Assert.That(core.LastStepUpSecret, Is.EqualTo("my-own-secret"));
            Assert.That(core.EnabledChanges, Is.EqualTo(new[] { (admin.Id, false) }),
                "the gated disable runs after the successful step-up");
            Assert.That(cut.Markup, Does.Not.Contain("Re-authentication required"));
        });
    }

    [Test]
    public void Admin_ShowsAccessDenied_OnForbidden()
    {
        var core = new FakeCoreClient
        {
            PrincipalsError = new CoreApiException(HttpStatusCode.Forbidden, "principal.administer required", "denied")
        };
        using var ctx = NewContext(core);

        var cut = ctx.Render<Admin>();

        Assert.That(cut.Markup, Does.Contain("Access denied"));
    }
}
