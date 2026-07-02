using Auxilia.Workflows.Views;

namespace Auxilia.CodingSession.Workflow;

/// <summary>
/// Orchestrates one live coding session: session branch on the mounted workspace, tmux+ttyd
/// host up, wait until the CLI exits (or the max-duration guard fires), then collect the
/// changed-file names and write the <see cref="CodingSessionResult"/> artifact.
/// </summary>
public sealed class CodingSessionApplication(
    ISessionHost host,
    IGitRunner git,
    IViewPublisher? views,
    SessionRunContext context,
    TimeProvider timeProvider)
{
    public const string ProgressViewName = "progress";

    public async Task<CodingSessionResult> RunAsync(CancellationToken cancellationToken)
    {
        var workspace = new GitWorkspace(git, context.WorkspaceDirectory);
        var started = timeProvider.GetUtcNow();

        await workspace.StartSessionBranchAsync(context.BranchName, cancellationToken);
        await PublishAsync("workspace", $"Session branch '{context.BranchName}' ready.", cancellationToken);

        await host.StartAsync(context, cancellationToken);
        await PublishAsync("session",
            $"Live session started — terminal on container port {context.TerminalPort}. " +
            "The run completes when the CLI exits.", cancellationToken);

        // The max-duration guard: a forgotten session must not hold the container (and the
        // personal token's environment) open forever.
        var timedOut = false;
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        guard.CancelAfter(context.MaxDuration);
        try
        {
            await host.WaitForSessionEndAsync(guard.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            await PublishAsync("session",
                $"Max duration of {context.MaxDuration.TotalMinutes:0} min reached — ending the session.",
                cancellationToken);
        }

        await host.ShutdownAsync();

        var changedFiles = await workspace.ChangedFilesAsync(cancellationToken);
        var result = new CodingSessionResult(
            context.BranchName, changedFiles, started, timeProvider.GetUtcNow(), timedOut);
        await result.WriteAsync(context.OutputDirectory, cancellationToken);

        await PublishAsync("result",
            $"Session ended — {changedFiles.Count} changed file{(changedFiles.Count == 1 ? "" : "s")} on '{context.BranchName}'.",
            cancellationToken);
        return result;
    }

    private Task PublishAsync(string phase, string message, CancellationToken ct)
        => views?.PublishAsync(ProgressViewName, new SessionProgressEntry(phase, message), ct)
           ?? Task.CompletedTask;
}
