using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.Policy;

namespace Auxilia.Workflows.SourceControl.Mcp;

/// <summary>
/// MCP tool server for <see cref="ISourceControlWriteAccess"/>.
/// Extends <see cref="SourceControlAccessMcpTools"/> with write operations:
/// <c>create_branch</c>, <c>write_file</c>, <c>commit</c>, <c>push</c>.
/// </summary>
public sealed class SourceControlWriteAccessMcpTools : SourceControlAccessMcpTools
{
    public SourceControlWriteAccessMcpTools(
        string slotName,
        ISourceControlWriteAccess access,
        ILoggerFactory? loggerFactory = null)
        : base(slotName, access, BuildWriteOptions(slotName, access, loggerFactory), loggerFactory)
    {
    }

    internal static McpServerOptions BuildWriteOptions(
        string slotName,
        ISourceControlWriteAccess access,
        ILoggerFactory? loggerFactory)
    {
        var options = BuildOptions(slotName, access, loggerFactory);
        var logger = loggerFactory?.CreateLogger<SourceControlWriteAccessMcpTools>();

        options.ToolCollection!.Add(McpServerTool.Create(
            async (string branchName, string? fromRef, CancellationToken ct) =>
            {
                try
                {
                    await access.CreateBranchAsync(branchName, fromRef, ct);
                    return $"Branch '{branchName}' created successfully.";
                }
                catch (ToolPolicyDeniedException ex)
                {
                    return $"Operation denied by tool policy: {ex.Key}";
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in create_branch");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "create_branch"),
                Description = "Creates a new branch. Optionally specify a source ref to branch from."
            }));

        options.ToolCollection!.Add(McpServerTool.Create(
            async (string relativePath, string content, CancellationToken ct) =>
            {
                try
                {
                    await access.WriteFileAsync(relativePath, content, ct);
                    return $"File '{relativePath}' written successfully.";
                }
                catch (ToolPolicyDeniedException ex)
                {
                    return $"Operation denied by tool policy: {ex.Key}";
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in write_file");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "write_file"),
                Description = "Writes content to a file in the working repository."
            }));

        options.ToolCollection!.Add(McpServerTool.Create(
            async (string message, CancellationToken ct) =>
            {
                try
                {
                    await access.CommitAsync(message, ct);
                    return $"Committed with message: {message}";
                }
                catch (ToolPolicyDeniedException ex)
                {
                    return $"Operation denied by tool policy: {ex.Key}";
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in commit");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "commit"),
                Description = "Commits staged changes with the given message."
            }));

        options.ToolCollection!.Add(McpServerTool.Create(
            async (CancellationToken ct) =>
            {
                try
                {
                    await access.PushAsync(ct);
                    return "Push completed successfully.";
                }
                catch (ToolPolicyDeniedException ex)
                {
                    return $"Operation denied by tool policy: {ex.Key}";
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in push");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "push"),
                Description = "Pushes committed changes to the remote repository."
            }));

        return options;
    }
}
