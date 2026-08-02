using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Auxilia.Core.Contracts;
using Microsoft.AspNetCore.Hosting;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// The slot-credential resolution endpoint is rate-limited per run (keyed by the resolution token)
/// so a compromised runner cannot spam the Core. This fixture lowers the limit to 3/min.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class SlotResolutionRateLimitTests : CoreApiComponentTestBase
{
    protected override void ConfigureHost(IWebHostBuilder builder)
        => builder.UseSetting("CoreApi:ResolutionRateLimitPermitsPerMinute", "3");

    [Test]
    public async Task ResolveSlot_ExceedingPerRunLimit_Returns429()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        var runId = Guid.NewGuid();
        const string token = "same-run-token";
        var client = CreateAnonymousClient();

        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/runs/{runId}/resolve-slot")
            {
                Content = JsonContent.Create(new ResolveSlotRequest("sc", publicKey))
            };
            request.Headers.Add("X-Resolution-Token", token);
            last = await client.SendAsync(request);
        }

        Assert.That(last!.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests),
            "The 4th resolution request for the same run within the window must be rate-limited.");
    }
}
