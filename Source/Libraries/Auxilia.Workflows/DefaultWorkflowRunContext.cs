using Auxilia.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Workflows;

public sealed class DefaultWorkflowRunContext : IWorkflowRunContext, IAsyncDisposable
{
    private readonly Task<RabbitMqClient> _clientTask;

    public DefaultWorkflowRunContext(string[] args)
    {
        var uri = ParseBrokerUri(args);
        var user = uri.UserInfo.Split(':') is [var u, ..] ? u : "guest";
        var pass = uri.UserInfo.Split(':') is [_, var p] ? p : "guest";
        _clientTask = RabbitMqClient.CreateAsync(uri.Host, uri.Port == -1 ? 5672 : uri.Port, user, pass);
        ExitService = new DefaultProcessExitService();
        Logger = NullLogger.Instance;
    }

    public IMessageBusClient MessageBus => _clientTask.GetAwaiter().GetResult();
    public IProcessExitService ExitService { get; }
    public ILogger Logger { get; }

    public async ValueTask DisposeAsync() => await (await _clientTask).DisposeAsync();

    private static Uri ParseBrokerUri(string[] args)
    {
        // 1. Explicit --broker <uri> argument takes precedence.
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--broker")
                return new Uri(args[i + 1]);

        // 2. Env vars injected by WorkflowDispatcher (RabbitMq__Host etc.)
        var host = System.Environment.GetEnvironmentVariable("RabbitMq__Host");
        if (!string.IsNullOrWhiteSpace(host))
        {
            var port = System.Environment.GetEnvironmentVariable("RabbitMq__Port") ?? "5672";
            var user = System.Environment.GetEnvironmentVariable("RabbitMq__UserName") ?? "guest";
            var pass = System.Environment.GetEnvironmentVariable("RabbitMq__Password") ?? "guest";
            return new Uri($"amqp://{user}:{pass}@{host}:{port}");
        }

        // 3. Local default.
        return new Uri("amqp://localhost:5672");
    }
}
