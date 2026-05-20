using Auxilia.Workflows;
using Auxilia.Workflows.PullRequestAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.ImplementationWorkflow.Tests.Fakes;

public sealed class FakePullRequestAccessSlotHandler(FakePullRequestAccess instance) : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, SlotConfiguration configuration)
        => services.AddKeyedSingleton<IPullRequestAccess>(slotName, instance);
}
