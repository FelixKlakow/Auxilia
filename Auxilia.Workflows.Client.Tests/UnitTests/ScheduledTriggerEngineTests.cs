using Auxilia.Workflows.Client.Triggers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Auxilia.Workflows.Client.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class ScheduledTriggerEngineTests
{
    private FakeCoreClient _core = null!;
    private InMemoryTriggerStore _store = null!;
    private FakeTimeProvider _time = null!;
    private ScheduledTriggerEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _core = new FakeCoreClient();
        _store = new InMemoryTriggerStore();
        _time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-01T10:00:00Z"));
        _engine = new ScheduledTriggerEngine(_store, _core, _time,
            Options.Create(new WorkflowClientOptions()),
            NullLogger<ScheduledTriggerEngine>.Instance);
    }

    [TearDown]
    public void TearDown() => _engine.Dispose();

    [Test]
    public async Task DueConfigurationTrigger_DispatchesOnBehalfOf_AndStampsLastDispatch()
    {
        var configId = Guid.NewGuid();
        var principal = Guid.NewGuid();
        await _store.SaveAsync(new ScheduledTriggerDefinition
        {
            IntervalSeconds = 60,
            ConfigurationId = configId,
            RunAsPrincipalId = principal,
            Context = new Dictionary<string, string> { ["source"] = "schedule" }
        });

        await _engine.TickAsync();

        var (dispatchedConfig, onBehalfOf, context) = _core.ConfigurationRuns.Single();
        Assert.Multiple(() =>
        {
            Assert.That(dispatchedConfig, Is.EqualTo(configId));
            Assert.That(onBehalfOf, Is.EqualTo(principal));
            Assert.That(context!["source"], Is.EqualTo("schedule"));
        });
        var stored = (await _store.GetScheduledTriggersAsync()).Single();
        Assert.That(stored.LastDispatchedUtc, Is.EqualTo(_time.GetUtcNow()));
    }

    [Test]
    public async Task InlineTypeTrigger_UsesTheAdHocRunPath()
    {
        await _store.SaveAsync(new ScheduledTriggerDefinition
        {
            IntervalSeconds = 60, WorkflowType = "nightly-cleanup"
        });

        await _engine.TickAsync();

        Assert.That(_core.InlineRuns.Single().WorkflowType, Is.EqualTo("nightly-cleanup"));
    }

    [Test]
    public async Task NotYetDue_AndDisabled_DoNotDispatch()
    {
        await _store.SaveAsync(new ScheduledTriggerDefinition
        {
            IntervalSeconds = 3600, ConfigurationId = Guid.NewGuid(),
            LastDispatchedUtc = _time.GetUtcNow().AddSeconds(-10)
        });
        await _store.SaveAsync(new ScheduledTriggerDefinition
        {
            IntervalSeconds = 1, ConfigurationId = Guid.NewGuid(), Enabled = false
        });

        await _engine.TickAsync();

        Assert.That(_core.ConfigurationRuns, Is.Empty);
    }

    [Test]
    public async Task ElapsedInterval_DispatchesAgain()
    {
        await _store.SaveAsync(new ScheduledTriggerDefinition
        {
            IntervalSeconds = 60, ConfigurationId = Guid.NewGuid()
        });

        await _engine.TickAsync();
        _time.Advance(TimeSpan.FromSeconds(59));
        await _engine.TickAsync();
        Assert.That(_core.ConfigurationRuns, Has.Count.EqualTo(1), "59s < 60s interval");

        _time.Advance(TimeSpan.FromSeconds(1));
        await _engine.TickAsync();
        Assert.That(_core.ConfigurationRuns, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task OneFailingTrigger_DoesNotStopTheSweep_AndRetriesNextTick()
    {
        await _store.SaveAsync(new ScheduledTriggerDefinition
        {
            IntervalSeconds = 60, ConfigurationId = Guid.NewGuid()
        });
        _core.DispatchError = new InvalidOperationException("core unreachable");

        await _engine.TickAsync();
        Assert.That((await _store.GetScheduledTriggersAsync()).Single().LastDispatchedUtc, Is.Null,
            "a failed dispatch must NOT stamp LastDispatchedUtc — it retries next tick");

        _core.DispatchError = null;
        await _engine.TickAsync();
        Assert.That(_core.ConfigurationRuns, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task HostedLoop_TicksOnTheTimer()
    {
        await _store.SaveAsync(new ScheduledTriggerDefinition
        {
            IntervalSeconds = 1, ConfigurationId = Guid.NewGuid()
        });

        await _engine.StartAsync(CancellationToken.None);
        try
        {
            // The loop waits on a FakeTimeProvider PeriodicTimer — advance to fire ticks.
            for (var i = 0; i < 50 && _core.ConfigurationRuns.IsEmpty; i++)
            {
                _time.Advance(TimeSpan.FromSeconds(10));
                await Task.Delay(10);
            }
            Assert.That(_core.ConfigurationRuns, Is.Not.Empty);
        }
        finally
        {
            await _engine.StopAsync(CancellationToken.None);
        }
    }
}
