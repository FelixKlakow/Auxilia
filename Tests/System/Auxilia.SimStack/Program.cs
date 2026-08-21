using System.Net;
using System.Net.Sockets;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

// The simulated distributed system of the run-pod scenario test (design doc §A worked
// example): one image, three roles selected by ROLE —
//   machine        a simulated hardware machine: TCP PING→PONG responder
//   fleet-manager  central piece A: manages machines (declared + workflow-added), pings
//                  them, publishes the availability list over the pod's RabbitMQ
//   coordinator    central piece B: consumes availability from RabbitMQ, answers TCP
//                  LIST queries with the machines it currently knows to be available
// Every role appends to a log file on the shared pod volume — the material the workflow
// zips into its logs.zip artifact.

var role = Environment.GetEnvironmentVariable("ROLE")
           ?? throw new InvalidOperationException("ROLE is required (machine|fleet-manager|coordinator)");
var logDir = Environment.GetEnvironmentVariable("LOG_DIR") ?? "/var/log/app";
Directory.CreateDirectory(logDir);
var logName = role == "machine" ? $"machine-{Dns.GetHostName()}" : role;
var logPath = Path.Combine(logDir, $"{logName}.log");
var logLock = new object();

void Log(string message)
{
    var line = $"{DateTime.UtcNow:O} [{logName}] {message}";
    Console.WriteLine(line);
    lock (logLock)
        File.AppendAllText(logPath, line + Environment.NewLine);
}

Log($"starting as {role}");

switch (role)
{
    case "machine":
        await RunMachineAsync();
        break;
    case "fleet-manager":
        await RunFleetManagerAsync();
        break;
    case "coordinator":
        await RunCoordinatorAsync();
        break;
    default:
        throw new InvalidOperationException($"Unknown ROLE '{role}'");
}

return;

async Task RunMachineAsync()
{
    var port = int.Parse(Environment.GetEnvironmentVariable("MACHINE_PORT") ?? "9000");
    var listener = new TcpListener(IPAddress.Any, port);
    listener.Start();
    Log($"listening on {port}");
    while (true)
    {
        var client = await listener.AcceptTcpClientAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                using var c = client;
                using var stream = c.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                if (await reader.ReadLineAsync() == "PING")
                {
                    await writer.WriteLineAsync($"PONG {Dns.GetHostName()}");
                    Log("answered PING");
                }
            }
            catch (Exception ex)
            {
                Log($"connection error: {ex.Message}");
            }
        });
    }
}

async Task RunFleetManagerAsync()
{
    var machines = new HashSet<string>(
        (Environment.GetEnvironmentVariable("MACHINES") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    var machinesLock = new object();
    Log($"managing {machines.Count} declared machine(s)");

    // Control surface: the workflow (the test case's manager) adds/removes machines here.
    var controlPort = int.Parse(Environment.GetEnvironmentVariable("CONTROL_PORT") ?? "8080");
    var control = new TcpListener(IPAddress.Any, controlPort);
    control.Start();
    _ = Task.Run(async () =>
    {
        while (true)
        {
            var client = await control.AcceptTcpClientAsync();
            _ = Task.Run(async () =>
            {
                try
                {
                    using var c = client;
                    using var stream = c.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    await using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                    if (await reader.ReadLineAsync() is not { } line)
                        return;
                    var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
                    lock (machinesLock)
                    {
                        switch (parts)
                        {
                            case ["ADD", var endpoint]: machines.Add(endpoint); break;
                            case ["REMOVE", var endpoint]: machines.Remove(endpoint); break;
                        }
                    }
                    Log($"control: {line}");
                    await writer.WriteLineAsync("OK");
                }
                catch (Exception ex)
                {
                    Log($"control error: {ex.Message}");
                }
            });
        }
    });

    var channel = await ConnectBusAsync();
    while (true)
    {
        string[] snapshot;
        lock (machinesLock)
            snapshot = machines.ToArray();
        var available = new List<string>();
        foreach (var endpoint in snapshot)
            if (await ProbeMachineAsync(endpoint))
                available.Add(endpoint);
        available.Sort(StringComparer.Ordinal);
        await channel.BasicPublishAsync(
            "", "availability", Encoding.UTF8.GetBytes(string.Join(',', available)));
        Log($"availability: {available.Count}/{snapshot.Length}");
        await Task.Delay(500);
    }
}

async Task<bool> ProbeMachineAsync(string endpoint)
{
    try
    {
        var parts = endpoint.Split(':');
        using var client = new TcpClient();
        await client.ConnectAsync(parts[0], int.Parse(parts[1]))
            .WaitAsync(TimeSpan.FromMilliseconds(750));
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        await writer.WriteLineAsync("PING");
        var reply = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds(750));
        return reply?.StartsWith("PONG", StringComparison.Ordinal) == true;
    }
    catch
    {
        return false;
    }
}

async Task RunCoordinatorAsync()
{
    var latest = "";
    var channel = await ConnectBusAsync();
    var consumer = new AsyncEventingBasicConsumer(channel);
    consumer.ReceivedAsync += (_, ea) =>
    {
        latest = Encoding.UTF8.GetString(ea.Body.Span);
        return Task.CompletedTask;
    };
    await channel.BasicConsumeAsync("availability", autoAck: true, consumer);
    Log("consuming availability");

    var queryPort = int.Parse(Environment.GetEnvironmentVariable("QUERY_PORT") ?? "7000");
    var listener = new TcpListener(IPAddress.Any, queryPort);
    listener.Start();
    Log($"query surface on {queryPort}");
    while (true)
    {
        var client = await listener.AcceptTcpClientAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                using var c = client;
                using var stream = c.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                var query = await reader.ReadLineAsync();
                if (query == "LIST")
                {
                    var current = latest;
                    var count = current.Length == 0 ? 0 : current.Split(',').Length;
                    await writer.WriteLineAsync($"{count}|{current}");
                    Log($"answered LIST: {count}");
                }
                // Egress prober: the workflow asks the coordinator — a pod-internal
                // container — whether it can open a TCP connection to a target. On the
                // --internal pod network everything outside the pod must answer CLOSED.
                else if (query?.StartsWith("PROBE ", StringComparison.Ordinal) == true)
                {
                    var target = query["PROBE ".Length..];
                    var answer = await ProbeTargetAsync(target);
                    await writer.WriteLineAsync(answer);
                    Log($"probed {target}: {answer}");
                }
            }
            catch (Exception ex)
            {
                Log($"query error: {ex.Message}");
            }
        });
    }
}

async Task<string> ProbeTargetAsync(string target)
{
    try
    {
        var parts = target.Split(':');
        using var client = new TcpClient();
        await client.ConnectAsync(parts[0], int.Parse(parts[1]))
            .WaitAsync(TimeSpan.FromSeconds(3));
        return "OPEN";
    }
    catch (Exception ex)
    {
        return $"CLOSED {(ex as AggregateException)?.InnerException?.GetType().Name ?? ex.GetType().Name}";
    }
}

async Task<IChannel> ConnectBusAsync()
{
    var busUri = Environment.GetEnvironmentVariable("BUS_URI")
                 ?? throw new InvalidOperationException("BUS_URI is required");
    var factory = new ConnectionFactory { Uri = new Uri(busUri) };
    // The pod's rabbit counts as "ready" once its container runs — AMQP needs longer.
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            var connection = await factory.CreateConnectionAsync();
            var channel = await connection.CreateChannelAsync();
            await channel.QueueDeclareAsync(
                "availability", durable: false, exclusive: false, autoDelete: false);
            Log("bus connected");
            return channel;
        }
        catch (Exception ex) when (attempt < 120)
        {
            if (attempt % 10 == 1)
                Log($"waiting for the bus ({ex.GetType().Name})");
            await Task.Delay(1000);
        }
    }
}
