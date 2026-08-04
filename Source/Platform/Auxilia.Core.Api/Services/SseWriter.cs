using System.Threading.Channels;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Shared SSE write loop for the run and artifact streams: serializes events as
/// <c>data:</c> frames and emits <c>: ping</c> comment lines after keepalive-quiet periods so
/// clients (and intermediaries) can distinguish an idle stream from a dead connection.
/// </summary>
public static class SseWriter
{
    public static async Task WriteEventAsync<T>(HttpResponse response, T evt, CancellationToken ct)
    {
        await response.WriteAsync(
            $"data: {System.Text.Json.JsonSerializer.Serialize(evt, System.Text.Json.JsonSerializerOptions.Web)}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// Pumps <paramref name="reader"/> to the response until the channel completes,
    /// <paramref name="isTerminal"/> matches an event, or the client disconnects. A keepalive of
    /// zero/negative disables the pings.
    /// </summary>
    public static async Task PumpAsync<T>(
        HttpResponse response, ChannelReader<T> reader, TimeSpan keepalive,
        Func<T, bool> isTerminal, CancellationToken ct)
    {
        Task<bool>? pendingRead = null;
        while (true)
        {
            pendingRead ??= reader.WaitToReadAsync(ct).AsTask();
            if (keepalive > TimeSpan.Zero)
            {
                var winner = await Task.WhenAny(pendingRead, Task.Delay(keepalive, ct));
                if (winner != pendingRead)
                {
                    await response.WriteAsync(": ping\n\n", ct);
                    await response.Body.FlushAsync(ct);
                    continue;
                }
            }

            var hasData = await pendingRead;
            pendingRead = null;
            if (!hasData)
                return;
            while (reader.TryRead(out var evt))
            {
                await WriteEventAsync(response, evt, ct);
                if (isTerminal(evt))
                    return;
            }
        }
    }
}
