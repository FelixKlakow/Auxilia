using System.Net;
using System.Net.Http.Json;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Auth-gate tests: REST and MCP both require an authenticated principal; health is anonymous.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class AuthTests : CoreApiComponentTestBase
{
    [Test]
    public async Task Rest_Unauthenticated_Returns401()
    {
        var response = await CreateAnonymousClient().GetAsync("/api/runs");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Rest_Authenticated_Succeeds()
    {
        var response = await CreateClient().GetAsync("/api/runs");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Mcp_Unauthenticated_IsRejected()
    {
        var response = await CreateAnonymousClient().PostAsJsonAsync("/mcp",
            new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Health_IsAnonymous()
    {
        var response = await CreateAnonymousClient().GetAsync("/health");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}
