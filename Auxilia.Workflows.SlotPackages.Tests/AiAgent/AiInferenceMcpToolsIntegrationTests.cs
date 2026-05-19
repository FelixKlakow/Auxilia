using System.IO.Pipes;
using Auxilia.Workflows.AiAgent.Mcp;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.SlotPackages.Tests.TestDoubles;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Auxilia.Workflows.SlotPackages.Tests.AiAgent;

[TestFixture]
[Category("Integration")]
public class AiInferenceMcpToolsIntegrationTests
{
    private const string SlotName = "ai-slot";
    private AiInferenceMcpTools _tools = null!;

    [SetUp]
    public void SetUp()
    {
        _tools = new AiInferenceMcpTools(SlotName, new StubAiInference());
    }

    [TearDown]
    public async Task TearDown()
    {
        await _tools.StopAsync();
    }

    [Test]
    public async Task HttpTransport_RunInference_ReturnsPlainTextResponse()
    {
        await _tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test-server"));
        var endpointUrl = ((HttpMcpTransportConfig)_tools.CurrentTransport!).EndpointUrl;

        await using var client = await CreateHttpClientAsync(endpointUrl);

        var result = await client.CallToolAsync(
            SlotMcpPrefix.Format(SlotName, "run_inference"),
            new Dictionary<string, object> { ["prompt"] = "Hello, world!" },
            null, null, CancellationToken.None);

        var text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.That(text, Is.EqualTo(StubAiInference.FixedResponse));
    }

    [Test]
    public async Task NamedPipeTransport_RunInference_ReturnsPlainTextResponse()
    {
        var pipeName = Guid.NewGuid().ToString("N");
        await _tools.StartAsync(new NamedPipeMcpTransportConfig(pipeName, "test-server"));

        await using var client = await CreatePipeClientAsync(pipeName);

        var result = await client.CallToolAsync(
            SlotMcpPrefix.Format(SlotName, "run_inference"),
            new Dictionary<string, object> { ["prompt"] = "Hello, world!" },
            null, null, CancellationToken.None);

        var text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.That(text, Is.EqualTo(StubAiInference.FixedResponse));
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

