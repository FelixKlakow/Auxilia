using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.CodeReview.Workflow.Tests.Fakes;

public sealed class FakeAiAgentSlotHandler : ISlotHandler
{
    private readonly FakeAiAgent _instance;

    public FakeAiAgentSlotHandler(FakeAiAgent instance)
    {
        _instance = instance;
    }

    public void Register(IServiceCollection services, string slotName, SlotConfiguration configuration)
        => services.AddKeyedSingleton<IAiAgent>(slotName, _instance);
}
