using Auxilia.ImplementationWorkflow.Context;
using Auxilia.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.ImplementationWorkflow.Tests.Fakes;

public sealed class FakeWorkflowBootstrapSlotHandler(
    string outputDirectory,
    FakeSignalEmitter signalEmitter,
    string workItemId,
    ImplementationWorkflowConfiguration configuration) : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, SlotConfiguration slotConfiguration)
    {
        // Pre-register fake signal emitter before AddImplementationWorkflow (which uses TryAdd)
        services.AddSingleton<ISignalEmitter>(signalEmitter);

        // Pre-register configuration overrides before AddImplementationWorkflow
        services.AddSingleton(configuration);
        services.AddSingleton(new WorkItemTrigger(workItemId));

        services.AddImplementationWorkflow(outputDirectory);
    }
}
