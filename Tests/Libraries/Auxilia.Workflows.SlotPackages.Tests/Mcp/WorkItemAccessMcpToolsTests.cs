using System.IO.Pipes;
using System.Text.Json;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.SlotPackages.Tests.TestDoubles;
using Auxilia.Workflows.TaskSource.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Auxilia.Workflows.SlotPackages.Tests.Mcp;

[TestFixture]
[Category("Integration")]
public class WorkItemAccessMcpToolsTests
{
    private const string SlotName = "slot";

    [Test]
    public async Task ToolNames_BeforeStart_ReturnsEmpty()
    {
        var tools = new WorkItemAccessMcpTools(SlotName, new StubWorkItemAccess());
        Assert.That(tools.ToolNames, Is.Empty);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ToolNames_AfterStart_ContainsBothPrefixedNames()
    {
        var tools = new WorkItemAccessMcpTools(SlotName, new StubWorkItemAccess());
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            Assert.That(tools.ToolNames, Is.EquivalentTo(new[]
            {
                SlotMcpPrefix.Format(SlotName, "get_work_item"),
                SlotMcpPrefix.Format(SlotName, "get_work_items")
            }));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task Http_GetWorkItem_ReturnsFormattedPlainText()
    {
        var tools = new WorkItemAccessMcpTools(SlotName, new StubWorkItemAccess());
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            var endpointUrl = ((HttpMcpTransportConfig)tools.CurrentTransport!).EndpointUrl;
            await using var client = await CreateHttpClientAsync(endpointUrl);

            var result = await client.CallToolAsync(
                SlotMcpPrefix.Format(SlotName, "get_work_item"),
                new Dictionary<string, object?> { ["id"] = "WI-1" },
                null, null, CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.That(text, Does.Contain("Id:"));
            Assert.That(text, Does.Contain("Title:"));
            Assert.That(text, Does.Contain("Status:"));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task Http_GetWorkItem_NotFound_ReturnsNotFoundText()
    {
        var tools = new WorkItemAccessMcpTools(SlotName, new NullStubWorkItemAccess());
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            var endpointUrl = ((HttpMcpTransportConfig)tools.CurrentTransport!).EndpointUrl;
            await using var client = await CreateHttpClientAsync(endpointUrl);

            var result = await client.CallToolAsync(
                SlotMcpPrefix.Format(SlotName, "get_work_item"),
                new Dictionary<string, object?> { ["id"] = "WI-MISSING" },
                null, null, CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.That(text, Is.EqualTo("Not found."));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task Http_GetWorkItems_AcceptsJsonArrayParameter()
    {
        var tools = new WorkItemAccessMcpTools(SlotName, new StubWorkItemAccess());
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            var endpointUrl = ((HttpMcpTransportConfig)tools.CurrentTransport!).EndpointUrl;
            await using var client = await CreateHttpClientAsync(endpointUrl);

            var result = await client.CallToolAsync(
                SlotMcpPrefix.Format(SlotName, "get_work_items"),
                new Dictionary<string, object?> { ["idsJson"] = """["id1","id2"]""" },
                null, null, CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            using var doc = JsonDocument.Parse(text);
            Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
            Assert.That(doc.RootElement.GetArrayLength(), Is.EqualTo(2));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task NamedPipe_GetWorkItems_ReturnsStructuredJsonWithCamelCase()
    {
        var pipeName = Guid.NewGuid().ToString("N");
        var tools = new WorkItemAccessMcpTools(SlotName, new StubWorkItemAccess());
        await tools.StartAsync(new NamedPipeMcpTransportConfig(pipeName, "test"));
        try
        {
            await using var client = await CreatePipeClientAsync(pipeName);

            var result = await client.CallToolAsync(
                SlotMcpPrefix.Format(SlotName, "get_work_items"),
                new Dictionary<string, object?> { ["idsJson"] = """["WI-1"]""" },
                null, null, CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            using var doc = JsonDocument.Parse(text);
            Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
            var first = doc.RootElement[0];
            Assert.That(first.TryGetProperty("assigneeDisplayName", out _), Is.True);
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
