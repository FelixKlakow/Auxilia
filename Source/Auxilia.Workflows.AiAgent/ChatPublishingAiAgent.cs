namespace Auxilia.Workflows.AiAgent;

/// <summary>
/// Decorates an <see cref="IAiAgent"/> so every session turn is published to the workflow's
/// declared "agent-chat" view: the prompt as a user entry, the response as an assistant entry.
/// </summary>
public sealed class ChatPublishingAiAgent(IAiAgent inner, AgentChatPublisher chat) : IAiAgent
{
    public async Task<IAiSession> OpenSessionAsync(
        AiSessionOptions? options = null, CancellationToken cancellationToken = default)
        => new ChatPublishingAiSession(await inner.OpenSessionAsync(options, cancellationToken), chat);

    private sealed class ChatPublishingAiSession(IAiSession inner, AgentChatPublisher chat) : IAiSession
    {
        public async Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            await chat.PublishUserAsync(prompt, cancellationToken);
            var response = await inner.ExecuteAsync(prompt, cancellationToken);
            await chat.PublishAssistantAsync(response, ct: cancellationToken);
            return response;
        }

        public Task CompactAsync(string focusDescription, CancellationToken cancellationToken = default)
            => inner.CompactAsync(focusDescription, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
