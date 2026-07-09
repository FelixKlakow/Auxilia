using System.Net;
using System.Text.Json;
using Auxilia.BackendService.Dashboard.Connect;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class AnthropicClaudeConnectFlowTests
{
    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static AnthropicClaudeConnectFlow Flow(RecordingHandler handler)
        => new(new AnthropicConnectSettings(), new HttpClient(handler));

    [Test]
    public async Task Begin_BuildsThePkceAuthorizeUrl()
    {
        var flow = Flow(new RecordingHandler(HttpStatusCode.OK, "{}"));

        var start = await flow.BeginAsync();

        Assert.Multiple(() =>
        {
            Assert.That(start.AuthorizeUrl, Does.StartWith("https://claude.ai/oauth/authorize?code=true"));
            Assert.That(start.AuthorizeUrl, Does.Contain("client_id="));
            Assert.That(start.AuthorizeUrl, Does.Contain("code_challenge="));
            Assert.That(start.AuthorizeUrl, Does.Contain("code_challenge_method=S256"));
            Assert.That(start.AuthorizeUrl, Does.Contain($"state={Uri.EscapeDataString(start.State)}"));
            Assert.That(start.AuthorizeUrl, Does.Not.Contain(" "), "the URL must be fully escaped");
        });
    }

    [Test]
    public async Task Begin_EveryAttempt_GetsItsOwnStateAndChallenge()
    {
        var flow = Flow(new RecordingHandler(HttpStatusCode.OK, "{}"));

        var first = await flow.BeginAsync();
        var second = await flow.BeginAsync();

        Assert.That(first.State, Is.Not.EqualTo(second.State));
    }

    [Test]
    public async Task Complete_ExchangesTheCode_AndReturnsTheAccountToken()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"access_token":"sk-ant-oat01-test","refresh_token":"rt"}""");
        var flow = Flow(handler);
        var start = await flow.BeginAsync();

        var token = await flow.CompleteAsync(start.State, "the-code");

        Assert.That(token, Is.EqualTo("sk-ant-oat01-test"));
        var request = JsonDocument.Parse(handler.LastBody!).RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(handler.LastRequest!.RequestUri!.ToString(),
                Is.EqualTo("https://console.anthropic.com/v1/oauth/token"));
            Assert.That(request.GetProperty("grant_type").GetString(), Is.EqualTo("authorization_code"));
            Assert.That(request.GetProperty("code").GetString(), Is.EqualTo("the-code"));
            Assert.That(request.GetProperty("state").GetString(), Is.EqualTo(start.State));
            Assert.That(request.GetProperty("code_verifier").GetString(), Is.Not.Empty);
        });
    }

    [Test]
    public async Task Complete_AcceptsTheCallbackPagesCodeHashStateFormat()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"access_token":"sk-ant-oat01-test"}""");
        var flow = Flow(handler);
        var start = await flow.BeginAsync();

        await flow.CompleteAsync(start.State, $"  raw-code#{start.State}  ");

        var request = JsonDocument.Parse(handler.LastBody!).RootElement;
        Assert.That(request.GetProperty("code").GetString(), Is.EqualTo("raw-code"));
    }

    [Test]
    public void Complete_UnknownState_IsRejected()
    {
        var flow = Flow(new RecordingHandler(HttpStatusCode.OK, "{}"));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(
            () => flow.CompleteAsync("never-begun", "code"));

        Assert.That(exception!.Message, Does.Contain("expired"));
    }

    [Test]
    public async Task Complete_SameState_CannotBeUsedTwice()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"access_token":"sk-ant-oat01-test"}""");
        var flow = Flow(handler);
        var start = await flow.BeginAsync();

        Assert.DoesNotThrowAsync(() => flow.CompleteAsync(start.State, "code"));
        Assert.ThrowsAsync<InvalidOperationException>(() => flow.CompleteAsync(start.State, "code"));
    }

    [Test]
    public async Task Complete_FailedExchange_ThrowsWithoutLeakingTheCode()
    {
        var flow = Flow(new RecordingHandler(HttpStatusCode.Unauthorized, "{}"));
        var start = await flow.BeginAsync();

        var exception = Assert.ThrowsAsync<InvalidOperationException>(
            () => flow.CompleteAsync(start.State, "super-secret-code"));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("401"));
            Assert.That(exception.Message, Does.Not.Contain("super-secret-code"));
        });
    }

    [Test]
    public void Registry_ResolvesFlowsByDescriptorKey()
    {
        var flow = Flow(new RecordingHandler(HttpStatusCode.OK, "{}"));
        var registry = new ConnectFlowRegistry([flow]);

        Assert.Multiple(() =>
        {
            Assert.That(registry.Find(AnthropicClaudeConnectFlow.FlowKey), Is.SameAs(flow));
            Assert.That(registry.Find("unknown"), Is.Null);
            Assert.That(registry.Find(null), Is.Null);
        });
    }
}


