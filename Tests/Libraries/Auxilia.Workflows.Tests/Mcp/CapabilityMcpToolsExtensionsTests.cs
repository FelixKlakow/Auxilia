using Auxilia.AI;
using Auxilia.Workflows.Mcp;
using Moq;

namespace Auxilia.Workflows.Tests.Mcp;

[TestFixture]
[Category("Unit")]
public class CapabilityMcpToolsExtensionsTests
{
    private const string TestUrl = "http://localhost:5100";
    private const string TestServerName = "test-mcp-server";
    private static readonly List<string> TestToolNames = ["tool_a", "tool_b"];

    [Test]
    public void WithCapabilityTools_HttpTransport_CallsWithMcpServerToolWhitelistWithCorrectUrl()
    {
        var builderMock = new Mock<IAgentSessionBuilder>();
        builderMock
            .Setup(b => b.WithMcpServerToolWhitelist(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>()))
            .Returns(builderMock.Object);

        var tools = new StartedStubTools(TestUrl, TestServerName, TestToolNames);

        builderMock.Object.WithCapabilityTools(tools);

        builderMock.Verify(
            b => b.WithMcpServerToolWhitelist(TestUrl, It.IsAny<string>(), It.IsAny<List<string>>()),
            Times.Once);
    }

    [Test]
    public void WithCapabilityTools_HttpTransport_PassesMcpServerName()
    {
        var builderMock = new Mock<IAgentSessionBuilder>();
        builderMock
            .Setup(b => b.WithMcpServerToolWhitelist(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>()))
            .Returns(builderMock.Object);

        var tools = new StartedStubTools(TestUrl, TestServerName, TestToolNames);

        builderMock.Object.WithCapabilityTools(tools);

        builderMock.Verify(
            b => b.WithMcpServerToolWhitelist(It.IsAny<string>(), TestServerName, It.IsAny<List<string>>()),
            Times.Once);
    }

    [Test]
    public void WithCapabilityTools_HttpTransport_PassesToolNamesAsWhitelist()
    {
        var builderMock = new Mock<IAgentSessionBuilder>();
        builderMock
            .Setup(b => b.WithMcpServerToolWhitelist(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>()))
            .Returns(builderMock.Object);

        var tools = new StartedStubTools(TestUrl, TestServerName, TestToolNames);

        builderMock.Object.WithCapabilityTools(tools);

        builderMock.Verify(
            b => b.WithMcpServerToolWhitelist(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.Is<List<string>>(list => list.SequenceEqual(TestToolNames))),
            Times.Once);
    }

    [Test]
    public void WithCapabilityTools_BeforeStartAsync_ThrowsInvalidOperationException()
    {
        var builderMock = new Mock<IAgentSessionBuilder>();
        var tools = new UnstartedStubTools();

        Assert.Throws<InvalidOperationException>(() => builderMock.Object.WithCapabilityTools(tools));
    }

    [Test]
    public void WithCapabilityTools_AfterStartAsync_InvokesWithMcpServerToolWhitelistWithCorrectServerNameAndToolNames()
    {
        var builderMock = new Mock<IAgentSessionBuilder>();
        builderMock
            .Setup(b => b.WithMcpServerToolWhitelist(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>()))
            .Returns(builderMock.Object);

        var tools = new StartedStubTools(TestUrl, TestServerName, TestToolNames);

        builderMock.Object.WithCapabilityTools(tools);

        builderMock.Verify(
            b => b.WithMcpServerToolWhitelist(TestUrl, TestServerName, It.Is<List<string>>(l => l.SequenceEqual(TestToolNames))),
            Times.Once);
    }

    [Test]
    public void WithCapabilityTools_UnknownTransportVariant_ThrowsNotSupportedException()
    {
        var builderMock = new Mock<IAgentSessionBuilder>();
        var tools = new UnknownTransportStubTools();

        Assert.Throws<NotSupportedException>(() => builderMock.Object.WithCapabilityTools(tools));
    }

    private sealed record CustomTransport(string McpServerName) : McpTransportConfig;

    private sealed class StartedStubTools(string endpointUrl, string serverName, List<string> toolNames) : ICapabilityMcpTools
    {
        public string SlotName => "started-slot";
        public bool IsStarted => true;
        public McpTransportConfig? CurrentTransport { get; } = new HttpMcpTransportConfig(endpointUrl, serverName);
        public IReadOnlyList<string> ToolNames { get; } = toolNames;

        public Task StartAsync(McpTransportConfig transport, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class UnstartedStubTools : ICapabilityMcpTools
    {
        public string SlotName => "unstarted-slot";
        public bool IsStarted => false;
        public McpTransportConfig? CurrentTransport => null;
        public IReadOnlyList<string> ToolNames => [];

        public Task StartAsync(McpTransportConfig transport, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class UnknownTransportStubTools : ICapabilityMcpTools
    {
        public string SlotName => "unknown-transport-slot";
        public bool IsStarted => true;
        public McpTransportConfig? CurrentTransport { get; } = new CustomTransport("custom-server");
        public IReadOnlyList<string> ToolNames => ["some_tool"];

        public Task StartAsync(McpTransportConfig transport, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
