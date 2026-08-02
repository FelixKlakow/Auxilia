namespace Auxilia.AI;

public interface IAgentRequest
{
    string Prompt { get; }
    IAgentSession Session { get; }
    IAgentRequest WithNonDefaultModel(string modelName);
    Task<TValidatorResult> ExecuteRequestAsync<TValidatorResult>(IAgentResultValidator<TValidatorResult> validator, CancellationToken cancellationToken);
}