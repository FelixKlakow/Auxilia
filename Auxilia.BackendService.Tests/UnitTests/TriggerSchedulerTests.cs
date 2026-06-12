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
public class TriggerSchedulerTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingBus : IMessageBusClient
    {
        public List<(string Topic, object Message)> Published { get; } = [];
        public Task DeclareQueueAsync(string q, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclareExchangeAsync(string e, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishAsync<T>(string topic, T message, CancellationToken ct = default)
        {
            Published.Add((topic, message!));
            return Task.CompletedTask;
        }
        public Task PublishToExchangeAsync<T>(string e, T m, CancellationToken ct = default) => PublishAsync(e, m, ct);
        public Task<IAsyncDisposable> SubscribeAsync<T>(string q, Func<T, CancellationToken, Task> h, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable>(new Noop());
        public Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(string e, Func<T, CancellationToken, Task> h, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable>(new Noop());
        private sealed class Noop : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private ManualTimeProvider _time = null!;
    private RecordingBus _bus = null!;
    private IDataAccess<ScheduledTriggerRecord> _triggers = null!;
    private TriggerScheduler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider();
        _bus = new RecordingBus();
        _triggers = new InMemoryDataAccess<ScheduledTriggerRecord>();
        _sut = new TriggerScheduler(
            _triggers, _bus,
            new AuditLog(new InMemoryDataAccess<AuditRecord>(), _time),
            _time,
            Options.Create(new PlatformHostSettings()),
            NullLogger<TriggerScheduler>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _sut.Dispose();
        (_triggers as IDisposable)?.Dispose();
    }

    private Task SeedTriggerAsync(int intervalSeconds = 60, bool enabled = true,
        DateTimeOffset? lastDispatched = null, Guid? runAs = null)
        => _triggers.SaveAsync(new ScheduledTriggerRecord
        {
            Id = ScheduledTriggerRecord.IdFor("nightly-scan"),
            WorkflowType = "security-scan",
            WorkflowPackageUri = "docker://scan:1",
            IntervalSeconds = intervalSeconds,
            Enabled = enabled,
            LastDispatchedUtc = lastDispatched,
            RunAsPrincipalId = runAs
        });

    [Test]
    public async Task NeverDispatchedTrigger_IsDispatched_WithRunAsPrincipal()
    {
        var principal = Guid.NewGuid();
        await SeedTriggerAsync(runAs: principal);

        await _sut.DispatchDueTriggersAsync(CancellationToken.None);

        var command = _bus.Published.Select(p => p.Message).OfType<RunWorkflowCommand>().Single();
        Assert.Multiple(async () =>
        {
            Assert.That(command.WorkflowType, Is.EqualTo("security-scan"));
            Assert.That(command.RequestedBy, Is.EqualTo(principal));
            var stored = await _triggers.ReadAsync(ScheduledTriggerRecord.IdFor("nightly-scan"));
            Assert.That(stored!.LastDispatchedUtc, Is.EqualTo(_time.Now));
        });
    }

    [Test]
    public async Task TriggerNotYetDue_IsNotDispatched()
    {
        await SeedTriggerAsync(intervalSeconds: 60, lastDispatched: _time.Now - TimeSpan.FromSeconds(30));

        await _sut.DispatchDueTriggersAsync(CancellationToken.None);

        Assert.That(_bus.Published, Is.Empty);
    }

    [Test]
    public async Task DueTrigger_IsDispatchedAgainAfterInterval()
    {
        await SeedTriggerAsync(intervalSeconds: 60, lastDispatched: _time.Now - TimeSpan.FromSeconds(61));

        await _sut.DispatchDueTriggersAsync(CancellationToken.None);

        Assert.That(_bus.Published.Select(p => p.Message).OfType<RunWorkflowCommand>().Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task DisabledTrigger_IsNeverDispatched()
    {
        await SeedTriggerAsync(enabled: false);

        await _sut.DispatchDueTriggersAsync(CancellationToken.None);

        Assert.That(_bus.Published, Is.Empty);
    }
}
