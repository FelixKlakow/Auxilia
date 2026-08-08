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
/// The rehomed Audit page renders against a fake <see cref="ICoreClient"/> (no Core, no network):
/// it queries <c>QueryAuditAsync</c>, displays entries, applies the actor/action/subject filter, and
/// surfaces a Core authorization failure as an access-denied panel.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class AuditPageTests
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

    private static AuditEntry Entry(string actor, string action, string subject, DateTimeOffset at)
        => new(Guid.NewGuid(), at, actor, action, subject, "allowed", null);

    [Test]
    public void Audit_DisplaysEntriesFromCore()
    {
        var core = new FakeCoreClient();
        core.AuditEntries.Add(Entry("svc-scheduler", "workflow.rerun", "run-1", DateTimeOffset.UtcNow));
        core.AuditEntries.Add(Entry("admin", "policy.allowed", "audit-log", DateTimeOffset.UtcNow.AddMinutes(-1)));
        using var ctx = NewContext(core);

        var cut = ctx.Render<Audit>();

        Assert.Multiple(() =>
        {
            Assert.That(core.QueryAuditCallCount, Is.GreaterThanOrEqualTo(1), "the page queries the Core audit log");
            Assert.That(cut.Markup, Does.Contain("workflow.rerun"));
            Assert.That(cut.Markup, Does.Contain("policy.allowed"));
            Assert.That(cut.Markup, Does.Contain("type=\"date\""), "the from/to date filter renders");
        });
    }

    [Test]
    public void Audit_AppliesActionFilter()
    {
        var core = new FakeCoreClient();
        core.AuditEntries.Add(Entry("svc-scheduler", "workflow.rerun", "run-1", DateTimeOffset.UtcNow));
        core.AuditEntries.Add(Entry("admin", "policy.allowed", "audit-log", DateTimeOffset.UtcNow.AddMinutes(-1)));
        using var ctx = NewContext(core);
        var cut = ctx.Render<Audit>();

        cut.Find("input[placeholder='e.g. policy.allowed']").Input("workflow.rerun");
        cut.Find(".audit-toolbar button").Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.LastAuditQuery!.Action, Is.EqualTo("workflow.rerun"), "the filter maps to AuditQuery.Action");
            // Assert on the unique subjects (not "policy.allowed", which is also an input placeholder).
            Assert.That(cut.Markup, Does.Contain("run-1"), "the matching entry stays");
            Assert.That(cut.Markup, Does.Not.Contain("audit-log"), "the non-matching entry is filtered out");
        });
    }

    [Test]
    public void Audit_ResolvesGuidActors_ToPrincipalDisplayNames()
    {
        var principalId = Guid.NewGuid();
        var core = new FakeCoreClient();
        core.Principals.Add(new PrincipalDto(principalId, "Human", "Ada Lovelace", "Active", null, [], []));
        core.AuditEntries.Add(Entry(principalId.ToString(), "connector.created", "github-bot", DateTimeOffset.UtcNow));
        core.AuditEntries.Add(Entry(Guid.NewGuid().ToString(), "policy.allowed", "audit-log", DateTimeOffset.UtcNow.AddMinutes(-1)));
        using var ctx = NewContext(core);

        var cut = ctx.Render<Audit>();

        cut.WaitForAssertion(() => Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Ada Lovelace"), "a known principal renders its display name");
            Assert.That(cut.Markup, Does.Contain($"title=\"{principalId}\""), "the raw id stays reachable via the tooltip");
            Assert.That(cut.Markup, Does.Contain("policy.allowed"), "an unknown-principal entry still renders (raw id)");
        }));
    }

    [Test]
    public void Audit_ShowsAccessDenied_OnForbidden()
    {
        var core = new FakeCoreClient
        {
            AuditError = new CoreApiException(HttpStatusCode.Forbidden, "audit.read required", "Core API request failed")
        };
        using var ctx = NewContext(core);

        var cut = ctx.Render<Audit>();

        Assert.That(cut.Markup, Does.Contain("Access denied"), "a 403 renders the denied panel");
    }
}
