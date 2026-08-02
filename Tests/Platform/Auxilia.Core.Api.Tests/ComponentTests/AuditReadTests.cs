using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// The Core's audit-read surface: <c>GET /api/audit</c> reads the centralized audit store with typed
/// filters + paging, gated by the <c>audit.read</c> permission (Administrator/Auditor). Verifies the
/// query shape end-to-end and the authorization gate.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class AuditReadTests : CoreApiComponentTestBase
{
    private static readonly DateTimeOffset Base = new(2026, 07, 26, 12, 0, 0, TimeSpan.Zero);

    private async Task SeedAsync(string actor, string action, string subject, DateTimeOffset when)
    {
        var store = Factory.Services.GetRequiredService<IDataAccess<AuditRecord>>();
        await store.SaveAsync(new AuditRecord
        {
            TimestampUtc = when, Actor = actor, Action = action, Subject = subject, Outcome = "ok"
        }, CancellationToken.None);
    }

    private async Task<HttpClient> ClientForRolesAsync(params string[] roles)
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync($"svc-{Guid.NewGuid():N}");
        foreach (var role in roles.Where(BuiltInRoles.Exists))
            await directory.AssignRoleAsync(principal.Id, role);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    [Test]
    public async Task Query_ReturnsNewestFirst_WithFiltersAndPaging()
    {
        // A unique action tag isolates this test's rows from any bootstrap-seeded audit entries.
        var tag = $"test.{Guid.NewGuid():N}";
        await SeedAsync("alice", tag, "wt-1", Base);
        await SeedAsync("bob", tag, "wt-1", Base.AddMinutes(10));
        await SeedAsync("alice", tag, "wt-2", Base.AddMinutes(20));

        var admin = CreateClient();

        // Filter by action: newest first, total reported.
        var byAction = await admin.GetFromJsonAsync<PagedResult<AuditEntry>>($"/api/audit?action={tag}");
        Assert.That(byAction!.Total, Is.EqualTo(3));
        Assert.That(byAction.Items[0].Actor, Is.EqualTo("alice"));
        Assert.That(byAction.Items[0].Subject, Is.EqualTo("wt-2"));
        Assert.That(byAction.Items.Select(i => i.Subject), Is.EqualTo(new[] { "wt-2", "wt-1", "wt-1" }));

        // Filter by actor (+ action tag): only alice's two rows.
        var byActor = await admin.GetFromJsonAsync<PagedResult<AuditEntry>>($"/api/audit?action={tag}&actor=alice");
        Assert.That(byActor!.Total, Is.EqualTo(2));
        Assert.That(byActor.Items.Select(i => i.Subject), Is.EquivalentTo(new[] { "wt-1", "wt-2" }));

        // Time range: to is exclusive, so the row at Base+20 is dropped.
        var early = await admin.GetFromJsonAsync<PagedResult<AuditEntry>>(
            $"/api/audit?action={tag}&fromUtc={Uri.EscapeDataString(Base.ToString("O"))}" +
            $"&toUtc={Uri.EscapeDataString(Base.AddMinutes(20).ToString("O"))}");
        Assert.That(early!.Total, Is.EqualTo(2));
        Assert.That(early.Items.Select(i => i.Actor), Is.EquivalentTo(new[] { "alice", "bob" }));

        // Paging preserves total.
        var page = await admin.GetFromJsonAsync<PagedResult<AuditEntry>>($"/api/audit?action={tag}&skip=1&take=1");
        Assert.That(page!.Total, Is.EqualTo(3));
        Assert.That(page.Items, Has.Count.EqualTo(1));
        Assert.That(page.Items.Single().Actor, Is.EqualTo("bob"));
    }

    [TestCase(BuiltInRoles.Administrator, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.Auditor, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.Operator, HttpStatusCode.Forbidden)]
    [TestCase(BuiltInRoles.User, HttpStatusCode.Forbidden)]
    public async Task Query_RequiresAuditReadPermission(string role, HttpStatusCode expected)
    {
        var client = await ClientForRolesAsync(role);
        var response = await client.GetAsync("/api/audit");
        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }

    [Test]
    public async Task Query_Anonymous_IsUnauthorized()
        => Assert.That((await CreateAnonymousClient().GetAsync("/api/audit")).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));
}
