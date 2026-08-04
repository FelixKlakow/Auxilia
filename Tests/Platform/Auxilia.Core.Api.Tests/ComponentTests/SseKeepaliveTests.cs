using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Component tests for the SSE keepalive: an idle stream emits <c>: ping</c> comment lines so a
/// client can distinguish "nothing happened" from a dead connection. Runs with a 1s keepalive so
/// the test observes pings quickly; also covers the artifact stream (same shared writer).
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class SseKeepaliveTests : CoreApiComponentTestBase
{
    protected override void ConfigureHost(IWebHostBuilder builder)
        => builder.UseSetting("CoreApi:SseKeepaliveSeconds", "1");

    [Test]
    public async Task IdleRunStream_EmitsKeepalivePings()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await CreateClient().GetAsync(
            $"/api/runs/{Guid.NewGuid()}/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await using var body = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(body);
        while (await reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (!line.StartsWith(':'))
                continue;
            Assert.That(line, Does.StartWith(": ping"));
            return;
        }
        Assert.Fail("The idle stream ended without a keepalive ping.");
    }

    [Test]
    public async Task IdleArtifactStream_EmitsKeepalivePings()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await CreateClient().GetAsync(
            "/api/artifacts/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await using var body = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(body);
        while (await reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (line.StartsWith(':'))
                return;
        }
        Assert.Fail("The idle artifact stream ended without a keepalive ping.");
    }
}
