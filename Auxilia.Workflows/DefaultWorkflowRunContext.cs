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
        _clientTask = RabbitMqClient.CreateAsync(uri.Host, uri.Port);
        ExitService = new DefaultProcessExitService();
        Logger = NullLogger.Instance;
    }

    public IMessageBusClient MessageBus => _clientTask.GetAwaiter().GetResult();
    public IProcessExitService ExitService { get; }
    public ILogger Logger { get; }

    public async ValueTask DisposeAsync() => await (await _clientTask).DisposeAsync();

    private static Uri ParseBrokerUri(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--broker")
                return new Uri(args[i + 1]);
        return new Uri("amqp://localhost:5672");
    }
}
