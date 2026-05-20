using Auxilia.Workflows;
using Auxilia.Workflows.TaskSource;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.ImplementationWorkflow.Tests.Fakes;

public sealed class FakeTaskSourceAccessSlotHandler(FakeTaskSourceAccess instance) : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, SlotConfiguration configuration)
        => services.AddKeyedSingleton<ITaskSourceAccess>(slotName, instance);
}
