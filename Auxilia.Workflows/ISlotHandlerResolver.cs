namespace Auxilia.Workflows;

public interface ISlotHandlerResolver
{
    void Register(string providerType, ISlotHandler handler);
    ISlotHandler Resolve(string providerType);
}
