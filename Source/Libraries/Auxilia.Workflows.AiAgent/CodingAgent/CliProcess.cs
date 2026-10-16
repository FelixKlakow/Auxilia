using System.Diagnostics;
using Auxilia.Workflows.Sandbox;

namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>Seam over a headless coding-CLI child process so agents can be unit-tested without one.</summary>
public interface ICliProcessFactory
{
    ICliProcess Start(ProcessStartInfo startInfo);
}

public interface ICliProcess : IDisposable
{
    TextReader Output { get; }
    TextReader Error { get; }
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Ends the child (and its process tree) unless it has already exited.</summary>
    void Kill();
}

public sealed class ProcessCliProcessFactory : ICliProcessFactory
{
    public static readonly ProcessCliProcessFactory Instance = new();

    public ICliProcess Start(ProcessStartInfo startInfo)
    {
        // The CLI is agent-controlled code: unprivileged agent user, no platform secret, and
        // nothing it spawns outlives the session (the lease kills leftovers when it ends).
        var lease = AgentSandbox.EnterAgentSession();
        try
        {
            return new ProcessCliProcess(Process.Start(AgentSandbox.Confine(startInfo))
                ?? throw new InvalidOperationException($"Failed to start '{startInfo.FileName}'."), lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private sealed class ProcessCliProcess(Process process, IDisposable lease) : ICliProcess
    {
        public TextReader Output => process.StandardOutput;
        public TextReader Error => process.StandardError;

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }

        public void Kill()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited between the check and the kill - nothing left to stop.
            }
        }

        public void Dispose()
        {
            process.Dispose();
            lease.Dispose();
        }
    }
}
