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
    public async Task TimedOutDispatch_IsLoggedAndRetried_NotTreatedAsShutdown()
    {
        await _store.SaveAsync(new ScheduledTriggerDefinition
        {
            IntervalSeconds = 60, ConfigurationId = Guid.NewGuid()
        });
        // The Core client's watchdog / HttpClient.Timeout shape — not the engine's own token.
        _core.DispatchError = new TaskCanceledException("Core call timed out");

        Assert.DoesNotThrowAsync(() => _engine.TickAsync());
        Assert.That((await _store.GetScheduledTriggersAsync()).Single().LastDispatchedUtc, Is.Null);

        _core.DispatchError = null;
        await _engine.TickAsync();
        Assert.That(_core.ConfigurationRuns, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task HostedLoop_SurvivesAFailingStoreRead_AndKeepsTicking()
    {
        var flaky = new FlakyStore(_store);
        _engine.Dispose();
        _engine = new ScheduledTriggerEngine(flaky, _core, _time,
            Options.Create(new WorkflowClientOptions()),
            NullLogger<ScheduledTriggerEngine>.Instance);
        await _store.SaveAsync(new ScheduledTriggerDefinition
        {
            IntervalSeconds = 1, ConfigurationId = Guid.NewGuid()
        });
        flaky.FailNextReads = 2; // the first ticks fail OUTSIDE the per-trigger guard

        await _engine.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 50 && _core.ConfigurationRuns.IsEmpty; i++)
            {
                _time.Advance(TimeSpan.FromSeconds(10));
                await Task.Delay(10);
            }
            Assert.That(flaky.FailNextReads, Is.Zero, "the failing reads happened");
            Assert.That(_core.ConfigurationRuns, Is.Not.Empty,
                "a failing sweep is logged and retried — it must not end the scheduler for the process lifetime");
        }
        finally
        {
            await _engine.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Delegating store whose scheduled-trigger read fails a configurable number of times.</summary>
    private sealed class FlakyStore(ITriggerStore inner) : ITriggerStore
    {
        public int FailNextReads { get; set; }

        public Task<IReadOnlyList<ScheduledTriggerDefinition>> GetScheduledTriggersAsync(CancellationToken ct = default)
        {
            if (FailNextReads > 0)
            {
                FailNextReads--;
                throw new TimeoutException("store unreachable");
            }
            return inner.GetScheduledTriggersAsync(ct);
        }

        public Task<IReadOnlyList<ArtifactTriggerDefinition>> GetArtifactTriggersAsync(CancellationToken ct = default) => inner.GetArtifactTriggersAsync(ct);
        public Task<IReadOnlyList<EventTriggerDefinition>> GetEventTriggersAsync(CancellationToken ct = default) => inner.GetEventTriggersAsync(ct);
        public Task SaveAsync(ScheduledTriggerDefinition trigger, CancellationToken ct = default) => inner.SaveAsync(trigger, ct);
        public Task SaveAsync(ArtifactTriggerDefinition trigger, CancellationToken ct = default) => inner.SaveAsync(trigger, ct);
        public Task SaveAsync(EventTriggerDefinition trigger, CancellationToken ct = default) => inner.SaveAsync(trigger, ct);
        public Task DeleteScheduledTriggerAsync(Guid id, CancellationToken ct = default) => inner.DeleteScheduledTriggerAsync(id, ct);
        public Task DeleteArtifactTriggerAsync(Guid id, CancellationToken ct = default) => inner.DeleteArtifactTriggerAsync(id, ct);
        public Task DeleteEventTriggerAsync(Guid id, CancellationToken ct = default) => inner.DeleteEventTriggerAsync(id, ct);
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
