using System.Security.Cryptography;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class SlotActivationHandlerTests
{
    private static readonly TimeSpan CredentialLifetime = TimeSpan.FromMinutes(30);

    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private ManualTimeProvider _time = null!;
    private CapturingBus _bus = null!;
    private SlotConfigurationStore _store = null!;
    private WorkflowInstanceRegistry _instanceRegistry = null!;
    private WorkflowInstanceTokenRegistry _tokenRegistry = null!;
    private InMemoryDataAccess<AuditRecord> _auditRecords = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider();
        _bus = new CapturingBus();
        _store = TestStores.NewSlotConfigurationStore();
        _instanceRegistry = TestStores.NewWorkflowInstanceRegistry();
        _tokenRegistry = new WorkflowInstanceTokenRegistry(
            Options.Create(new WorkflowDispatcherSettings()), _time);
        _auditRecords = new InMemoryDataAccess<AuditRecord>();
    }

    [TearDown]
    public void TearDown() => _auditRecords.Dispose();

    private async Task<SlotActivationHandler> StartHandlerAsync(bool requireInstanceToken = true)
    {
        var settings = Options.Create(new WorkflowDispatcherSettings
        {
            RequireInstanceToken = requireInstanceToken,
            SlotCredentialLifetime = CredentialLifetime
        });
        var handler = new SlotActivationHandler(
            _bus,
            new ConfigurationResolver(_store, TestStores.NewSignalHandlerStore(), NullLogger<ConfigurationResolver>.Instance),
            _instanceRegistry,
            _tokenRegistry,
            new AuditLog(_auditRecords, _time),
            _time,
            settings,
            NullLogger<SlotActivationHandler>.Instance);
        await handler.StartAsync(CancellationToken.None);
        return handler;
    }

    private async Task SeedSlotAsync(string workflowType = "TestWorkflow", string slotName = "slotA")
        => await _store.UpsertConfigurationAsync(workflowType,
            new StoredSlotConfiguration(slotName, "ProviderX",
                new Dictionary<string, string> { ["ApiKey"] = "secret" },
                ConfigurationStatus.Valid));

    private async Task<List<AuditRecord>> AuditEntriesAsync(string action)
        => (await _auditRecords.ReadAsync()).Where(r => r.Action == action).ToList();

    private static string ValidPublicKey()
    {
        using var rsa = RSA.Create(2048);
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }

    [Test]
    public async Task HandleAsync_ValidTokenAndRegisteredInstance_RespondsOnCanonicalQueueWithEncryptedSlot()
    {
        var issued = _tokenRegistry.Issue("TestWorkflow");
        await _instanceRegistry.RegisterAsync(issued.WorkflowInstanceId, "TestWorkflow");
        await SeedSlotAsync();
        await StartHandlerAsync();

        await _bus.InvokeAsync(new SlotActivationRequest(
            issued.WorkflowInstanceId, "slotA", ValidPublicKey(), issued.Token), CancellationToken.None);

        Assert.That(_bus.Published, Has.Count.EqualTo(1));
        var (topic, msg) = _bus.Published[0];
        Assert.That(topic, Is.EqualTo(WorkflowQueues.ResponseQueueFor(issued.WorkflowInstanceId)));
        var response = (SlotActivationResponse)msg;
        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.ErrorMessage, Is.Null);
            Assert.That(response.SlotName, Is.EqualTo("slotA"));
            Assert.That(response.Slot, Is.Not.Null);
            Assert.That(response.Slot!.ProviderType, Is.EqualTo("ProviderX"));
            Assert.That(response.Slot.EncryptedSettings, Is.Not.Empty);
            Assert.That(response.ExpiresUtc, Is.EqualTo(_time.Now + CredentialLifetime));
        });
    }

    [Test]
    public async Task HandleAsync_SuccessfulActivation_AuditsSlotActivated()
    {
        var issued = _tokenRegistry.Issue("TestWorkflow");
        await _instanceRegistry.RegisterAsync(issued.WorkflowInstanceId, "TestWorkflow");
        await SeedSlotAsync();
        await StartHandlerAsync();

        await _bus.InvokeAsync(new SlotActivationRequest(
            issued.WorkflowInstanceId, "slotA", ValidPublicKey(), issued.Token), CancellationToken.None);

        var entries = await AuditEntriesAsync("workflow.slot-activated");
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Subject, Is.EqualTo(issued.WorkflowInstanceId.ToString()));
        Assert.That(entries[0].Outcome, Is.EqualTo("slotA"));
    }

    [Test]
    public async Task HandleAsync_InvalidToken_PublishesNothingAndAuditsRejection()
    {
        var issued = _tokenRegistry.Issue("TestWorkflow");
        await _instanceRegistry.RegisterAsync(issued.WorkflowInstanceId, "TestWorkflow");
        await SeedSlotAsync();
        await StartHandlerAsync();

        await _bus.InvokeAsync(new SlotActivationRequest(
            issued.WorkflowInstanceId, "slotA", ValidPublicKey(), "not-the-issued-token"), CancellationToken.None);

        Assert.That(_bus.Published, Is.Empty,
            "An invalid token must be rejected silently — no response on any queue.");
        var rejections = await AuditEntriesAsync("workflow.slot-activation.rejected");
        Assert.That(rejections, Has.Count.EqualTo(1));
        Assert.That(rejections[0].Outcome, Is.EqualTo("invalid-instance-token"));
    }

    [Test]
    public async Task HandleAsync_MissingToken_PublishesNothing()
    {
        var issued = _tokenRegistry.Issue("TestWorkflow");
        await _instanceRegistry.RegisterAsync(issued.WorkflowInstanceId, "TestWorkflow");
        await SeedSlotAsync();
        await StartHandlerAsync();

        await _bus.InvokeAsync(new SlotActivationRequest(
            issued.WorkflowInstanceId, "slotA", ValidPublicKey()), CancellationToken.None);

        Assert.That(_bus.Published, Is.Empty);
    }

    [Test]
    public async Task HandleAsync_UnknownInstance_PublishesFailureResponse()
    {
        var issued = _tokenRegistry.Issue("TestWorkflow");
        // Token is valid but the instance never registered in the instance registry.
        await StartHandlerAsync();

        await _bus.InvokeAsync(new SlotActivationRequest(
            issued.WorkflowInstanceId, "slotA", ValidPublicKey(), issued.Token), CancellationToken.None);

        Assert.That(_bus.Published, Has.Count.EqualTo(1));
        var response = (SlotActivationResponse)_bus.Published[0].Message;
        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.ErrorMessage, Does.Contain("not registered"));
            Assert.That(response.Slot, Is.Null);
        });
    }

    [Test]
    public async Task HandleAsync_UnknownSlot_PublishesFailureResponseAndAuditsRejection()
    {
        var issued = _tokenRegistry.Issue("TestWorkflow");
        await _instanceRegistry.RegisterAsync(issued.WorkflowInstanceId, "TestWorkflow");
        await StartHandlerAsync(); // store stays empty — no configuration for any slot

        await _bus.InvokeAsync(new SlotActivationRequest(
            issued.WorkflowInstanceId, "ghost-slot", ValidPublicKey(), issued.Token), CancellationToken.None);

        Assert.That(_bus.Published, Has.Count.EqualTo(1));
        var response = (SlotActivationResponse)_bus.Published[0].Message;
        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.ErrorMessage, Does.Contain("ghost-slot"));
            Assert.That(response.Slot, Is.Null);
        });
        var rejections = await AuditEntriesAsync("workflow.slot-activation.rejected");
        Assert.That(rejections, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task HandleAsync_TokenNotRequired_ActivatesWithoutToken()
    {
        var instanceId = Guid.NewGuid();
        await _instanceRegistry.RegisterAsync(instanceId, "TestWorkflow");
        await SeedSlotAsync();
        await StartHandlerAsync(requireInstanceToken: false);

        await _bus.InvokeAsync(new SlotActivationRequest(
            instanceId, "slotA", ValidPublicKey()), CancellationToken.None);

        Assert.That(_bus.Published, Has.Count.EqualTo(1));
        var response = (SlotActivationResponse)_bus.Published[0].Message;
        Assert.That(response.Success, Is.True);
        Assert.That(response.Slot, Is.Not.Null);
    }

    [Test]
    public async Task StartAsync_DeclaresAndSubscribesSlotActivationQueue()
    {
        await StartHandlerAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_bus.DeclaredQueues, Does.Contain("workflow-slot-activation"));
            Assert.That(_bus.SubscribedQueues, Does.Contain("workflow-slot-activation"));
        });
    }

    // A fake that captures the subscription callback so tests can invoke it directly.
    private sealed class CapturingBus : IMessageBusClient
    {
        public List<(string Topic, object Message)> Published { get; } = [];
        public List<string> DeclaredQueues { get; } = [];
        public List<string> SubscribedQueues { get; } = [];
        private Func<SlotActivationRequest, CancellationToken, Task>? _handler;

        public Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default)
        {
            DeclaredQueues.Add(queueName);
            return Task.CompletedTask;
        }

        public Task DeclareExchangeAsync(string exchangeName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
        {
            Published.Add((topic, message!));
            return Task.CompletedTask;
        }

        public Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken cancellationToken = default)
        {
            Published.Add((exchangeName, message!));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(
            string queueName,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            SubscribedQueues.Add(queueName);
            if (handler is Func<SlotActivationRequest, CancellationToken, Task> typedHandler)
                _handler = typedHandler;
            return Task.FromResult<IAsyncDisposable>(new NullDisposable());
        }

        public Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(
            string exchangeName,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IAsyncDisposable>(new NullDisposable());

        public Task InvokeAsync(SlotActivationRequest request, CancellationToken cancellationToken)
            => _handler!(request, cancellationToken);

        private sealed class NullDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
