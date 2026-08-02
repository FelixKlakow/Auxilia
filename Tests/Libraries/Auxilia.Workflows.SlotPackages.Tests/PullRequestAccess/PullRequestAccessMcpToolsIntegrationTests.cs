using System.IO.Pipes;
using System.Text.Json;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.PullRequestAccess.Mcp;
using Auxilia.Workflows.SlotPackages.Tests.TestDoubles;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Auxilia.Workflows.SlotPackages.Tests.PullRequestAccess;

[TestFixture]
[Category("Integration")]
public class PullRequestAccessMcpToolsIntegrationTests
{
    private const string SlotName = "pr-slot";
    private PullRequestAccessMcpTools _tools = null!;

    [SetUp]
    public void SetUp()
    {
        _tools = new PullRequestAccessMcpTools(SlotName, new StubPullRequestAccess());
    }

    [TearDown]
    public async Task TearDown()
    {
        await _tools.StopAsync();
    }

    [Test]
    public async Task HttpTransport_ToolNames_ArePrefixed()
    {
        await _tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test-server"));

        Assert.That(_tools.ToolNames, Is.Not.Empty);
        Assert.That(_tools.ToolNames, Has.All.StartWith(SlotName + "."));
    }

    [Test]
    public async Task HttpTransport_GetChangedFiles_ReturnsJsonArray()
    {
        await _tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test-server"));
        var endpointUrl = ((HttpMcpTransportConfig)_tools.CurrentTransport!).EndpointUrl;

        await using var client = await CreateHttpClientAsync(endpointUrl);

        var result = await client.CallToolAsync(
            SlotMcpPrefix.Format(SlotName, "get_changed_files"),
            null, null, null, CancellationToken.None);

        var text = result.Content.OfType<TextContentBlock>().First().Text;
        using var doc = JsonDocument.Parse(text);
        Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(doc.RootElement.GetArrayLength(), Is.GreaterThan(0));
    }

    [Test]
    public async Task HttpTransport_PostComment_ReturnsPlainTextConfirmation()
    {
        await _tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test-server"));
        var endpointUrl = ((HttpMcpTransportConfig)_tools.CurrentTransport!).EndpointUrl;

        await using var client = await CreateHttpClientAsync(endpointUrl);

        var result = await client.CallToolAsync(
            SlotMcpPrefix.Format(SlotName, "post_comment"),
            new Dictionary<string, object?> { ["body"] = "LGTM", ["filePath"] = null, ["lineNumber"] = null },
            null, null, CancellationToken.None);

        var text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.That(text, Is.EqualTo("Comment posted."));
    }

    [Test]
    public async Task NamedPipeTransport_ToolNames_ArePrefixed()
    {
        var pipeName = Guid.NewGuid().ToString("N");
        await _tools.StartAsync(new NamedPipeMcpTransportConfig(pipeName, "test-server"));

        Assert.That(_tools.ToolNames, Is.Not.Empty);
        Assert.That(_tools.ToolNames, Has.All.StartWith(SlotName + "."));
    }

    [Test]
    public async Task NamedPipeTransport_GetChangedFiles_ReturnsJsonArray()
    {
        var pipeName = Guid.NewGuid().ToString("N");
        await _tools.StartAsync(new NamedPipeMcpTransportConfig(pipeName, "test-server"));

        await using var client = await CreatePipeClientAsync(pipeName);

        var result = await client.CallToolAsync(
            SlotMcpPrefix.Format(SlotName, "get_changed_files"),
            null, null, null, CancellationToken.None);

        var text = result.Content.OfType<TextContentBlock>().First().Text;
        using var doc = JsonDocument.Parse(text);
        Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(doc.RootElement.GetArrayLength(), Is.GreaterThan(0));
    }

    [Test]
    public async Task NamedPipeTransport_PostComment_ReturnsPlainTextConfirmation()
    {
        var pipeName = Guid.NewGuid().ToString("N");
        await _tools.StartAsync(new NamedPipeMcpTransportConfig(pipeName, "test-server"));

        await using var client = await CreatePipeClientAsync(pipeName);

        var result = await client.CallToolAsync(
            SlotMcpPrefix.Format(SlotName, "post_comment"),
            new Dictionary<string, object?> { ["body"] = "LGTM", ["filePath"] = null, ["lineNumber"] = null },
            null, null, CancellationToken.None);

        var text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.That(text, Is.EqualTo("Comment posted."));
    }

    private static async Task<McpClient> CreateHttpClientAsync(string endpointUrl)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpointUrl),
            TransportMode = HttpTransportMode.StreamableHttp
        });
        return await McpClient.CreateAsync(transport, new McpClientOptions(), null, CancellationToken.None);
    }

    private static async Task<McpClient> CreatePipeClientAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000);
        var transport = new StreamClientTransport(pipe, pipe, null);
        return await McpClient.CreateAsync(transport, new McpClientOptions(), null, CancellationToken.None);
    }
}


