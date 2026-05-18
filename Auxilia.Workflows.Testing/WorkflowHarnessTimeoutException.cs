namespace Auxilia.Workflows.Testing;

public sealed class WorkflowHarnessTimeoutException : Exception
{
    public WorkflowHarnessTimeoutException(TimeSpan timeout)
        : base($"The workflow harness did not receive the expected message within the configured timeout of {timeout}.")
    {
    }
}
