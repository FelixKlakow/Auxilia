namespace Auxilia.Workflows;

public sealed class DefaultProcessExitService : IProcessExitService
{
    public void Exit(int exitCode) => System.Environment.Exit(exitCode);
}
