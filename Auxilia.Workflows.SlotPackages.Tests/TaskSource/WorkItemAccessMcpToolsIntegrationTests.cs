using System.IO.Pipes;
using System.Text.Json;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.SlotPackages.Tests.TestDoubles;
using Auxilia.Workflows.TaskSource.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Auxilia.Workflows.SlotPackages.Tests.TaskSource;

[TestFixture]
[Category("Integration")]
public class WorkItemAccessMcpToolsIntegrationTests
{
    private const string SlotName = "task-slot";
    private WorkItemAccessMcpTools _tools = null!;

    [SetUp]
    public void SetUp()
    {
        _tools = new WorkItemAccessMcpTools(SlotName, new StubWorkItemAccess());
    }

    [TearDown]
    public async Task TearDown()
    {
        await _tools.StopAsync();
    }

    [Test]
    public async Task HttpTransport_GetWorkItem_ReturnsPlainText()
    {
        await _tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test-server"));
        var endpointUrl = ((HttpMcpTransportConfig)_tools.CurrentTransport!).EndpointUrl;

        await using var client = await CreateHttpClientAsync(endpointUrl);

        var result = await client.CallToolAsync(
            SlotMcpPrefix.Format(SlotName, "get_work_item"),
            new Dictionary<string, object> { ["id"] = "WI-1" },
            null, null, CancellationToken.None);

        var text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.That(text, Does.Contain("WI-1"));
        Assert.That(text, Does.Contain("Stub work item WI-1"));
    }

    [Test]
    public async Task HttpTransport_GetWorkItems_ReturnsJsonArray()
    {
        await _tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test-server"));
        var endpointUrl = ((HttpMcpTransportConfig)_tools.CurrentTransport!).EndpointUrl;

        await using var client = await CreateHttpClientAsync(endpointUrl);

        var result = await client.CallToolAsync(
            SlotMcpPrefix.Format(SlotName, "get_work_items"),
            new Dictionary<string, object> { ["idsJson"] = """["WI-1","WI-2"]""" },
            null, null, CancellationToken.None);

        var text = result.Content.OfType<TextContentBlock>().First().Text;
        using var doc = JsonDocument.Parse(text);
        Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(doc.RootElement.GetArrayLength(), Is.EqualTo(2));
    }

    [Test]
    public async Task NamedPipeTransport_GetWorkItem_ReturnsPlainText()
    {
        var pipeName = Guid.NewGuid().ToString("N");
        await _tools.StartAsync(new NamedPipeMcpTransportConfig(pipeName, "test-server"));

        await using var client = await CreatePipeClientAsync(pipeName);

        var result = await client.CallToolAsync(
            SlotMcpPrefix.Format(SlotName, "get_work_item"),
            new Dictionary<string, object> { ["id"] = "WI-1" },
            null, null, CancellationToken.None);

        var text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.That(text, Does.Contain("WI-1"));
        Assert.That(text, Does.Contain("Stub work item WI-1"));
    }

    [Test]
    public async Task NamedPipeTransport_GetWorkItems_ReturnsJsonArray()
    {
        var pipeName = Guid.NewGuid().ToString("N");
        await _tools.StartAsync(new NamedPipeMcpTransportConfig(pipeName, "test-server"));

        await using var client = await CreatePipeClientAsync(pipeName);

        var result = await client.CallToolAsync(
            SlotMcpPrefix.Format(SlotName, "get_work_items"),
            new Dictionary<string, object> { ["idsJson"] = """["WI-1","WI-2"]""" },
            null, null, CancellationToken.None);

        var text = result.Content.OfType<TextContentBlock>().First().Text;
        using var doc = JsonDocument.Parse(text);
        Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(doc.RootElement.GetArrayLength(), Is.EqualTo(2));
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

