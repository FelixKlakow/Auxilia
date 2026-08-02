using Auxilia.Workflows;
using Auxilia.Workflows.TaskSource;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.CodeReview.Workflow.Tests.Fakes;

public sealed class FakeWorkItemAccessSlotHandler : ISlotHandler
{
    private readonly FakeWorkItemAccess _instance;

    public FakeWorkItemAccessSlotHandler(FakeWorkItemAccess instance)
    {
        _instance = instance;
    }

    public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
        => services.AddSingleton<IWorkItemAccess>(_instance);
}
