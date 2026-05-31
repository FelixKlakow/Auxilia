using Auxilia.Workflows;
using Auxilia.Workflows.SourceControl;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.ImplementationWorkflow.Tests.Fakes;

public sealed class FakeSourceControlWriteAccessSlotHandler(
    FakeSourceControlWriteAccess instance,
    Action<IServiceCollection>? bootstrap = null) : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
    {
        bootstrap?.Invoke(services);
        services.AddKeyedSingleton<ISourceControlWriteAccess>(slotName, instance);
        services.AddKeyedSingleton<ISourceControlAccess>(slotName, instance);
    }
}
