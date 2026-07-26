using Auxilia.Workflows;

namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>
/// Everything one run needs from its launch environment: the instruction assembled from the
/// dispatch context, the workspace the agent works in, and the output directory.
/// </summary>
public sealed record AgentSessionContext(
    string Instruction,
    string WorkspaceDirectory,
    string OutputDirectory)
{
    public static AgentSessionContext FromEnvironment()
        => FromValues(
            System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__TITLE"),
            System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__BODY"),
            System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.WorkspaceDirectory),
            System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.OutputDirectory));

    /// <summary>
    /// The instruction is Title + Body of the dispatch context — the mail subject/body for
    /// mail-triggered runs, the same shape for manual, scheduled, and rerun dispatches.
    /// </summary>
    public static AgentSessionContext FromValues(
        string? title, string? body, string? workspaceDirectory, string? outputDirectory)
    {
        var instruction = string.Join(
            "\n\n",
            new[] { title, body }.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (instruction.Length == 0)
            throw new InvalidOperationException(
                "The dispatch context carries no instruction — an agent session needs a Title and/or Body context entry.");

        return new AgentSessionContext(
            instruction,
            Fallback(workspaceDirectory, "agent-session-workspace"),
            Fallback(outputDirectory, "agent-session-output"));

        static string Fallback(string? configured, string tempName)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;
            var fallback = Path.Combine(Path.GetTempPath(), $"{tempName}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}
