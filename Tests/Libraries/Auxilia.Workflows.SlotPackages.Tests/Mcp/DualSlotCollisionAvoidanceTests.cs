using System.IO.Pipes;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.SlotPackages.Tests.TestDoubles;
using Auxilia.Workflows.SourceControl.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Auxilia.Workflows.SlotPackages.Tests.Mcp;

[TestFixture]
[Category("Integration")]
public class DualSlotCollisionAvoidanceTests
{
    private SourceControlAccessMcpTools _toolsA = null!;
    private SourceControlAccessMcpTools _toolsB = null!;

    [SetUp]
    public void SetUp()
    {
        _toolsA = new SourceControlAccessMcpTools("slot-a", new StubSourceControlAccess());
        _toolsB = new SourceControlAccessMcpTools("slot-b", new StubSourceControlAccess());
    }

    [TearDown]
    public async Task TearDown()
    {
        await _toolsA.StopAsync();
        await _toolsB.StopAsync();
    }

    [Test]
    public async Task TwoInstances_DifferentSlotNames_ToolNamesAreDisjoint()
    {
        await _toolsA.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "server-a"));
        await _toolsB.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "server-b"));

        var namesA = new HashSet<string>(_toolsA.ToolNames);
        var namesB = new HashSet<string>(_toolsB.ToolNames);

        Assert.That(namesA, Is.Not.Empty);
        Assert.That(namesB, Is.Not.Empty);
        Assert.That(namesA.Intersect(namesB), Is.Empty, "Tool name sets must be disjoint.");
    }

    [Test]
    public async Task TwoInstances_DifferentTransports_EachClientSeesOnlyItsOwnTools()
    {
        var pipeName = Guid.NewGuid().ToString("N");
        await _toolsA.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "server-a"));
        await _toolsB.StartAsync(new NamedPipeMcpTransportConfig(pipeName, "server-b"));

        var endpointUrlA = ((HttpMcpTransportConfig)_toolsA.CurrentTransport!).EndpointUrl;
        var transportA = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpointUrlA),
            TransportMode = HttpTransportMode.StreamableHttp
        });
        await using McpClient clientA = await McpClient.CreateAsync(transportA, new McpClientOptions(), null, CancellationToken.None);

        var pipeStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipeStream.ConnectAsync(10_000);
        await using McpClient clientB = await McpClient.CreateAsync(
            new StreamClientTransport(pipeStream, pipeStream, null),
            new McpClientOptions(), null, CancellationToken.None);

        var toolsFromA = await clientA.ListToolsAsync((RequestOptions?)null, CancellationToken.None);
        var toolsFromB = await clientB.ListToolsAsync((RequestOptions?)null, CancellationToken.None);

        Assert.That(toolsFromA.Select(t => t.Name), Has.All.StartWith("slot-a."));
        Assert.That(toolsFromB.Select(t => t.Name), Has.All.StartWith("slot-b."));
        Assert.That(toolsFromA.Select(t => t.Name).Intersect(toolsFromB.Select(t => t.Name)), Is.Empty);
    }
}



