using Auxilia.Workflows.Mcp;

namespace Auxilia.Workflows.Tests.Mcp;

/// <summary>
/// Zero-trust invariant tests: verifies that <see cref="ICapabilityMcpTools"/> implementations
/// are safely inert before <see cref="ICapabilityMcpTools.StartAsync"/> is called.
/// </summary>
[TestFixture]
[Category("Unit")]
public class ZeroTrustInvariantTests
{
    private StubCapabilityMcpTools _stub = null!;

    [SetUp]
    public void SetUp() => _stub = new StubCapabilityMcpTools();

    [Test]
    public void IsStarted_BeforeStartAsync_ReturnsFalse()
    {
        Assert.That(_stub.IsStarted, Is.False);
    }

    [Test]
    public void CurrentTransport_BeforeStartAsync_ReturnsNull()
    {
        Assert.That(_stub.CurrentTransport, Is.Null);
    }

    [Test]
    public void ToolNames_BeforeStartAsync_ReturnsEmptyList()
    {
        Assert.That(_stub.ToolNames, Has.Count.EqualTo(0));
    }

    [Test]
    public void WithCapabilityTools_BeforeStartAsync_ThrowsInvalidOperationException()
    {
        var builder = new StubAgentSessionBuilder();

        Assert.Throws<InvalidOperationException>(() => builder.WithCapabilityTools(_stub));
    }

    private sealed class StubCapabilityMcpTools : ICapabilityMcpTools
    {
        public string SlotName => "stub-slot";
        public bool IsStarted => false;
        public McpTransportConfig? CurrentTransport => null;
        public IReadOnlyList<string> ToolNames => [];

        public Task StartAsync(McpTransportConfig transport, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubAgentSessionBuilder : Auxilia.AI.IAgentSessionBuilder
    {
        public Auxilia.AI.IAgentSessionBuilder WithMcpServerTools(string url, string mcpName) => this;
        public Auxilia.AI.IAgentSessionBuilder WithMcpServerToolBlacklist(string url, string mcpName, List<string> blacklistedTools) => this;
        public Auxilia.AI.IAgentSessionBuilder WithMcpServerToolWhitelist(string url, string mcpName, List<string> whitelistedTools) => this;
        public Auxilia.AI.IAgentSessionBuilder WithWorkflowId(Guid workflowId) => this;
        public Auxilia.AI.IAgentSessionBuilder WithSystemPrompt(string systemPrompt) => this;
        public Auxilia.AI.IAgentSessionBuilder WithRootDirectory(string rootDirectory) => this;
        public Auxilia.AI.IAgentSessionBuilder WithDefaultModel(string modelName) => this;
        public Task<Auxilia.AI.IAgentSession> BuildAsync() => throw new NotImplementedException();
    }
}
