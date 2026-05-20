using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.Policy;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.PullRequestAccess.Mcp;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.SourceControl.Mcp;
using Auxilia.Workflows.TaskSource;
using Auxilia.Workflows.TaskSource.Mcp;
using Auxilia.Workflows.TestRunner;
using Auxilia.Workflows.TestRunner.Mcp;
using Moq;

namespace Auxilia.Workflows.Tests.Mcp;

/// <summary>
/// Unit tests for Phase 3 MCP tool surface projections.
/// Verifies tool name registration and ToolPolicyDeniedException non-propagation.
/// </summary>
[TestFixture]
[Category("Unit")]
public class Phase3McpToolsTests
{
    private const string Slot = "test-slot";

    #region Tool name tests

    [Test]
    public void SourceControlWriteAccessMcpTools_BuildWriteOptions_ContainsWriteToolNames()
    {
        var accessMock = new Mock<ISourceControlWriteAccess>();
        var options = SourceControlWriteAccessMcpTools.BuildWriteOptions(Slot, accessMock.Object, null);
        var toolNames = options.ToolCollection!.Select(t => t.ProtocolTool.Name).ToList();

        Assert.That(toolNames, Contains.Item(SlotMcpPrefix.Format(Slot, "create_branch")));
        Assert.That(toolNames, Contains.Item(SlotMcpPrefix.Format(Slot, "write_file")));
        Assert.That(toolNames, Contains.Item(SlotMcpPrefix.Format(Slot, "commit")));
        Assert.That(toolNames, Contains.Item(SlotMcpPrefix.Format(Slot, "push")));
    }

    [Test]
    public void PullRequestAccessMcpTools_BuildOptions_ContainsOpenPullRequestTool()
    {
        var accessMock = new Mock<IPullRequestAccess>();
        var options = PullRequestAccessMcpTools.BuildOptions(Slot, accessMock.Object, null);
        var toolNames = options.ToolCollection!.Select(t => t.ProtocolTool.Name).ToList();

        Assert.That(toolNames, Has.Count.EqualTo(6));
        Assert.That(toolNames, Contains.Item(SlotMcpPrefix.Format(Slot, "open_pull_request")));
    }

    [Test]
    public void TaskSourceMcpTools_BuildOptions_ContainsExpectedToolNames()
    {
        var accessMock = new Mock<ITaskSourceAccess>();
        var options = TaskSourceMcpTools.BuildOptions(Slot, accessMock.Object, null);
        var toolNames = options.ToolCollection!.Select(t => t.ProtocolTool.Name).ToList();

        Assert.That(toolNames, Has.Count.EqualTo(4));
        Assert.That(toolNames, Contains.Item(SlotMcpPrefix.Format(Slot, "get_work_item")));
        Assert.That(toolNames, Contains.Item(SlotMcpPrefix.Format(Slot, "get_work_items")));
        Assert.That(toolNames, Contains.Item(SlotMcpPrefix.Format(Slot, "update_status")));
        Assert.That(toolNames, Contains.Item(SlotMcpPrefix.Format(Slot, "post_comment")));
    }

    [Test]
    public void TestRunnerMcpTools_BuildOptions_ContainsRunTestsTool()
    {
        var runnerMock = new Mock<ITestRunner>();
        var options = TestRunnerMcpTools.BuildOptions(Slot, runnerMock.Object, null);
        var toolNames = options.ToolCollection!.Select(t => t.ProtocolTool.Name).ToList();

        Assert.That(toolNames, Has.Count.EqualTo(1));
        Assert.That(toolNames, Contains.Item(SlotMcpPrefix.Format(Slot, "run_tests")));
    }

    #endregion

    #region Policy denial tests

    [Test]
    public async Task SourceControlWriteAccessMcpTools_CreateBranch_PolicyDenied_ReturnsErrorString()
    {
        var accessMock = new Mock<ISourceControlWriteAccess>();
        accessMock.Setup(a => a.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new ToolPolicyDeniedException("source_control.create_branch", Slot));

        var tools = new SourceControlWriteAccessMcpTools(Slot, accessMock.Object);
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            var endpointUrl = ((HttpMcpTransportConfig)tools.CurrentTransport!).EndpointUrl;
            await using var client = await CreateHttpClientAsync(endpointUrl);

            var result = await client.CallToolAsync(
                SlotMcpPrefix.Format(Slot, "create_branch"),
                new Dictionary<string, object?> { ["branchName"] = "feature/test", ["fromRef"] = null },
                null, null, CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.That(text, Does.Contain("Operation denied by tool policy"));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task PullRequestAccessMcpTools_OpenPullRequest_PolicyDenied_ReturnsErrorString()
    {
        var accessMock = new Mock<IPullRequestAccess>();
        accessMock.Setup(a => a.OpenPullRequestAsync(It.IsAny<PullRequestOptions>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new ToolPolicyDeniedException("pull_request.open_pull_request", Slot));

        var tools = new PullRequestAccessMcpTools(Slot, accessMock.Object);
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            var endpointUrl = ((HttpMcpTransportConfig)tools.CurrentTransport!).EndpointUrl;
            await using var client = await CreateHttpClientAsync(endpointUrl);

            var prOptionsJson = JsonSerializer.Serialize(new
            {
                title = "My PR",
                sourceBranch = "feature/test",
                targetBranch = "main"
            });

            var result = await client.CallToolAsync(
                SlotMcpPrefix.Format(Slot, "open_pull_request"),
                new Dictionary<string, object?> { ["optionsJson"] = prOptionsJson },
                null, null, CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.That(text, Does.Contain("Operation denied by tool policy"));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task TaskSourceMcpTools_UpdateStatus_PolicyDenied_ReturnsErrorString()
    {
        var accessMock = new Mock<ITaskSourceAccess>();
        accessMock.Setup(a => a.UpdateStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new ToolPolicyDeniedException("task_source.update_status", Slot));

        var tools = new TaskSourceMcpTools(Slot, accessMock.Object);
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            var endpointUrl = ((HttpMcpTransportConfig)tools.CurrentTransport!).EndpointUrl;
            await using var client = await CreateHttpClientAsync(endpointUrl);

            var result = await client.CallToolAsync(
                SlotMcpPrefix.Format(Slot, "update_status"),
                new Dictionary<string, object?> { ["id"] = "WI-1", ["newStatus"] = "Done" },
                null, null, CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.That(text, Does.Contain("Operation denied by tool policy"));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    [Test]
    public async Task TestRunnerMcpTools_RunTests_PolicyDenied_ReturnsErrorString()
    {
        var runnerMock = new Mock<ITestRunner>();
        runnerMock.Setup(r => r.RunTestsAsync(It.IsAny<TestRunRequest>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new ToolPolicyDeniedException("test_runner.run_tests", Slot));

        var tools = new TestRunnerMcpTools(Slot, runnerMock.Object);
        await tools.StartAsync(new HttpMcpTransportConfig("http://localhost:0", "test"));
        try
        {
            var endpointUrl = ((HttpMcpTransportConfig)tools.CurrentTransport!).EndpointUrl;
            await using var client = await CreateHttpClientAsync(endpointUrl);

            var requestJson = JsonSerializer.Serialize(new { command = "dotnet test" });

            var result = await client.CallToolAsync(
                SlotMcpPrefix.Format(Slot, "run_tests"),
                new Dictionary<string, object?> { ["requestJson"] = requestJson },
                null, null, CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.That(text, Does.Contain("Operation denied by tool policy"));
        }
        finally
        {
            await tools.StopAsync();
        }
    }

    #endregion

    private static async Task<McpClient> CreateHttpClientAsync(string endpointUrl)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpointUrl),
            TransportMode = HttpTransportMode.StreamableHttp
        });
        return await McpClient.CreateAsync(transport, new McpClientOptions(), null, CancellationToken.None);
    }
}
