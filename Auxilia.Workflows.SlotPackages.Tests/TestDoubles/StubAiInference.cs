using Auxilia.Workflows.AiAgent;

namespace Auxilia.Workflows.SlotPackages.Tests.TestDoubles;

/// <summary>Hand-written stub for <see cref="IAiAgent"/> returning a fixed inference string.</summary>
public sealed class StubAiInference : IAiAgent
{
    public const string FixedResponse = "stub inference result";

    public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IAiSession>(new StubAiSession());

    private sealed class StubAiSession : IAiSession
    {
        public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(FixedResponse);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
