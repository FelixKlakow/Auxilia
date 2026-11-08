using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.Testing.DummyWorkflows;

/// <summary>
/// Dummy workflow for the resumable run stream: publishes <see cref="ItemsPerPhase"/> numbered
/// items alternating over two views, waits for any run input, publishes as many again and ends
/// with the reason <c>completed</c> — so a test can connect late, leave mid-run, and resume.
/// </summary>
public static class ViewSequenceWorkflow
{
    public const string WorkflowName = "view-sequence-workflow";
    public const string LogView = "log";
    public const string StepsView = "steps";
    public const int ItemsPerPhase = 5;

    /// <summary>One published item: its number in publish order (1-based).</summary>
    public sealed record NumberedItem(int Number);

    public static Task RunAsync(string[] args) =>
        WorkflowBuilder
            .Create(WorkflowName)
            .DeclaresView<NumberedItem>(LogView, ViewRendering.Log, ViewLifecycle.LiveAndPersisted)
            .DeclaresView<NumberedItem>(StepsView, ViewRendering.Log, ViewLifecycle.LiveAndPersisted)
            .WithApplication(ExecuteAsync)
            .Run(args);

    private static async Task ExecuteAsync(IServiceProvider provider, CancellationToken ct)
    {
        var views = provider.GetRequiredService<IViewPublisher>();
        await PublishPhaseAsync(views, first: 1, ct);
        await provider.GetRequiredService<IWorkflowInputs>().ReceiveAsync(ct);
        await PublishPhaseAsync(views, first: ItemsPerPhase + 1, ct);
        provider.GetRequiredService<IRunOutcome>().SetReason("completed");
    }

    private static async Task PublishPhaseAsync(IViewPublisher views, int first, CancellationToken ct)
    {
        for (var number = first; number < first + ItemsPerPhase; number++)
            await views.PublishAsync(number % 2 == 0 ? StepsView : LogView, new NumberedItem(number), ct);
    }
}
