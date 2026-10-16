namespace Auxilia.EgressGateway;

/// <summary>One fixed relay: <c>&lt;listenPort&gt;=&lt;host&gt;:&lt;port&gt;[/&lt;kind&gt;]</c>.</summary>
public sealed record ForwardSpec(int ListenPort, string Host, int Port, string Kind)
{
    public static IReadOnlyList<ForwardSpec> ParseAll(string? value)
    {
        var specs = new List<ForwardSpec>();
        foreach (var raw in (value ?? string.Empty).Split(
                     ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (spec, kind) = raw.Split('/', 2) is [var s, var k] ? (s, k) : (raw, "forward");
            if (spec.Split('=', 2) is not [var listen, var target]
                || !int.TryParse(listen, out var listenPort)
                || target.LastIndexOf(':') is var colon && colon <= 0
                || !int.TryParse(target[(colon + 1)..], out var port))
                throw new FormatException($"invalid EGRESS_FORWARDS entry '{raw}'");
            specs.Add(new ForwardSpec(listenPort, target[..colon], port, kind));
        }
        return specs;
    }

    public override string ToString() => $"{ListenPort}={Host}:{Port}/{Kind}";
}
