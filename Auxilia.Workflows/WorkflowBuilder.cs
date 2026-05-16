using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Auxilia.Messaging;
using Auxilia.Workflows.Capabilities;
using Auxilia.Workflows.Environment;
using Auxilia.Workflows.Internal;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Workflows;

public sealed class WorkflowBuilder : IWorkflowBuilder
{
    private readonly string _workflowName;
    private readonly List<SlotDefinition> _slots = new();
    private readonly List<IEnvironmentRequirement> _environmentRequirements = new();
    private readonly WorkflowMetadata _metadata = new();

    private WorkflowBuilder(string workflowName)
    {
        _workflowName = workflowName;
    }

    public static IWorkflowBuilder Create(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new WorkflowBuilder(name);
    }

    public IWorkflowBuilder Requires<T>(string name, T capabilities, string? description = null)
        where T : ICapability
    {
        if (_slots.Any(s => s.SlotName == name))
            throw new InvalidOperationException($"A slot with name '{name}' has already been declared.");
        _slots.Add(new SlotDefinition(name, capabilities, description));
        return this;
    }

    public IWorkflowBuilder RequiresEnvironment(Action<IEnvironmentBuilder> configure)
    {
        var builder = new EnvironmentBuilder();
        configure(builder);
        _environmentRequirements.AddRange(builder.Requirements);
        return this;
    }

    public IWorkflowBuilder WithMetadata(Action<WorkflowMetadata> configure)
    {
        configure(_metadata);
        return this;
    }

    public Task Run(string[] args)
        => RunAsync(args, Console.Out, System.Environment.Exit);

    public async Task Run(
        string[] args,
        IMessageBusClient messageBus,
        IServiceCollection services,
        ILogger? logger = null,
        IProcessExitService? exitService = null)
    {
        var log = logger ?? NullLogger.Instance;
        var exit = exitService ?? new DefaultProcessExitService();

        var timeoutSeconds = 30;
        var envTimeout = System.Environment.GetEnvironmentVariable("WORKFLOW_CONFIG_TIMEOUT_SECONDS");
        if (!string.IsNullOrEmpty(envTimeout) && int.TryParse(envTimeout, out var parsed))
            timeoutSeconds = parsed;

        using var keyPair = new Crypto.EphemeralKeyPair();
        var instanceId = Guid.NewGuid();
        var responseTopic = $"workflow-response-{instanceId}";

        await messageBus.DeclareQueueAsync(responseTopic);

        var tcs = new TaskCompletionSource<Messaging.Messages.WorkflowConfigurationResponse>();
        var subscription = await messageBus.SubscribeAsync<Messaging.Messages.WorkflowConfigurationResponse>(
            responseTopic,
            (msg, _) => { tcs.TrySetResult(msg); return Task.CompletedTask; });

        await messageBus.PublishAsync(
            "workflow-registration",
            new WorkflowRegistrationRequest(instanceId, BuildManifest(instanceId), keyPair.PublicKeyBase64, responseTopic));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));

        if (completed != tcs.Task || tcs.Task.Result.Success == false)
        {
            if (completed != tcs.Task)
                log.LogError("Timed out waiting for workflow configuration response after {Timeout}s.", timeoutSeconds);
            else
                log.LogError("Received workflow configuration response with Success=false: {Error}", tcs.Task.Result.ErrorMessage);

            await subscription.DisposeAsync();
            exit.Exit(1);
            return;
        }

        var response = tcs.Task.Result;
        var bootstrapper = new WorkflowBootstrapper(response, keyPair);
        bootstrapper.Apply(services);

        await subscription.DisposeAsync();
    }

    internal Task RunAsync(string[] args, TextWriter output, Action<int> exit)
    {
        var schema = BuildSchema();
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        output.Write(JsonSerializer.Serialize(schema, options));
        exit(0);
        return Task.CompletedTask;
    }

    private WorkflowManifest BuildManifest(Guid instanceId = default)
        => new(_workflowName, instanceId.ToString("D"), _slots.AsReadOnly(), _environmentRequirements.AsReadOnly());

    private WorkflowSchema BuildSchema()
        => new(_workflowName, _slots.AsReadOnly(), _environmentRequirements.AsReadOnly());
}
