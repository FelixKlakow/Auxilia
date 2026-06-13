using Auxilia.BackendService.PlatformHost;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class ArtifactTriggerHandlerTests
{
    private sealed class RecordingBus : IMessageBusClient
    {
        public List<(string Topic, object Message)> Published { get; } = [];
        public Func<ArtifactPersistedEvent, CancellationToken, Task>? ArtifactHandler { get; private set; }

        public Task DeclareQueueAsync(string queueName, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclareExchangeAsync(string exchangeName, CancellationToken ct = default) => Task.CompletedTask;

        public Task PublishAsync<T>(string topic, T message, CancellationToken ct = default)
        {
            Published.Add((topic, message!));
            return Task.CompletedTask;
        }

        public Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken ct = default)
            => PublishAsync(exchangeName, message, ct);

        public Task<IAsyncDisposable> SubscribeAsync<T>(string queueName, Func<T, CancellationToken, Task> handler, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable>(new Noop());

        public Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(string exchangeName, Func<T, CancellationToken, Task> handler, CancellationToken ct = default)
        {
            if (handler is Func<ArtifactPersistedEvent, CancellationToken, Task> artifactHandler)
                ArtifactHandler = artifactHandler;
            return Task.FromResult<IAsyncDisposable>(new Noop());
        }

        private sealed class Noop : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private RecordingBus _bus = null!;
    private IDataAccess<ArtifactTriggerRecord> _triggers = null!;
    private IDataAccess<AuditRecord> _audit = null!;
    private PlatformHostSettings _settings = null!;
    private ArtifactTriggerHandler _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        _bus = new RecordingBus();
        _triggers = new InMemoryDataAccess<ArtifactTriggerRecord>();
        _audit = new InMemoryDataAccess<AuditRecord>();
        _settings = new PlatformHostSettings();
        _sut = new ArtifactTriggerHandler(
            _bus, _triggers,
            new AuditLog(_audit, TimeProvider.System),
            Options.Create(_settings),
            NullLogger<ArtifactTriggerHandler>.Instance);

        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync(CancellationToken.None);
        (_triggers as IDisposable)?.Dispose();
        (_audit as IDisposable)?.Dispose();
    }

    private Task SeedTriggerAsync(
        string artifactType = "review-result", bool enabled = true, Guid? runAsPrincipalId = null,
        Guid? workflowConfigurationId = null)
        => _triggers.SaveAsync(new ArtifactTriggerRecord
        {
            Id = ArtifactTriggerRecord.IdFor(artifactType, "follow-up-workflow"),
            ArtifactType = artifactType,
            WorkflowType = "follow-up-workflow",
            WorkflowPackageUri = "docker://follow-up-workflow:test",
            Enabled = enabled,
            RunAsPrincipalId = runAsPrincipalId,
            WorkflowConfigurationId = workflowConfigurationId
        });

    private static ArtifactPersistedEvent Persisted(string artifactType = "review-result")
        => new(Guid.NewGuid(), artifactType, "review-workflow", "WI-9",
            Guid.NewGuid(), 1, "ABCDEF", DateTimeOffset.UtcNow);

    [Test]
    public async Task MatchingEnabledTrigger_DispatchesRunWorkflowCommandWithArtifactContextAndAudits()
    {
        var principal = Guid.NewGuid();
        await SeedTriggerAsync(runAsPrincipalId: principal);
        var persisted = Persisted();

        await _bus.ArtifactHandler!(persisted, CancellationToken.None);

        var command = _bus.Published
            .Where(p => p.Topic == _settings.CommandQueueName)
            .Select(p => p.Message).OfType<RunWorkflowCommand>().SingleOrDefault();
        Assert.That(command, Is.Not.Null, "A matching enabled trigger must dispatch the follow-up workflow.");
        Assert.Multiple(() =>
        {
            Assert.That(command!.WorkflowType, Is.EqualTo("follow-up-workflow"));
            Assert.That(command.WorkflowPackageUri, Is.EqualTo("docker://follow-up-workflow:test"));
            Assert.That(command.Context["ArtifactId"], Is.EqualTo(persisted.ArtifactId.ToString("D")));
            Assert.That(command.Context["ArtifactType"], Is.EqualTo("review-result"));
            Assert.That(command.Context["WorkItemId"], Is.EqualTo("WI-9"));
            Assert.That(command.RequestedBy, Is.EqualTo(principal),
                "The chained dispatch must run as the trigger's principal.");
        });

        var auditQuery = await _audit.ReadAsync();
        Assert.That(auditQuery.Any(a => a.Action == "trigger.artifact-dispatch"), Is.True);
    }

    [Test]
    public async Task ConfigurationWiredTrigger_DispatchesByConfigurationId()
    {
        var configurationId = Guid.NewGuid();
        await SeedTriggerAsync(workflowConfigurationId: configurationId);

        await _bus.ArtifactHandler!(Persisted(), CancellationToken.None);

        var command = _bus.Published.Select(p => p.Message).OfType<RunWorkflowCommand>().Single();
        Assert.That(command.WorkflowConfigurationId, Is.EqualTo(configurationId),
            "the dispatcher must resolve type, package, and slot bindings from the configuration");
    }

    [Test]
    public async Task DisabledTrigger_DispatchesNothing()
    {
        await SeedTriggerAsync(enabled: false);

        await _bus.ArtifactHandler!(Persisted(), CancellationToken.None);

        Assert.That(_bus.Published, Is.Empty);
        var auditQuery = await _audit.ReadAsync();
        Assert.That(auditQuery, Is.Empty);
    }

    [Test]
    public async Task NonMatchingArtifactType_DispatchesNothing()
    {
        await SeedTriggerAsync(artifactType: "some-other-artifact");

        await _bus.ArtifactHandler!(Persisted(), CancellationToken.None);

        Assert.That(_bus.Published, Is.Empty);
    }
}
