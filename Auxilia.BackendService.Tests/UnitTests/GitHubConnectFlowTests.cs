using System.Net;
using Auxilia.BackendService.Dashboard.Connect;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class GitHubConnectFlowTests
{
    /// <summary>Answers the device-code request first, then token polls with the queued bodies.</summary>
    private sealed class ScriptedHandler(params string[] tokenBodies) : HttpMessageHandler
    {
        private int _tokenCalls;

        public List<(string Url, string Body, string? Accept)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), body, request.Headers.Accept.ToString()));

            if (request.RequestUri!.AbsolutePath.Contains("device/code"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"device_code":"dev-123","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""")
                };

            var tokenBody = tokenBodies[Math.Min(_tokenCalls++, tokenBodies.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenBody) };
        }
    }

    private static GitHubConnectFlow Flow(ScriptedHandler handler)
        => new(new GitHubConnectSettings(), new HttpClient(handler));

    [Test]
    public async Task Begin_FetchesTheDeviceCode_AndHandsOutTheUserCode()
    {
        var handler = new ScriptedHandler();
        var flow = Flow(handler);

        var start = await flow.BeginAsync();

        Assert.Multiple(() =>
        {
            Assert.That(start.UserCode, Is.EqualTo("ABCD-1234"));
            Assert.That(start.AuthorizeUrl, Is.EqualTo("https://github.com/login/device"));
            Assert.That(start.PasteRequired, Is.False, "device flow: nothing is pasted back");
            Assert.That(handler.Requests.Single().Body, Does.Contain("client_id=").And.Contain("scope=repo"));
            Assert.That(handler.Requests.Single().Accept, Does.Contain("application/json"));
        });
    }

    [Test]
    public async Task Complete_AfterApproval_ReturnsTheToken()
    {
        var handler = new ScriptedHandler("""{"access_token":"gho_test","token_type":"bearer"}""");
        var flow = Flow(handler);
        var start = await flow.BeginAsync();

        var token = await flow.CompleteAsync(start.State, null);

        Assert.That(token, Is.EqualTo("gho_test"));
        Assert.Multiple(() =>
        {
            Assert.That(handler.Requests[1].Body, Does.Contain("device_code=dev-123"));
            Assert.That(handler.Requests[1].Body,
                Does.Contain(Uri.EscapeDataString("urn:ietf:params:oauth:grant-type:device_code")));
        });
    }

    [Test]
    public async Task Complete_BeforeApproval_IsPending_AndTheAttemptStaysValid()
    {
        var handler = new ScriptedHandler(
            """{"error":"authorization_pending"}""",
            """{"access_token":"gho_test"}""");
        var flow = Flow(handler);
        var start = await flow.BeginAsync();

        Assert.ThrowsAsync<ConnectPendingException>(() => flow.CompleteAsync(start.State, null));

        Assert.That(await flow.CompleteAsync(start.State, null), Is.EqualTo("gho_test"),
            "a pending attempt is finished by simply retrying");
    }

    [Test]
    public async Task Complete_ExpiredOrDenied_IsATerminalError()
    {
        var handler = new ScriptedHandler("""{"error":"expired_token"}""");
        var flow = Flow(handler);
        var start = await flow.BeginAsync();

        var exception = Assert.ThrowsAsync<InvalidOperationException>(
            () => flow.CompleteAsync(start.State, null));

        Assert.That(exception!.Message, Does.Contain("expired"));
    }

    [Test]
    public void Complete_UnknownState_IsRejected()
    {
        var flow = Flow(new ScriptedHandler());

        Assert.ThrowsAsync<InvalidOperationException>(() => flow.CompleteAsync("never-begun", null));
    }
}
