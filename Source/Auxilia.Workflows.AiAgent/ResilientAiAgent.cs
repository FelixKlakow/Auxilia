namespace Auxilia.Workflows.AiAgent;

/// <summary>
/// Decorator that wraps <see cref="IAiAgent"/> with bounded exponential-backoff retry.
/// On transient failures, <see cref="OpenSessionAsync"/> retries up to
/// <see cref="AiResilienceOptions.MaxAttempts"/> times before re-throwing the last exception.
/// When <see cref="AiResilienceOptions.RetryExecute"/> is <c>true</c>, the returned
/// <see cref="IAiSession"/> also retries transient <see cref="IAiSession.ExecuteAsync"/> calls.
/// </summary>
public sealed class ResilientAiAgent : IAiAgent
{
    private readonly IAiAgent _inner;
    private readonly AiResilienceOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>
    /// Initialises a new <see cref="ResilientAiAgent"/> with real <see cref="Task.Delay"/> back-off.
    /// </summary>
    public ResilientAiAgent(IAiAgent inner, AiResilienceOptions? options = null)
        : this(inner, options ?? new AiResilienceOptions(), Task.Delay) { }

    /// <summary>
    /// Initialises a new <see cref="ResilientAiAgent"/> with a custom delay strategy.
    /// Intended for testing — inject a no-op or recording delegate to avoid real waits.
    /// </summary>
    internal ResilientAiAgent(IAiAgent inner, AiResilienceOptions options, Func<TimeSpan, CancellationToken, Task> delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttempts, 1);
        _inner = inner;
        _options = options;
        _delay = delay;
    }

    /// <inheritdoc />
    public async Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;
        var backoff = TimeSpan.FromMilliseconds(_options.InitialBackoffMs);

        for (int attempt = 0; attempt < _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var session = await _inner.OpenSessionAsync(options, cancellationToken);
                return _options.RetryExecute
                    ? new ResilientAiSession(session, _options, _delay)
                    : session;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt < _options.MaxAttempts - 1)
                {
                    await _delay(backoff, cancellationToken);
                    backoff = TimeSpan.FromMilliseconds(backoff.TotalMilliseconds * 2);
                }
            }
        }

        throw lastException!;
    }

    private sealed class ResilientAiSession : IAiSession
    {
        private readonly IAiSession _inner;
        private readonly AiResilienceOptions _options;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        internal ResilientAiSession(IAiSession inner, AiResilienceOptions options, Func<TimeSpan, CancellationToken, Task> delay)
        {
            _inner = inner;
            _options = options;
            _delay = delay;
        }

        public async Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            Exception? lastException = null;
            var backoff = TimeSpan.FromMilliseconds(_options.InitialBackoffMs);

            for (int attempt = 0; attempt < _options.MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await _inner.ExecuteAsync(prompt, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    if (attempt < _options.MaxAttempts - 1)
                    {
                        await _delay(backoff, cancellationToken);
                        backoff = TimeSpan.FromMilliseconds(backoff.TotalMilliseconds * 2);
                    }
                }
            }

            throw lastException!;
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
