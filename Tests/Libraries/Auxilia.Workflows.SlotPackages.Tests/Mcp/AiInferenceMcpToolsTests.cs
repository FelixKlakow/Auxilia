using System.IO.Pipes;
using Auxilia.Workflows.AiAgent.Mcp;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.SlotPackages.Tests.TestDoubles;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Auxilia.Workflows.SlotPackages.Tests.Mcp;

[TestFixture]
[Category("Integration")]
public class AiInferenceMcpToolsTests
{
    private const string SlotName = "slot";

    [Test]
    public async Task ToolNames_BeforeStart_ReturnsEmpty()
    {
        var tools = new AiInferenceMcpTools(SlotName, new StubAiInference());
        Assert.That(tools.ToolNames, Is.Empty);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ToolNames_AfterStart_ContainsPrefixedRunInference()
    {
        var tools = new AiInferenceMcpTools(SlotName, new StubAiInference());
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            Assert.That(tools.ToolNames, Does.Contain(SlotMcpPrefix.Format(SlotName, "run_inference")));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task Http_RunInference_ReturnsResponseText()
    {
        var tools = new AiInferenceMcpTools(SlotName, new StubAiInference());
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            var endpointUrl = ((HttpMcpTransportConfig)tools.CurrentTransport!).EndpointUrl;
            await using var client = await CreateHttpClientAsync(endpointUrl);

            var result = await client.CallToolAsync(
                SlotMcpPrefix.Format(SlotName, "run_inference"),
                new Dictionary<string, object?> { ["prompt"] = "Hello!" },
                null, null, CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.That(text, Is.EqualTo(StubAiInference.FixedResponse));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task Http_RunInference_SessionIsDisposedAfterCall()
    {
        var stub = new StubAiInference();
        var tools = new AiInferenceMcpTools(SlotName, stub);
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            var endpointUrl = ((HttpMcpTransportConfig)tools.CurrentTransport!).EndpointUrl;
            await using var client = await CreateHttpClientAsync(endpointUrl);

            await client.CallToolAsync(
                SlotMcpPrefix.Format(SlotName, "run_inference"),
                new Dictionary<string, object?> { ["prompt"] = "Hello!" },
                null, null, CancellationToken.None);

            Assert.That(stub.DisposedCount, Is.EqualTo(1));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task NamedPipe_RunInference_ReturnsResponseText()
    {
        var pipeName = Guid.NewGuid().ToString("N");
        var tools = new AiInferenceMcpTools(SlotName, new StubAiInference());
        await tools.StartAsync(new NamedPipeMcpTransportConfig(pipeName, "test"));
        try
        {
            await using var client = await CreatePipeClientAsync(pipeName);

            var result = await client.CallToolAsync(
                SlotMcpPrefix.Format(SlotName, "run_inference"),
                new Dictionary<string, object?> { ["prompt"] = "Hello!" },
                null, null, CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.That(text, Is.EqualTo(StubAiInference.FixedResponse));
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
