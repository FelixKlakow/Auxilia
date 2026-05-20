using Auxilia.Workflows;
using Auxilia.Workflows.TestRunner;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.ImplementationWorkflow.Tests.Fakes;

public sealed class FakeTestRunnerSlotHandler(FakeTestRunner instance) : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, SlotConfiguration configuration)
        => services.AddKeyedSingleton<ITestRunner>(slotName, instance);
}
