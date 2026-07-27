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
    /// <summary>How permission requests are handled; see <see cref="AgentPermissionModes"/>.</summary>
    public string PermissionMode { get; init; } = AgentPermissionModes.AskOperator;

    /// <summary>The push-action policy, independent of <see cref="PermissionMode"/>.</summary>
    public string PushPolicy { get; init; } = AgentPermissionModes.AskOperator;

    /// <summary>
    /// The session stays alive after each turn awaiting the operator's next instruction; the
    /// initial instruction is optional in this mode.
    /// </summary>
    public bool MultiTurn { get; init; }

    public static AgentSessionContext FromEnvironment()
        => FromValues(
            System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__TITLE"),
            System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__BODY")
                ?? System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__INSTRUCTION"),
            System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.WorkspaceDirectory),
            System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.OutputDirectory),
            System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__PERMISSION-MODE"),
            System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__PUSH-POLICY"),
            System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__MULTI-TURN"));

    /// <summary>
    /// The instruction is Title + Body of the dispatch context — the mail subject/body for
    /// mail-triggered runs, the same shape for manual, scheduled, and rerun dispatches. A
    /// multi-turn session may start without one (the operator sends the first instruction live).
    /// </summary>
    public static AgentSessionContext FromValues(
        string? title, string? body, string? workspaceDirectory, string? outputDirectory,
        string? permissionMode = null, string? pushPolicy = null, string? multiTurn = null)
    {
        var isMultiTurn = IsTrue(multiTurn);
        var instruction = string.Join(
            "\n\n",
            new[] { title, body }.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (instruction.Length == 0 && !isMultiTurn)
            throw new InvalidOperationException(
                "The dispatch context carries no instruction — a single-turn agent session needs a "
                + "Title and/or Body context entry (only multi-turn sessions may start without one).");

        return new AgentSessionContext(
            instruction,
            Fallback(workspaceDirectory, "agent-session-workspace"),
            Fallback(outputDirectory, "agent-session-output"))
        {
            PermissionMode = string.IsNullOrWhiteSpace(permissionMode)
                ? AgentPermissionModes.AskOperator
                : permissionMode.Trim(),
            PushPolicy = string.IsNullOrWhiteSpace(pushPolicy)
                ? AgentPermissionModes.AskOperator
                : pushPolicy.Trim(),
            MultiTurn = isMultiTurn,
        };

        static bool IsTrue(string? value)
            => value?.Trim() is { } trimmed
               && (bool.TryParse(trimmed, out var parsed) ? parsed : trimmed == "1");

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
