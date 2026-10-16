using System.Text.Json;

namespace Auxilia.EgressGateway;

/// <summary>
/// One JSON object per line on stdout — the runner follows it live (denials are audited the
/// moment they happen) and persists the whole log with the run. Lines are self-describing so
/// the runner needs no knowledge of the gateway's internals.
/// </summary>
public sealed class EgressLog(TextWriter output)
{
    private readonly object _gate = new();

    public void Write(string evt, string? kind = null, string? host = null, int? port = null,
        string? decision = null, string? reason = null, long? bytesOut = null, long? bytesIn = null,
        long? durationMs = null)
    {
        var line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["event"] = evt,
            ["kind"] = kind,
            ["host"] = host,
            ["port"] = port,
            ["decision"] = decision,
            ["reason"] = reason,
            ["bytesOut"] = bytesOut,
            ["bytesIn"] = bytesIn,
            ["durationMs"] = durationMs,
        }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value));
        lock (_gate)
        {
            output.WriteLine(line);
            output.Flush();
        }
    }
}
