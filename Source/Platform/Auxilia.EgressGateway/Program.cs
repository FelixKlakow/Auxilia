using System.Net;
using Auxilia.EgressGateway;

// Configuration is data handed over by the runner (no knowledge of providers or workflows):
//   EGRESS_ALLOW     the run's effective allowlist (see EgressPolicy), "*" for allow-all
//   EGRESS_FORWARDS  fixed relays: "<listenPort>=<host>:<port>[/<kind>]", comma-separated — kinds "amqp"
//                    (the broker, identity-pinned), "terminal" (the run's terminal), "tcp" (a declared
//                    endpoint: its addresses obey the same special-purpose rules as the proxy)
//   EGRESS_PORT      proxy listen port (default 3128)
//   EGRESS_AMQP_USER the only broker identity the relays of kind "amqp" admit (the run's own)
//   EGRESS_PROTECTED platform endpoints refused before any allow entry (host[:port], comma-separated)
var log = new EgressLog(Console.Out);
var policy = EgressPolicy.Parse(
    Environment.GetEnvironmentVariable("EGRESS_ALLOW"), Environment.GetEnvironmentVariable("EGRESS_PROTECTED"));
var proxyPort = int.TryParse(Environment.GetEnvironmentVariable("EGRESS_PORT"), out var configured) ? configured : 3128;

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stopping.Cancel();

var proxy = new EgressProxy(policy, log, new IPEndPoint(IPAddress.Any, proxyPort));
proxy.Start();
var tasks = new List<Task> { proxy.RunAsync(stopping.Token) };
foreach (var forward in ForwardSpec.ParseAll(Environment.GetEnvironmentVariable("EGRESS_FORWARDS")))
{
    var relay = new TcpForward(forward.ListenPort, forward.Host, forward.Port, forward.Kind, log,
        forward.Kind == "amqp" ? Environment.GetEnvironmentVariable("EGRESS_AMQP_USER") ?? "" : null,
        forward.Kind == "tcp" ? policy : null);
    relay.Start();
    tasks.Add(relay.RunAsync(stopping.Token));
}

log.Write("listening", "proxy", port: proxyPort, decision: policy.AllowAll ? "allow-all" : "allowlist");
await Task.WhenAll(tasks);
