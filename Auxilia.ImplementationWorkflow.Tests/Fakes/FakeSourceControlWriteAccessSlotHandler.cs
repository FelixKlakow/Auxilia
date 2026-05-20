using Auxilia.Workflows;
using Auxilia.Workflows.SourceControl;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.ImplementationWorkflow.Tests.Fakes;

public sealed class FakeSourceControlWriteAccessSlotHandler(FakeSourceControlWriteAccess instance) : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, SlotConfiguration configuration)
    {
        services.AddKeyedSingleton<ISourceControlWriteAccess>(slotName, instance);
        services.AddKeyedSingleton<ISourceControlAccess>(slotName, instance);
    }
}
