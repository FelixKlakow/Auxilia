using System.Net;
using System.Security.Claims;
using Auxilia.AdminConsole.Auth;
using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.AdminConsole.Support;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The Workflow types (registry) page lists registered types from a fake <see cref="ICoreClient"/> and
/// gates every mutation on the caller's Core-computed permissions: approve/deny need
/// <c>workflow-type.sign</c>, register/enable-disable/unregister need <c>workflow-type.manage</c>.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class AdminWorkflowTypesPageTests
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
                new CurrentPrincipal(Guid.NewGuid(), "Registry Admin", ["Administrator"], permissions)))));

    private static IRenderedComponent<AdminWorkflowTypes> Render(
        BunitContext ctx, Task<AuthenticationState>? auth = null)
        => auth is null
            ? ctx.Render<AdminWorkflowTypes>()
            : ctx.Render<AdminWorkflowTypes>(ps => ps.AddCascadingValue(auth));

    private static WorkflowTypeDto Type(string name, string status, params string[] tags)
        => new(name, "1.2.0", "Job", $"{name} description", tags, "https://packages/x.workflow.zip", status);

    [Test]
    public void ListsTypes_WithStatusAndTags_AndHidesActionsWithoutPermissions()
    {
        var core = new FakeCoreClient();
        core.WorkflowTypes.Add(Type("code-review", WorkflowTypeStatus.Active, "ai", "service"));
        core.WorkflowTypes.Add(Type("nightly-report", WorkflowTypeStatus.Pending));
        using var ctx = NewContext(core);

        // No cascading auth state: the caller holds neither manage nor sign — read-only table.
        var cut = Render(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("code-review"));
            Assert.That(cut.Markup, Does.Contain("nightly-report"));
            Assert.That(cut.Markup, Does.Contain("Pending"));
            Assert.That(cut.Markup, Does.Contain("ai"));
            var buttons = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();
            Assert.That(buttons, Has.None.EqualTo("Approve"));
            Assert.That(buttons, Has.None.EqualTo("Unregister"));
            Assert.That(buttons, Has.None.EqualTo("Register"), "register card needs workflow-type.manage");
        });
    }

    [Test]
    public void Signer_ApprovesPendingType_ButCannotUnregisterOrRegister()
    {
        var core = new FakeCoreClient();
        core.WorkflowTypes.Add(Type("nightly-report", WorkflowTypeStatus.Pending));
        using var ctx = NewContext(core);
        var cut = Render(ctx, AuthWith(PermissionActions.WorkflowTypeSign));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Approve").Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.ApprovedWorkflowTypes, Is.EqualTo(new[] { "nightly-report" }));
            Assert.That(cut.Markup, Does.Contain("approved"));
            // Signing authority alone gets no manage actions.
            var buttons = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();
            Assert.That(buttons, Has.None.EqualTo("Unregister"));
            Assert.That(buttons, Has.None.EqualTo("Register"));
        });
    }

    [Test]
    public void Signer_DenyRequiresReason_AndRecordsIt()
    {
        var core = new FakeCoreClient();
        core.WorkflowTypes.Add(Type("nightly-report", WorkflowTypeStatus.Pending));
        using var ctx = NewContext(core);
        var cut = Render(ctx, AuthWith(PermissionActions.WorkflowTypeSign));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Deny…").Click();
        var denyButton = cut.FindAll("button").First(b => b.TextContent.Trim() == "Deny");
        Assert.That(denyButton.HasAttribute("disabled"), Is.True, "deny needs a reason first");

        cut.Find("input[placeholder='denial reason']").Input("unsigned vendor package");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Deny").Click();

        Assert.That(core.DeniedWorkflowTypes,
            Is.EqualTo(new[] { ("nightly-report", "unsigned vendor package") }));
    }

    [Test]
    public void Manager_DisablesAndUnregisters_WithConfirm()
    {
        var core = new FakeCoreClient();
        core.WorkflowTypes.Add(Type("code-review", WorkflowTypeStatus.Active));
        using var ctx = NewContext(core);
        var cut = Render(ctx, AuthWith(PermissionActions.WorkflowTypeManage));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Disable").Click();
        Assert.Multiple(() =>
        {
            Assert.That(core.WorkflowTypeEnabledChanges, Is.EqualTo(new[] { ("code-review", false) }));
            Assert.That(cut.Markup, Does.Contain("Enable"), "a disabled type offers re-enable");
            // Approve/deny stay hidden: manage alone is not the signing authority.
            Assert.That(cut.FindAll("button").Select(b => b.TextContent.Trim()), Has.None.EqualTo("Approve"));
        });

        // Unregister asks for confirmation before calling the Core.
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Unregister").Click();
        Assert.That(core.UnregisteredWorkflowTypes, Is.Empty, "first click only arms the confirm");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Yes, unregister").Click();
        Assert.Multiple(() =>
        {
            Assert.That(core.UnregisteredWorkflowTypes, Is.EqualTo(new[] { "code-review" }));
            Assert.That(cut.Markup, Does.Contain("No workflow types match"));
        });
    }

    [Test]
    public void Manager_RegistersType_AndSurfacesPendingOutcome()
    {
        var core = new FakeCoreClient { RegisteredWorkflowTypeStatus = WorkflowTypeStatus.Pending };
        using var ctx = NewContext(core);
        var cut = Render(ctx, AuthWith(PermissionActions.WorkflowTypeManage));

        cut.Find("input[placeholder='e.g. code-review']").Change("release-notes");
        cut.Find("input[placeholder^='https://']").Change("docker://acme/release-notes:1");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Register").Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.LastRegisteredWorkflowType, Is.Not.Null);
            Assert.That(core.LastRegisteredWorkflowType!.WorkflowType, Is.EqualTo("release-notes"));
            Assert.That(core.LastRegisteredWorkflowType.PackageUri, Is.EqualTo("docker://acme/release-notes:1"));
            Assert.That(cut.Markup, Does.Contain("awaiting the signing authority"));
            Assert.That(cut.Markup, Does.Contain("release-notes"), "the new type shows in the reloaded list");
        });
    }

    [Test]
    public void Details_OfAPodType_RenderTheSpawnSummaryProminently()
    {
        var core = new FakeCoreClient();
        core.WorkflowTypes.Add(Type("pod-scenario", WorkflowTypeStatus.Pending));
        core.WorkflowSchemas["pod-scenario"] = new WorkflowSchemaDto(
            "pod-scenario", "1.0.0", "1", "Job", [], [], [], [], [], [], null, "{}",
            Status: WorkflowTypeStatus.Pending)
        {
            Companions =
            [
                new WorkflowCompanionDto("rabbit", "rabbitmq@sha256:aaaa", 1, 1, null, []),
                new WorkflowCompanionDto("machine", "sim@sha256:bbbb", 0, 8, "machines", ["rabbit"], 512, 1.5)
            ],
            PodControl = new WorkflowPodControlDto(2, "Simulated machines", ["logs"]),
            MaxPodContainers = 11
        };
        using var ctx = NewContext(core);
        var cut = Render(ctx, AuthWith(PermissionActions.WorkflowTypeSign, PermissionActions.WorkflowTypeManage));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Details").Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Spawn summary"),
                "the approval-relevant pod topology must be called out");
            Assert.That(cut.Markup, Does.Contain("up to 11 pod container(s)"));
            Assert.That(cut.Markup, Does.Contain("runtime-spawn envelope of 2"));
            Assert.That(cut.Markup, Does.Contain("Declared companions"));
            Assert.That(cut.Markup, Does.Contain("0–8"), "the scale bounds show per companion");
            Assert.That(cut.Markup, Does.Contain("512 MB · 1.5 CPU"));
            Assert.That(cut.Markup, Does.Contain("Simulated machines"), "the envelope's declared purpose shows");
            Assert.That(cut.Markup, Does.Contain("logs"), "the shared pod volumes show");
        });
    }

    [Test]
    public void Details_OfAPodlessType_ShowNoSpawnSummary()
    {
        var core = new FakeCoreClient();
        core.WorkflowTypes.Add(Type("plain", WorkflowTypeStatus.Active));
        core.WorkflowSchemas["plain"] = new WorkflowSchemaDto(
            "plain", "1.0.0", "1", "Job", [], [], [], [], [], [], null, "{}");
        using var ctx = NewContext(core);
        var cut = Render(ctx, AuthWith(PermissionActions.WorkflowTypeManage));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Details").Click();

        Assert.That(cut.Markup, Does.Not.Contain("Spawn summary"));
    }

    [Test]
    public void Details_WithoutAReadableSchema_WarnTheApproverOfTheBlindSpot()
    {
        var core = new FakeCoreClient();
        core.WorkflowTypes.Add(Type("mystery", WorkflowTypeStatus.Pending));
        // No WorkflowSchemas entry: a docker:// type registered without a declared schema.
        using var ctx = NewContext(core);
        var cut = Render(ctx, AuthWith(PermissionActions.WorkflowTypeSign, PermissionActions.WorkflowTypeManage));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Details").Click();

        Assert.That(cut.Markup, Does.Contain("No declared schema"),
            "approving a schemaless type is a blind trust decision — the page must say so");
    }

    [Test]
    public void ShowsAccessDenied_OnForbidden()
    {
        var core = new FakeCoreClient
        {
            WorkflowTypesError = new CoreApiException(
                HttpStatusCode.Forbidden, "workflow-configuration.manage required", "denied")
        };
        using var ctx = NewContext(core);

        var cut = Render(ctx);

        Assert.That(cut.Markup, Does.Contain("Access denied"));
    }
}
