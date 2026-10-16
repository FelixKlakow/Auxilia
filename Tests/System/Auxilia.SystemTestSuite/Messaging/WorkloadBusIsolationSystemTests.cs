using Auxilia.Messaging;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.Messaging;

/// <summary>
/// A workload's broker identity against a REAL broker (the fakes enforce nothing): minted through
/// the management API with <see cref="WorkloadBusAccess"/>, it reaches exactly the platform's
/// ingress queues and its own queues — never the run-command queue, never another run's view,
/// never a platform-only message — and stops working the moment it is revoked.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class WorkloadBusIsolationSystemTests
{
    private const string RabbitMqImage = "rabbitmq:4-management";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private RabbitMqContainer _rabbitMq = null!;
    private RabbitMqClient _platform = null!;
    private RabbitMqManagementIdentityIssuer _issuer = null!;
    private readonly string _ingressQueue = $"test-ingress-{Guid.NewGuid():N}";
    private readonly string _commandQueue = $"test-run-commands-{Guid.NewGuid():N}";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _rabbitMq = new RabbitMqBuilder(RabbitMqImage)
            .WithUsername("guest")
            .WithPassword("guest")
            .WithPortBinding(15672, true)
            .Build();
        await _rabbitMq.StartAsync();
        _platform = await RabbitMqClient.CreateAsync(_rabbitMq.Hostname, _rabbitMq.GetMappedPublicPort(5672));
        _issuer = new RabbitMqManagementIdentityIssuer(new HttpClient(), new RabbitMqManagementSettings(
            new Uri($"http://{_rabbitMq.Hostname}:{_rabbitMq.GetMappedPublicPort(15672)}/"), "guest", "guest"));

        // Platform topology, exactly as the runner declares it before any launch
        // (WorkloadBusTopology) — RabbitMQ 4 refuses topic permissions on a missing exchange.
        await _platform.DeclareTopicExchangeAsync(WorkflowQueues.IngressExchangeName);
        await _platform.DeclareExchangeAsync(WorkflowQueues.StateExchangeName);
        await _platform.DeclareTopicExchangeAsync(ViewDataMessage.ExchangeName);
        await _platform.DeclareTopicExchangeAsync(WorkflowEventMessage.ExchangeName);
        await _platform.DeclareQueueAsync(_ingressQueue);
        await _platform.BindQueueAsync(_ingressQueue, WorkflowQueues.IngressExchangeName, _ingressQueue);
        await _platform.DeclareQueueAsync(_commandQueue);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_platform is not null)
            await _platform.DisposeAsync();
        if (_rabbitMq is not null)
            await _rabbitMq.DisposeAsync();
    }

    private async Task<(Guid Instance, string User, string Password)> IssueAsync()
    {
        var instance = Guid.NewGuid();
        var user = WorkloadBusIdentity.UserNameFor(instance);
        var password = await _issuer.IssueAsync(user, WorkloadBusAccess.For(instance, [_ingressQueue]));
        return (instance, user, password);
    }

    private Task<RabbitMqClient> ConnectAsync(string user, string password, bool viaIngress = true)
        => RabbitMqClient.CreateAsync(_rabbitMq.Hostname, _rabbitMq.GetMappedPublicPort(5672), user, password,
            queuePublishExchange: viaIngress ? WorkflowQueues.IngressExchangeName : null);

    [Test]
    public async Task AWorkload_ReachesTheIngress_AndIsAttributedToItsOwnInstance()
    {
        var (instance, user, password) = await IssueAsync();
        var received = new TaskCompletionSource<WorkflowSignalMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var _ = await _platform.SubscribeAsync<WorkflowSignalMessage>(_ingressQueue,
            (m, _) => { received.TrySetResult(m); return Task.CompletedTask; });
        await using var workload = await ConnectAsync(user, password);

        await workload.PublishAsync(_ingressQueue, new WorkflowSignalMessage(instance, "probe", "{}"));

        Assert.That((await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).WorkflowInstanceId, Is.EqualTo(instance));
    }

    [Test]
    public async Task AWorkload_CannotReachTheRunCommandQueue_ThroughAnyExchange()
    {
        var (_, user, password) = await IssueAsync();
        var command = new RunWorkflowCommand(Guid.NewGuid(), "evil", "docker://evil:latest", new Dictionary<string, string>());

        await using (var viaIngress = await ConnectAsync(user, password))
            Assert.CatchAsync(() => viaIngress.PublishAsync(_commandQueue, command),
                "the ingress refuses every routing key but the platform's request queues");
        await using (var viaDefault = await ConnectAsync(user, password, viaIngress: false))
            Assert.CatchAsync(() => viaDefault.PublishAsync(_commandQueue, command),
                "a workload has no write permission on the default exchange");
    }

    [Test]
    public async Task AWorkload_CannotConsumeTheRunCommandQueue()
    {
        var (_, user, password) = await IssueAsync();
        await using var workload = await ConnectAsync(user, password);

        Assert.CatchAsync(() => workload.SubscribeAsync<RunWorkflowCommand>(_commandQueue, (_, _) => Task.CompletedTask),
            "stealing other runs' commands (and their resolution tokens) must be impossible");
    }

    [Test]
    public async Task AWorkload_CannotSpeakForAnotherRun()
    {
        var (instance, user, password) = await IssueAsync();
        var victim = Guid.NewGuid();
        var received = new List<ViewDataMessage>();
        await using var _ = await _platform.SubscribeToTopicExchangeAsync<ViewDataMessage>(
            ViewDataMessage.ExchangeName, ["#"], (m, _) => { lock (received) received.Add(m); return Task.CompletedTask; });

        await using (var foreignKey = await ConnectAsync(user, password))
            Assert.CatchAsync(() => foreignKey.PublishToTopicExchangeAsync(ViewDataMessage.ExchangeName,
                    ViewDataMessage.RoutingKeyFor(victim), new ViewDataMessage(victim, "chat", 1, "{}")),
                "the broker refuses another run's routing key");

        await using var workload = await ConnectAsync(user, password);
        // Its OWN key, but a body claiming the victim: the attribution check drops it.
        await workload.PublishToTopicExchangeAsync(ViewDataMessage.ExchangeName,
            ViewDataMessage.RoutingKeyFor(instance), new ViewDataMessage(victim, "chat", 1, "{\"forged\":true}"));
        await workload.PublishToTopicExchangeAsync(ViewDataMessage.ExchangeName,
            ViewDataMessage.RoutingKeyFor(instance), new ViewDataMessage(instance, "chat", 1, "{}"));
        await Task.Delay(Settle);

        lock (received)
            Assert.That(received.Select(m => m.WorkflowInstanceId), Is.EqualTo(new[] { instance }));
    }

    [Test]
    public async Task AWorkload_CannotPublishAPlatformOnlyMessage_EvenThroughAnAllowedRoute()
    {
        var (_, user, password) = await IssueAsync();
        var received = 0;
        await using var _ = await _platform.SubscribeAsync<RunWorkflowCommand>(_ingressQueue,
            (_, _) => { Interlocked.Increment(ref received); return Task.CompletedTask; });
        await using var workload = await ConnectAsync(user, password);

        await workload.PublishAsync(_ingressQueue,
            new RunWorkflowCommand(Guid.NewGuid(), "evil", "docker://evil:latest", new Dictionary<string, string>()));
        await Task.Delay(Settle);

        Assert.That(received, Is.Zero, "a command published by a workload identity is never acted on");
    }

    [Test]
    public async Task ARevokedIdentity_CannotConnect()
    {
        var (_, user, password) = await IssueAsync();
        await _issuer.RevokeAsync(user);

        Assert.CatchAsync(() => ConnectAsync(user, password));
    }
}
