using System.Net;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
/// Authorization matrix for the new dashboard pages: anonymous callers get 401, principals
/// without the required permission get the policy-deny panel, permitted roles see the page.
/// </summary>
[TestFixture]
[Category("Component")]
public class OperatorAreaAuthorizationTests : DashboardComponentTestBase
{
    [TestCase("/operator/slots")]
    [TestCase("/operator/schedules")]
    [TestCase("/operator/artifact-triggers")]
    [TestCase("/admin/bundles")]
    [TestCase("/admin/provider-catalog")]
    [TestCase("/admin/identity-sources")]
    [TestCase("/dashboard")]
    [TestCase("/")]
    public async Task Page_Anonymous_Returns401(string path)
    {
        using var client = CreateClient();

        var response = await client.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [TestCase("/operator/slots", "Registered slot providers")]
    [TestCase("/operator/schedules", "Create schedule")]
    [TestCase("/operator/artifact-triggers", "Create chaining rule")]
    public async Task OperatorPage_AsOperator_RendersContent(string path, string expectedSection)
    {
        using var client = CreateClient();
        var (_, username, password) = await CreatePrincipalAsync("Operator");
        var (cookie, _) = await LoginAsync(client, username, password);

        var html = await GetHtmlAsync(client, path, cookie);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain(expectedSection));
            Assert.That(html, Does.Not.Contain("Access denied"));
        });
    }

    [TestCase("/operator/slots")]
    [TestCase("/operator/schedules")]
    [TestCase("/operator/artifact-triggers")]
    public async Task OperatorPage_AsUser_IsDeniedByPolicy(string path)
    {
        using var client = CreateClient();
        var (_, username, password) = await CreatePrincipalAsync("User");
        var (cookie, _) = await LoginAsync(client, username, password);

        var html = await GetHtmlAsync(client, path, cookie);

        Assert.That(html, Does.Contain("Access denied"));
    }

    [Test]
    public async Task BundlesPage_AsAdministrator_RendersContent()
    {
        using var client = CreateClient();
        var (cookie, _) = await LoginAsync(client, AdminUsername, AdminPassword);

        var html = await GetHtmlAsync(client, "/admin/bundles", cookie);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Create bundle"));
            Assert.That(html, Does.Not.Contain("Access denied"));
        });
    }

    [Test]
    public async Task BundlesPage_AsOperator_IsDeniedByPolicy()
    {
        using var client = CreateClient();
        var (_, username, password) = await CreatePrincipalAsync("Operator");
        var (cookie, _) = await LoginAsync(client, username, password);

        var html = await GetHtmlAsync(client, "/admin/bundles", cookie);

        Assert.That(html, Does.Contain("Access denied"));
    }

    [Test]
    public async Task DashboardPage_AsUser_RendersEmptyDashboard()
    {
        using var client = CreateClient();
        var (_, username, password) = await CreatePrincipalAsync("User");
        var (cookie, _) = await LoginAsync(client, username, password);

        var html = await GetHtmlAsync(client, "/dashboard", cookie);

        Assert.That(html, Does.Contain("Nothing pinned yet"));
    }
}
