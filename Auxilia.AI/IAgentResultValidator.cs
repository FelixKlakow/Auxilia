namespace Auxilia.AI;

public interface IAgentResultValidator<TValidatorResult>
{
    Task<TValidatorResult> ValidateAsync(IAgentRequest originalRequest, string agentTextOutput);
}