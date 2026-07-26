namespace Auxilia.Workflows.Testing.DummyWorkflows;

/// <summary>
/// Dummy workflow that sleeps for a configurable duration, used by failover system tests to
/// keep a run in the Running state long enough to kill its owning Core.Runner.
/// Declares no slots. Sleep duration comes from <c>WORKFLOW_CONTEXT__SLEEP_SECONDS</c> (default 60).
/// </summary>
public static class SleepingWorkflow
{
    public const string WorkflowName = "sleeping-workflow";

    public static Task RunAsync(string[] args) =>
        WorkflowBuilder
            .Create(WorkflowName)
            .WithApplication(ExecuteAsync)
            .Run(args);

    private static async Task ExecuteAsync(IServiceProvider _, CancellationToken ct)
    {
        var seconds = int.TryParse(
            System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__SLEEP_SECONDS"), out var s) ? s : 60;
        Console.WriteLine($"[SleepingWorkflow] Sleeping {seconds}s …");
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        Console.WriteLine("[SleepingWorkflow] Done.");
    }
}
