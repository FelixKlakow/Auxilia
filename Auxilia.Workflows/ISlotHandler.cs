using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows;

public interface ISlotHandler
{
    void Register(IServiceCollection services, SlotConfiguration configuration);
}
