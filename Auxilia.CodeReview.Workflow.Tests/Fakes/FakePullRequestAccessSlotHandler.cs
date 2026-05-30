using Auxilia.Workflows;
using Auxilia.Workflows.PullRequestAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.CodeReview.Workflow.Tests.Fakes;

public sealed class FakePullRequestAccessSlotHandler : ISlotHandler
{
    private readonly FakePullRequestAccess _instance;

    public FakePullRequestAccessSlotHandler(FakePullRequestAccess instance)
    {
        _instance = instance;
    }

    public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
        => services.AddSingleton<IPullRequestAccess>(_instance);
}
