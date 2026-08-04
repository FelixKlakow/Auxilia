using System.Net;
using System.Text;

namespace Auxilia.Core.Client.Tests;

/// <summary>How a scripted SSE connection ends after serving its lines.</summary>
public enum SseConnectionEnd
{
    OrderlyClose,
    ThrowMidStream,
    HangSilently
}

/// <summary>One scripted connection: status code, raw SSE lines, and how it ends.</summary>
public sealed record SseConnection(
    HttpStatusCode StatusCode,
    IReadOnlyList<string>? Lines = null,
    SseConnectionEnd End = SseConnectionEnd.OrderlyClose,
    string? ErrorBody = null,
    TimeSpan? ResponseDelay = null);

/// <summary>
/// Deterministic fake transport for the resilient-stream tests: each SendAsync dequeues the next
/// scripted connection. Running out of scripts throws — a test that reconnects more often than
/// scripted fails loudly instead of hanging.
/// </summary>
public sealed class SseScriptHandler : HttpMessageHandler
{
    private readonly Queue<SseConnection> _script = new();

    public int ConnectionsServed { get; private set; }
    public List<Uri> Requests { get; } = [];

    public SseScriptHandler Enqueue(SseConnection connection)
    {
        _script.Enqueue(connection);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        if (_script.Count == 0)
            throw new InvalidOperationException(
                $"No scripted connection left for {request.RequestUri} (served {ConnectionsServed}).");
        var connection = _script.Dequeue();
        ConnectionsServed++;

        if (connection.ResponseDelay is { } delay)
            await Task.Delay(delay, cancellationToken);

        if (!IsSuccess(connection.StatusCode))
            return new HttpResponseMessage(connection.StatusCode)
            {
                Content = new StringContent(
                    connection.ErrorBody ?? $$"""{"error":"scripted {{(int)connection.StatusCode}}"}""",
                    Encoding.UTF8, "application/json")
            };

        return new HttpResponseMessage(connection.StatusCode)
        {
            Content = new StreamContent(new ScriptedStream(connection))
        };
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    /// <summary>Serves the scripted lines, then closes / throws / hangs per the script.</summary>
    private sealed class ScriptedStream(SseConnection connection) : Stream
    {
        private readonly byte[] _payload = Encoding.UTF8.GetBytes(
            connection.Lines is { Count: > 0 } lines ? string.Join("\n", lines) + "\n" : "");
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position < _payload.Length)
            {
                var count = Math.Min(buffer.Length, _payload.Length - _position);
                _payload.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            switch (connection.End)
            {
                case SseConnectionEnd.OrderlyClose:
                    return 0;
                case SseConnectionEnd.ThrowMidStream:
                    throw new IOException("scripted mid-stream drop");
                default:
                    await Task.WhenAny(Task.Delay(Timeout.Infinite, ct), _disposed.Task);
                    ct.ThrowIfCancellationRequested();
                    throw new IOException("scripted hang ended by disposal");
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            _disposed.TrySetResult();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
