using System.IO.Pipes;
using System.Text.Json;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.SlotPackages.Tests.TestDoubles;
using Auxilia.Workflows.SourceControl.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Auxilia.Workflows.SlotPackages.Tests.SourceControl;

[TestFixture]
[Category("Integration")]
public class SourceControlAccessMcpToolsIntegrationTests
{
    private const string SlotName = "test-slot";
    private SourceControlAccessMcpTools _tools = null!;

    [SetUp]
    public void SetUp()
    {
        _tools = new SourceControlAccessMcpTools(SlotName, new StubSourceControlAccess());
    }

    [TearDown]
    public async Task TearDown()
    {
        await _tools.StopAsync();
    }

    [Test]
    public async Task UnstartedInstance_ToolNames_IsEmpty()
    {
        Assert.That(_tools.ToolNames, Is.Empty);
        await Task.CompletedTask;
    }

    [Test]
    public async Task HttpTransport_ToolNames_ArePrefixed()
    {
        await _tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test-server"));

        Assert.That(_tools.ToolNames, Is.Not.Empty);
        Assert.That(_tools.ToolNames, Has.All.StartWith(SlotName + "."));
    }

    [Test]
    public async Task HttpTransport_ListFiles_ReturnsPlainTextPaths()
    {
        await _tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test-server"));
        var endpointUrl = ((HttpMcpTransportConfig)_tools.CurrentTransport!).EndpointUrl;

        await using var client = await CreateHttpClientAsync(endpointUrl);

        var result = await client.CallToolAsync(
            SlotMcpPrefix.Format(SlotName, "list_files"),
            new Dictionary<string, object?> { ["relativePath"] = null },
            null, null, CancellationToken.None);

        var text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.That(text, Does.Contain("src/Program.cs"));
        Assert.That(text, Does.Contain("README.md"));
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
    public async Task NamedPipeTransport_ListFiles_ReturnsPlainTextPaths()
    {
        var pipeName = Guid.NewGuid().ToString("N");
        await _tools.StartAsync(new NamedPipeMcpTransportConfig(pipeName, "test-server"));

        await using var client = await CreatePipeClientAsync(pipeName);

        var result = await client.CallToolAsync(
            SlotMcpPrefix.Format(SlotName, "list_files"),
            new Dictionary<string, object?> { ["relativePath"] = null },
            null, null, CancellationToken.None);

        var text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.That(text, Does.Contain("src/Program.cs"));
        Assert.That(text, Does.Contain("README.md"));
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


