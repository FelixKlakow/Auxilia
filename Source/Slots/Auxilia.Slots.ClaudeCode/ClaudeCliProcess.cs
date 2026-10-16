using System.Diagnostics;
using Auxilia.Workflows.Sandbox;

namespace Auxilia.Slots.ClaudeCode;

/// <summary>Seam between <see cref="ClaudeCodeCliAgent"/> and the operating system process.</summary>
public interface IClaudeCliProcessFactory
{
    IClaudeCliProcess Start(ProcessStartInfo startInfo);
}

public interface IClaudeCliProcess : IDisposable
{
    TextReader Output { get; }
    TextReader Error { get; }
    /// <summary>Stdin of the CLI - only valid for interactive (stream-json input) sessions.</summary>
    TextWriter Input { get; }
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
    /// <summary>Closes stdin so an interactive CLI ends its session and exits.</summary>
    void CloseInput();
    void Kill();
}

public sealed class ClaudeCliProcessFactory : IClaudeCliProcessFactory
{
    public IClaudeCliProcess Start(ProcessStartInfo startInfo)
    {
        // The CLI is agent-controlled code: unprivileged agent user, no platform secret, and
        // nothing it spawns outlives the session (the lease kills leftovers when it ends).
        var lease = AgentSandbox.EnterAgentSession();
        try
        {
            var process = Process.Start(AgentSandbox.Confine(startInfo))
                          ?? throw new InvalidOperationException(
                              $"Failed to start the Claude Code CLI ('{startInfo.FileName}').");
            return new ClaudeCliProcess(process, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private sealed class ClaudeCliProcess(Process process, IDisposable lease) : IClaudeCliProcess
    {
        public TextReader Output => process.StandardOutput;
        public TextReader Error => process.StandardError;
        public TextWriter Input => process.StandardInput;

        public void CloseInput()
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (InvalidOperationException)
            {
                // Stdin was not redirected (autonomous session) or the process is gone.
            }
        }

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
                // Exited between the check and the kill — nothing left to stop.
            }
        }

        public void Dispose()
        {
            process.Dispose();
            lease.Dispose();
        }
    }
}
