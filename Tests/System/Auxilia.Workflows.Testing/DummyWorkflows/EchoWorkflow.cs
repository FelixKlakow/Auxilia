using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.Testing.DummyWorkflows;

/// <summary>
/// Dummy workflow with a declared text input and a live output view — the minimal end-to-end
/// demo of the configure → run-with-dynamic-values → watch-outputs loop: it echoes the
/// <c>Message</c> input into its <c>output</c> view a few times, then completes.
/// </summary>
public static class EchoWorkflow
{
    public const string WorkflowName = "echo-workflow";
    public const string OutputViewName = "output";

    /// <summary>One echoed line in the output view.</summary>
    public sealed record EchoLine(string Line);

    public static Task RunAsync(string[] args) =>
        WorkflowBuilder
            .Create(WorkflowName)
            .RequiresInput("Message", "Message", required: true,
                "The text this workflow echoes into its output view.")
            .DeclaresView<EchoLine>(OutputViewName, ViewRendering.Log, ViewLifecycle.LiveAndPersisted)
            .DeclaresTrigger(TriggerDeclaration.Manual, "Run with a message to echo.")
            .WithApplication(ExecuteAsync)
            .Run(args);

    private static async Task ExecuteAsync(IServiceProvider provider, CancellationToken ct)
    {
        var message = System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__MESSAGE")
                      ?? "(no message)";
        var views = provider.GetService<IViewPublisher>();

        for (var i = 1; i <= 3; i++)
        {
            var line = $"echo {i}/3: {message}";
            Console.WriteLine($"[EchoWorkflow] {line}");
            if (views is not null)
                await views.PublishAsync(OutputViewName, new EchoLine(line), ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}
