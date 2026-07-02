using Auxilia.Workflows;

namespace Auxilia.CodingSession.Workflow;

/// <summary>
/// Everything one interactive session run needs from its launch environment: where the
/// session shell lives, how long it may run, and the terminal port ttyd listens on.
/// </summary>
public sealed record SessionRunContext(
    string WorkspaceDirectory,
    string OutputDirectory,
    string SessionCommand,
    int TerminalPort,
    TimeSpan MaxDuration,
    string BranchName)
{
    /// <summary>The container port ttyd serves the terminal on; published by the launcher.</summary>
    public const int DefaultTerminalPort = 7681;

    public static SessionRunContext FromEnvironment()
        => FromValues(
            Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.WorkspaceDirectory),
            Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.OutputDirectory),
            Environment.GetEnvironmentVariable("CODING_SESSION_CLI"),
            Environment.GetEnvironmentVariable("CODING_SESSION_MAX_MINUTES"),
            Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.InstanceId));

    internal static SessionRunContext FromValues(
        string? workspaceDirectory, string? outputDirectory, string? sessionCommand,
        string? maxMinutes, string? instanceId)
    {
        var runTag = string.IsNullOrWhiteSpace(instanceId)
            ? Guid.NewGuid().ToString("N")[..8]
            : instanceId.Replace("-", "")[..Math.Min(8, instanceId.Replace("-", "").Length)];

        return new SessionRunContext(
            Fallback(workspaceDirectory, "coding-session-workspace"),
            Fallback(outputDirectory, "coding-session-output"),
            string.IsNullOrWhiteSpace(sessionCommand) ? "claude" : sessionCommand.Trim(),
            DefaultTerminalPort,
            TimeSpan.FromMinutes(int.TryParse(maxMinutes, out var minutes) && minutes > 0 ? minutes : 240),
            $"cc-session/{runTag}");

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
