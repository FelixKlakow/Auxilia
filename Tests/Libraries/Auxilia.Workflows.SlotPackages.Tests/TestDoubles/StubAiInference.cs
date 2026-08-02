using Auxilia.Workflows.AiAgent;

namespace Auxilia.Workflows.SlotPackages.Tests.TestDoubles;

/// <summary>Hand-written stub for <see cref="IAiAgent"/> returning a fixed inference string.</summary>
public sealed class StubAiInference : IAiAgent
{
    public const string FixedResponse = "stub inference result";

    public int DisposedCount { get; private set; }

    public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IAiSession>(new StubAiSession(this));

    private sealed class StubAiSession : IAiSession
    {
        private readonly StubAiInference _owner;

        public StubAiSession(StubAiInference owner) => _owner = owner;

        public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(FixedResponse);

        public Task CompactAsync(string focusDescription, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _owner.DisposedCount++;
            return ValueTask.CompletedTask;
        }
    }
}
