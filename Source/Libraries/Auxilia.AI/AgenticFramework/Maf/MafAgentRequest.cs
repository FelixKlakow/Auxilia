using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Auxilia.AI.AgenticFramework.Maf;

/// <summary>
/// Microsoft Agent Framework implementation of <see cref="IAgentRequest"/>.
/// Runs the agent using the streaming API so intermediate events can be published,
/// then calls the validator on the aggregated text.
/// </summary>
internal sealed class MafAgentRequest : IAgentRequest
{
    private readonly MafAgentSession _session;
    private string? _modelOverride;

    public string Prompt { get; }
    public IAgentSession Session => _session;

    internal MafAgentRequest(string prompt, MafAgentSession session)
    {
        Prompt = prompt;
        _session = session;
    }

    public IAgentRequest WithNonDefaultModel(string modelName)
    {
        _modelOverride = modelName;
        return this;
    }

    public async Task<TValidatorResult> ExecuteRequestAsync<TValidatorResult>(
        IAgentResultValidator<TValidatorResult> validator,
        CancellationToken cancellationToken)
    {
        _session.Publish(new AgentRequestStartedEvent(_session.Id, DateTime.UtcNow, Prompt));

        ChatClientAgentRunOptions? runOptions = null;
        if (_modelOverride is not null)
        {
            runOptions = new ChatClientAgentRunOptions(
                new ChatOptions { ModelId = _modelOverride });
        }

        var responseText = new System.Text.StringBuilder();

        try
        {
            await foreach (var update in _session.Agent.RunStreamingAsync(
                               Prompt,
                               _session.AgentSession,
                               runOptions,
                               cancellationToken))
            {
                var chunk = update.Text;
                if (chunk.Length > 0)
                {
                    responseText.Append(chunk);
                    _session.Publish(new AgentResponseChunkEvent(
                        _session.Id, DateTime.UtcNow, chunk));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _session.Publish(new AgentErrorEvent(_session.Id, DateTime.UtcNow, ex));
            throw;
        }

        var fullText = responseText.ToString();
        _session.Publish(new AgentResponseCompleteEvent(_session.Id, DateTime.UtcNow, fullText));

        return await validator.ValidateAsync(this, fullText);
    }
}


