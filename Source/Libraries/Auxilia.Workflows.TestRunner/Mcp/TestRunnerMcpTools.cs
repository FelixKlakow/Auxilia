using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.Policy;

namespace Auxilia.Workflows.TestRunner.Mcp;

/// <summary>
/// MCP tool server for <see cref="ITestRunner"/>.
/// Exposes test execution as a prefixed MCP tool under the given slot name.
/// </summary>
public sealed class TestRunnerMcpTools : CapabilityMcpToolsBase
{
    public TestRunnerMcpTools(
        string slotName,
        ITestRunner runner,
        ILoggerFactory? loggerFactory = null)
        : base(slotName, BuildOptions(slotName, runner, loggerFactory), loggerFactory)
    {
    }

    internal static McpServerOptions BuildOptions(
        string slotName,
        ITestRunner runner,
        ILoggerFactory? loggerFactory)
    {
        var logger = loggerFactory?.CreateLogger<TestRunnerMcpTools>();
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var options = new McpServerOptions
        {
            ToolCollection = new McpServerPrimitiveCollection<McpServerTool>(),
            ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = slotName,
                Version = "1.0"
            }
        };

        options.ToolCollection.Add(McpServerTool.Create(
            async (string requestJson, CancellationToken ct) =>
            {
                try
                {
                    var request = JsonSerializer.Deserialize<TestRunRequest>(requestJson, jsonOptions)
                        ?? throw new ArgumentException("Failed to deserialize TestRunRequest.");
                    var result = await runner.RunTestsAsync(request, ct);
                    return JsonSerializer.Serialize(result, jsonOptions);
                }
                catch (ToolPolicyDeniedException ex)
                {
                    return $"Operation denied by tool policy: {ex.Key}";
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in run_tests");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "run_tests"),
                Description = "Runs tests using the given JSON TestRunRequest (command, optional testFilter, optional timeout) and returns a JSON TestRunResult."
            }));

        return options;
    }
}
