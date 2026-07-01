using System.Diagnostics;

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
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

public sealed class ClaudeCliProcessFactory : IClaudeCliProcessFactory
{
    public IClaudeCliProcess Start(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException(
                          $"Failed to start the Claude Code CLI ('{startInfo.FileName}').");
        return new ClaudeCliProcess(process);
    }

    private sealed class ClaudeCliProcess(Process process) : IClaudeCliProcess
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
                // Exited between the check and the kill — nothing left to stop.
            }
        }

        public void Dispose() => process.Dispose();
    }
}
