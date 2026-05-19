using System.IO.Pipes;
using System.Text.Json;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.PullRequestAccess.Mcp;
using Auxilia.Workflows.SlotPackages.Tests.TestDoubles;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Auxilia.Workflows.SlotPackages.Tests.Mcp;

[TestFixture]
[Category("Integration")]
public class PullRequestAccessMcpToolsTests
{
    private const string SlotName = "pr-slot";

    [Test]
    public async Task ToolNames_BeforeStart_ReturnsEmpty()
    {
        var tools = new PullRequestAccessMcpTools(SlotName, new StubPullRequestAccess());
        Assert.That(tools.ToolNames, Is.Empty);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ToolNames_AfterStart_ContainsAllFivePrefixedNames()
    {
        var tools = new PullRequestAccessMcpTools(SlotName, new StubPullRequestAccess());
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            Assert.That(tools.ToolNames, Is.EquivalentTo(new[]
            {
                SlotMcpPrefix.Format(SlotName, "get_changed_files"),
                SlotMcpPrefix.Format(SlotName, "get_diff_hunks"),
                SlotMcpPrefix.Format(SlotName, "get_comments"),
                SlotMcpPrefix.Format(SlotName, "get_linked_work_items"),
                SlotMcpPrefix.Format(SlotName, "post_comment")
            }));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task Http_GetDiffHunks_ReturnsStructuredJson()
    {
        var tools = new PullRequestAccessMcpTools(SlotName, new StubPullRequestAccess());
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            var endpointUrl = ((HttpMcpTransportConfig)tools.CurrentTransport!).EndpointUrl;
            await using var client = await CreateHttpClientAsync(endpointUrl);

            var result = await client.CallToolAsync(
                SlotMcpPrefix.Format(SlotName, "get_diff_hunks"),
                new Dictionary<string, object?> { ["filePath"] = "src/Program.cs" },
                null, null, CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            using var doc = JsonDocument.Parse(text);
            Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
            var first = doc.RootElement[0];
            Assert.That(first.TryGetProperty("filePath", out _), Is.True);
            Assert.That(first.TryGetProperty("oldStart", out _), Is.True);
            Assert.That(first.TryGetProperty("newStart", out _), Is.True);
            Assert.That(first.TryGetProperty("content", out _), Is.True);
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task Http_PostComment_ReturnsConfirmationText()
    {
        var tools = new PullRequestAccessMcpTools(SlotName, new StubPullRequestAccess());
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            var endpointUrl = ((HttpMcpTransportConfig)tools.CurrentTransport!).EndpointUrl;
            await using var client = await CreateHttpClientAsync(endpointUrl);

            var result = await client.CallToolAsync(
                SlotMcpPrefix.Format(SlotName, "post_comment"),
                new Dictionary<string, object?> { ["body"] = "LGTM", ["filePath"] = null, ["lineNumber"] = null },
                null, null, CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.That(text, Is.EqualTo("Comment posted."));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task NamedPipe_GetComments_ReturnsStructuredJson()
    {
        var pipeName = Guid.NewGuid().ToString("N");
        var tools = new PullRequestAccessMcpTools(SlotName, new StubPullRequestAccess());
        await tools.StartAsync(new NamedPipeMcpTransportConfig(pipeName, "test"));
        try
        {
            await using var client = await CreatePipeClientAsync(pipeName);

            var result = await client.CallToolAsync(
                SlotMcpPrefix.Format(SlotName, "get_comments"),
                null, null, null, CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            using var doc = JsonDocument.Parse(text);
            Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
            var first = doc.RootElement[0];
            Assert.That(first.TryGetProperty("id", out _), Is.True);
            Assert.That(first.TryGetProperty("body", out _), Is.True);
            Assert.That(first.TryGetProperty("author", out _), Is.True);
            Assert.That(first.TryGetProperty("createdAt", out _), Is.True);
        }
        finally
        {
            await tools.StopAsync();
        }
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
