using Auxilia.Workflows.Mcp;

namespace Auxilia.Workflows.Tests.Mcp;

[TestFixture]
[Category("Unit")]
public class McpTransportConfigTests
{
    [Test]
    public void HttpMcpTransportConfig_CanBeConstructed_WithExpectedProperties()
    {
        var config = new HttpMcpTransportConfig("http://localhost:5050", "my-server");

        Assert.That(config.EndpointUrl, Is.EqualTo("http://localhost:5050"));
        Assert.That(config.McpServerName, Is.EqualTo("my-server"));
    }

    [Test]
    public void HttpMcpTransportConfig_IsAssignableFromAbstractBase()
    {
        McpTransportConfig config = new HttpMcpTransportConfig("http://localhost:5050", "my-server");

        Assert.That(config, Is.InstanceOf<McpTransportConfig>());
    }

    [Test]
    public void HttpMcpTransportConfig_IsPatternMatchableAsMcpTransportConfig()
    {
        McpTransportConfig config = new HttpMcpTransportConfig("http://localhost:5050", "my-server");

        var matched = config switch
        {
            HttpMcpTransportConfig http => http.EndpointUrl,
            _ => null
        };

        Assert.That(matched, Is.EqualTo("http://localhost:5050"));
    }

    [Test]
    public void HttpMcpTransportConfig_RecordEquality_SameValues_AreEqual()
    {
        var a = new HttpMcpTransportConfig("http://localhost:5050", "my-server");
        var b = new HttpMcpTransportConfig("http://localhost:5050", "my-server");

        Assert.That(a, Is.EqualTo(b));
    }

    [Test]
    public void HttpMcpTransportConfig_RecordEquality_HoldsForSameValues()
    {
        var a = new HttpMcpTransportConfig("http://localhost:5050", "my-server");
        var b = new HttpMcpTransportConfig("http://localhost:5050", "my-server");

        Assert.That(a, Is.EqualTo(b));
    }

    [Test]
    public void HttpMcpTransportConfig_RecordEquality_DifferentUrl_AreNotEqual()
    {
        var a = new HttpMcpTransportConfig("http://localhost:5050", "my-server");
        var b = new HttpMcpTransportConfig("http://localhost:9090", "my-server");

        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void HttpMcpTransportConfig_McpServerName_IsAccessible()
    {
        McpTransportConfig config = new HttpMcpTransportConfig("http://localhost:5050", "srv");

        Assert.That(config.McpServerName, Is.EqualTo("srv"));
    }

    [Test]
    public void McpTransportConfig_SwitchExpression_HttpVariant_Matches()
    {
        McpTransportConfig config = new HttpMcpTransportConfig("http://localhost:8080", "test-server");

        var url = config switch
        {
            HttpMcpTransportConfig http => http.EndpointUrl,
            _ => throw new InvalidOperationException("Unexpected transport type")
        };

        Assert.That(url, Is.EqualTo("http://localhost:8080"));
    }

    [Test]
    public void NamedPipeMcpTransportConfig_IsPatternMatchableAsMcpTransportConfig()
    {
        McpTransportConfig config = new NamedPipeMcpTransportConfig("test-pipe", "pipe-server");

        var pipeName = config switch
        {
            NamedPipeMcpTransportConfig pipe => pipe.PipeName,
            _ => null
        };

        Assert.That(pipeName, Is.EqualTo("test-pipe"));
    }

    [Test]
    public void NamedPipeMcpTransportConfig_RecordEquality_HoldsForSameValues()
    {
        var a = new NamedPipeMcpTransportConfig("my-pipe", "srv");
        var b = new NamedPipeMcpTransportConfig("my-pipe", "srv");

        Assert.That(a, Is.EqualTo(b));
    }

    [Test]
    public void NamedPipeMcpTransportConfig_McpServerName_IsAccessible()
    {
        McpTransportConfig config = new NamedPipeMcpTransportConfig("some-pipe", "pipe-srv");

        Assert.That(config.McpServerName, Is.EqualTo("pipe-srv"));
    }
}
