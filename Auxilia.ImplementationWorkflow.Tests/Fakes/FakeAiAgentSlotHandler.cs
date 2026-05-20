using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.ImplementationWorkflow.Tests.Fakes;

public sealed class FakeAiAgentSlotHandler(FakeAiAgent instance) : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, SlotConfiguration configuration)
        => services.AddKeyedSingleton<IAiAgent>(slotName, instance);
}
