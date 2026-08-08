using Auxilia.AdminConsole.Auth;
using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.AdminConsole.Support;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The two access surfaces added alongside connector/configuration sharing: who may bind a
/// provider (the same AccessGrant list, edited by the same shared editor), and the workflow-type
/// access list (per-action, role-or-subject, exclusive once populated).
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class AccessSurfaceTests
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

    private static Task<AuthenticationState> AuthWith(params string[] permissions)
        => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
            ConsoleAuthenticationStateProvider.IdentityOf(
                new CurrentPrincipal(Guid.NewGuid(), "Policy Admin", ["Administrator"], permissions)))));

    private static ProviderCatalogEntry Provider(string name, IReadOnlyList<AccessGrant>? grants = null)
        => new(name, true, "SourceControl", [], ["ISourceControlAccess"], $"{name} provider")
        {
            Grants = grants ?? []
        };

    // ---------------------------------------------------------------- provider sharing

    [Test]
    public void ProviderCatalog_ShowsAccessColumn_OpenAndRestricted()
    {
        var core = new FakeCoreClient();
        core.ProviderCatalog.Add(Provider("github"));
        core.ProviderCatalog.Add(Provider("azure-devops",
            [new AccessGrant(AccessGrantKind.Group, Guid.NewGuid().ToString())]));
        using var ctx = NewContext(core);

        var cut = ctx.Render<AdminProviderCatalog>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("everyone"), "an unrestricted provider reads as open");
            Assert.That(cut.Markup, Does.Contain("1 subject"), "a restricted one shows how many admit it");
        });
    }

    [Test]
    public void ProviderCatalog_Sharing_SavesGrants()
    {
        var groupId = Guid.NewGuid();
        var core = new FakeCoreClient
        {
            SharingSubjects = new SharingSubjects([], [new SharingGroup(groupId, "Platform team")])
        };
        core.ProviderCatalog.Add(Provider("github"));
        using var ctx = NewContext(core);
        var cut = ctx.Render<AdminProviderCatalog>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Sharing").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add subject").Click();
        cut.Find(".drawer .grant-row select[aria-label='Subject kind']").Change(AccessGrantKind.Group);
        cut.Find(".drawer .grant-row select[aria-label='Group']").Change(groupId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save sharing").Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.ProviderGrantCalls, Has.Count.EqualTo(1));
            Assert.That(core.ProviderGrantCalls[0].ProviderType, Is.EqualTo("github"));
            Assert.That(core.ProviderGrantCalls[0].Grants[0].Kind, Is.EqualTo(AccessGrantKind.Group));
            Assert.That(core.ProviderGrantCalls[0].Grants[0].Id, Is.EqualTo(groupId.ToString()));
        });
    }

    [Test]
    public void ProviderCatalog_Sharing_ElevationRequired_RaisesThePrompt()
    {
        var core = new FakeCoreClient { RequireElevation = true };
        core.ProviderCatalog.Add(Provider("github"));
        using var ctx = NewContext(core);
        var cut = ctx.Render<AdminProviderCatalog>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Sharing").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add subject").Click();
        cut.Find(".drawer .grant-row input").Change(Guid.NewGuid().ToString());
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save sharing").Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Re-authentication required"));
            Assert.That(cut.Markup, Does.Not.Contain(StepUpFlow.ElevationRequired));
        });
    }

    // ---------------------------------------------------------------- workflow-type access list

    private static IRenderedComponent<AdminWorkflowTypes> RenderRegistry(
        BunitContext ctx, params string[] permissions)
        => ctx.Render<AdminWorkflowTypes>(p => p.AddCascadingValue(AuthWith(permissions)));

    private static FakeCoreClient RegistryWithType()
    {
        var core = new FakeCoreClient();
        core.WorkflowTypes.Add(new WorkflowTypeDto(
            "code-review", "1.0", "Job", null, [], null, WorkflowTypeStatus.Active));
        return core;
    }

    [Test]
    public void Registry_AccessEditor_HiddenWithoutPolicyAdminister()
    {
        using var ctx = NewContext(RegistryWithType());

        var cut = RenderRegistry(ctx, PermissionActions.WorkflowTypeManage);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Details").Click();

        Assert.That(cut.Markup, Does.Not.Contain("Access list"),
            "the access list is a policy surface — managing types does not admit you to it");
    }

    [Test]
    public void Registry_AccessEditor_ListsEntries_WithResolvedSubjectNames()
    {
        var principalId = Guid.NewGuid();
        var core = RegistryWithType();
        core.SharingSubjects = new SharingSubjects(
            [new SharingPrincipal(principalId, "Jane Operator", "Human")], []);
        core.WorkflowTypeAccess.Add(new WorkflowTypeAccessEntryDto(
            PermissionActions.WorkflowTrigger, RoleName: "Operator"));
        core.WorkflowTypeAccess.Add(new WorkflowTypeAccessEntryDto(
            PermissionActions.WorkflowTrigger, PrincipalId: principalId));
        using var ctx = NewContext(core);

        var cut = RenderRegistry(ctx, PermissionActions.PolicyAdminister);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Details").Click();

        cut.WaitForAssertion(() => Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Access list"));
            Assert.That(cut.Markup, Does.Contain("Operator"), "a role entry reads as its role name");
            Assert.That(cut.Markup, Does.Contain("Jane Operator"),
                "a principal entry resolves through the sharing directory instead of showing a GUID");
            Assert.That(cut.Markup, Does.Contain("first"), "the exclusivity rule is explained, not implied");
        }));
    }

    [Test]
    public void Registry_AccessEditor_GrantsARoleEntry()
    {
        var core = RegistryWithType();
        using var ctx = NewContext(core);
        var cut = RenderRegistry(ctx, PermissionActions.PolicyAdminister);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Details").Click();

        cut.WaitForElement(".access-add");
        cut.Find(".access-add select[aria-label='Role']").Change("Operator");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add entry").Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.AccessGrantCalls, Has.Count.EqualTo(1));
            Assert.That(core.AccessGrantCalls[0].Type, Is.EqualTo("code-review"));
            Assert.That(core.AccessGrantCalls[0].Change.Action, Is.EqualTo(PermissionActions.WorkflowTrigger),
                "workflow.trigger is the default action — the common case needs no thought");
            Assert.That(core.AccessGrantCalls[0].Change.RoleName, Is.EqualTo("Operator"));
        });
    }

    [Test]
    public void Registry_AccessEditor_RevokesWithConfirmation()
    {
        var core = RegistryWithType();
        core.WorkflowTypeAccess.Add(new WorkflowTypeAccessEntryDto(
            PermissionActions.WorkflowTrigger, RoleName: "Operator"));
        using var ctx = NewContext(core);
        var cut = RenderRegistry(ctx, PermissionActions.PolicyAdminister);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Details").Click();
        cut.WaitForElement(".access-editor");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Revoke").Click();
        Assert.That(core.AccessRevokeCalls, Is.Empty, "arming must not revoke");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Yes, revoke").Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.AccessRevokeCalls, Has.Count.EqualTo(1));
            Assert.That(core.AccessRevokeCalls[0].Change.RoleName, Is.EqualTo("Operator"));
            Assert.That(core.WorkflowTypeAccess, Is.Empty);
        });
    }

    [Test]
    public void Registry_AccessEditor_EmptyList_SaysRolesDecide()
    {
        using var ctx = NewContext(RegistryWithType());

        var cut = RenderRegistry(ctx, PermissionActions.PolicyAdminister);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Details").Click();

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("role permissions decide"),
            "an empty access list is not the same as a locked-down one"));
    }
}
