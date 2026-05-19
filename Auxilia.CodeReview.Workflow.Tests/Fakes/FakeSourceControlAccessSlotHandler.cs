using Auxilia.Workflows;
using Auxilia.Workflows.SourceControl;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.CodeReview.Workflow.Tests.Fakes;

public sealed class FakeSourceControlAccessSlotHandler : ISlotHandler
{
    private readonly FakeSourceControlAccess _instance;

    public FakeSourceControlAccessSlotHandler(FakeSourceControlAccess instance)
    {
        _instance = instance;
    }

    public void Register(IServiceCollection services, string slotName, SlotConfiguration configuration)
        => services.AddSingleton<ISourceControlAccess>(_instance);
}
