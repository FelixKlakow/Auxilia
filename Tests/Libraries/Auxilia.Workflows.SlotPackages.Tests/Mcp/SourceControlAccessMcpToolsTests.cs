using System.IO.Pipes;
using System.Text.Json;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.SlotPackages.Tests.TestDoubles;
using Auxilia.Workflows.SourceControl.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Auxilia.Workflows.SlotPackages.Tests.Mcp;

[TestFixture]
[Category("Integration")]
public class SourceControlAccessMcpToolsTests
{
    private const string SlotName = "s";

    [Test]
    public async Task ToolNames_BeforeStart_ReturnsEmpty()
    {
        var tools = new SourceControlAccessMcpTools(SlotName, new StubSourceControlAccess());
        Assert.That(tools.ToolNames, Is.Empty);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ToolNames_AfterStart_ContainsAllThreePrefixedNames()
    {
        var tools = new SourceControlAccessMcpTools(SlotName, new StubSourceControlAccess());
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            Assert.That(tools.ToolNames, Is.EquivalentTo(new[]
            {
                SlotMcpPrefix.Format(SlotName, "list_files"),
                SlotMcpPrefix.Format(SlotName, "read_file"),
                SlotMcpPrefix.Format(SlotName, "get_changed_files")
            }));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task Http_ListTools_ReturnsExpectedPrefixedNames()
    {
        var tools = new SourceControlAccessMcpTools(SlotName, new StubSourceControlAccess());
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            var endpointUrl = ((HttpMcpTransportConfig)tools.CurrentTransport!).EndpointUrl;
            await using var client = await CreateHttpClientAsync(endpointUrl);

            var listed = await client.ListToolsAsync((RequestOptions?)null, CancellationToken.None);

            Assert.That(listed.Select(t => t.Name), Is.EquivalentTo(tools.ToolNames));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task Http_ListFiles_ReturnsNewlineSeparatedPaths()
    {
        var tools = new SourceControlAccessMcpTools(SlotName, new StubSourceControlAccess());
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            var endpointUrl = ((HttpMcpTransportConfig)tools.CurrentTransport!).EndpointUrl;
            await using var client = await CreateHttpClientAsync(endpointUrl);

            var result = await client.CallToolAsync(
                SlotMcpPrefix.Format(SlotName, "list_files"),
                new Dictionary<string, object?> { ["relativePath"] = null },
                null, null, CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.That(text, Does.Contain("src/Program.cs"));
            Assert.That(text, Does.Contain("README.md"));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task Http_GetChangedFiles_ReturnsJsonArray()
    {
        var tools = new SourceControlAccessMcpTools(SlotName, new StubSourceControlAccess());
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            var endpointUrl = ((HttpMcpTransportConfig)tools.CurrentTransport!).EndpointUrl;
            await using var client = await CreateHttpClientAsync(endpointUrl);

            var result = await client.CallToolAsync(
                SlotMcpPrefix.Format(SlotName, "get_changed_files"),
                new Dictionary<string, object?> { ["baseRef"] = "main", ["headRef"] = "HEAD" },
                null, null, CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            using var doc = JsonDocument.Parse(text);
            Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
            var first = doc.RootElement[0];
            Assert.That(first.TryGetProperty("relativePath", out _), Is.True);
            Assert.That(first.TryGetProperty("kind", out _), Is.True);
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task NamedPipe_ListTools_ReturnsExpectedPrefixedNames()
    {
        var pipeName = Guid.NewGuid().ToString("N");
        var tools = new SourceControlAccessMcpTools(SlotName, new StubSourceControlAccess());
        await tools.StartAsync(new NamedPipeMcpTransportConfig(pipeName, "test"));
        try
        {
            await using var client = await CreatePipeClientAsync(pipeName);

            var listed = await client.ListToolsAsync((RequestOptions?)null, CancellationToken.None);

            Assert.That(listed.Select(t => t.Name), Is.EquivalentTo(tools.ToolNames));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task NamedPipe_ReadFile_ReturnsPlainTextContent()
    {
        var pipeName = Guid.NewGuid().ToString("N");
        var tools = new SourceControlAccessMcpTools(SlotName, new StubSourceControlAccess());
        await tools.StartAsync(new NamedPipeMcpTransportConfig(pipeName, "test"));
        try
        {
            await using var client = await CreatePipeClientAsync(pipeName);

            var result = await client.CallToolAsync(
                SlotMcpPrefix.Format(SlotName, "read_file"),
                new Dictionary<string, object?> { ["relativePath"] = "src/Program.cs" },
                null, null, CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.That(text, Does.Contain("stub content"));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task TwoInstances_DifferentSlotNames_ToolNamesAreDisjoint()
    {
        var toolsA = new SourceControlAccessMcpTools("a", new StubSourceControlAccess());
        await toolsA.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "server-a"));
        var toolsB = new SourceControlAccessMcpTools("b", new StubSourceControlAccess());
        await toolsB.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "server-b"));
        try
        {
            Assert.That(toolsA.ToolNames.Intersect(toolsB.ToolNames), Is.Empty);
        }
        finally
        {
            await toolsA.StopAsync();
            await toolsB.StopAsync();
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
