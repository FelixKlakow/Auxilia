using System.Net;
using System.Security.Claims;
using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.AdminConsole.Support;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Auxilia.AdminConsole.Auth;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// Step-up re-authentication is shared, not per-page: the flow itself stashes and retries, and every
/// admin surface that can hit a privileged endpoint raises the prompt instead of printing the Core's
/// raw <c>elevation-required</c> string. The workflow registry used to do exactly that.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class StepUpFlowTests
{
    [Test]
    public async Task RunAsync_StashesTheRejectedAction_AndConfirmRetriesIt()
    {
        var core = new FakeCoreClient { RequireElevation = true };
        var principalId = Guid.NewGuid();
        var flow = new StepUpFlow(core);

        var completed = await flow.RunAsync(() =>
            core.SetPrincipalEnabledAsync(principalId, new SetPrincipalEnabledRequest(false)));

        Assert.Multiple(() =>
        {
            Assert.That(completed, Is.False, "the rejected action reports as not done");
            Assert.That(flow.IsPending, Is.True, "and is stashed for the retry");
            Assert.That(core.EnabledChanges, Is.Empty);
        });

        flow.Secret = "my-own-secret";
        var retried = await flow.ConfirmAsync();

        Assert.Multiple(() =>
        {
            Assert.That(retried, Is.True);
            Assert.That(flow.IsPending, Is.False, "the prompt closes once the retry lands");
            Assert.That(core.LastStepUpSecret, Is.EqualTo("my-own-secret"));
            Assert.That(core.EnabledChanges, Is.EqualTo(new[] { (principalId, false) }),
                "the user never has to repeat the original click");
        });
    }

    [Test]
    public async Task RunAsync_PropagatesEveryOtherFailure()
    {
        var flow = new StepUpFlow(new FakeCoreClient());

        Assert.That(async () => await flow.RunAsync(() =>
                throw new CoreApiException(HttpStatusCode.Forbidden, "run.observe required", "denied")),
            Throws.TypeOf<CoreApiException>(),
            "only 'elevation-required' is the flow's business");
        await Task.CompletedTask;
        Assert.That(flow.IsPending, Is.False);
    }

    [Test]
    public async Task ConfirmAsync_KeepsThePromptOpen_OnAWrongSecret()
    {
        var core = new FakeCoreClient { RequireElevation = true, StepUpError = "bad-credential" };
        var flow = new StepUpFlow(core);
        await flow.RunAsync(() =>
            core.SetPrincipalEnabledAsync(Guid.NewGuid(), new SetPrincipalEnabledRequest(false)));

        flow.Secret = "wrong";
        var confirmed = await flow.ConfirmAsync();

        Assert.Multiple(() =>
        {
            Assert.That(confirmed, Is.False);
            Assert.That(flow.IsPending, Is.True, "the pending action survives a failed re-authentication");
            Assert.That(flow.Error, Is.EqualTo("bad-credential"));
        });
    }

    /// <summary>The registry page shares the flow — an elevation-gated unregister must not leak the raw error.</summary>
    [Test]
    public void WorkflowRegistry_ElevationRequired_RaisesThePromptInsteadOfTheRawError()
    {
        var core = new FakeCoreClient { RequireElevation = true };
        core.WorkflowTypes.Add(new WorkflowTypeDto(
            "code-review", "1.0", "Job", null, [], null, WorkflowTypeStatus.Active));
        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<ICoreClient>(core);
        ctx.Services.AddSingleton(new ViewRendererRegistry([]));
        ctx.Services.AddSingleton(new StepUpFlow(core));
        ctx.Services.AddSingleton(new SharingDirectory(core));
        var state = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
            ConsoleAuthenticationStateProvider.IdentityOf(new CurrentPrincipal(
                Guid.NewGuid(), "Registry Admin", ["Administrator"],
                [PermissionActions.WorkflowTypeManage])))));

        var cut = ctx.Render<AdminWorkflowTypes>(parameters =>
            parameters.AddCascadingValue(state));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Unregister").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Yes, unregister").Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Re-authentication required"),
                "the shared prompt appears on the registry page too");
            Assert.That(cut.Markup, Does.Not.Contain(StepUpFlow.ElevationRequired),
                "the Core's raw error detail is never shown to the operator");
            Assert.That(core.UnregisteredWorkflowTypes, Is.Empty);
        });

        cut.Find("input[type=password]").Input("my-own-secret");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Re-authenticate").Click();

        Assert.That(core.UnregisteredWorkflowTypes, Is.EqualTo(new[] { "code-review" }),
            "the gated unregister runs after the successful step-up");
    }
}
