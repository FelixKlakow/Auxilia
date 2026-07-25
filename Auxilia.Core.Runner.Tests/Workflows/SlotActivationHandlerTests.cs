using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.Core.Contracts;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Tests.Workflows;

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
    private WorkflowInstanceRegistry _instanceRegistry = null!;
    private WorkflowInstanceTokenRegistry _tokenRegistry = null!;
    private InMemoryDataAccess<AuditRecord> _auditRecords = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider();
        _bus = new CapturingBus();
        _instanceRegistry = TestStores.NewWorkflowInstanceRegistry();
        _tokenRegistry = new WorkflowInstanceTokenRegistry(
            Options.Create(new WorkflowDispatcherSettings()), _time);
        _auditRecords = new InMemoryDataAccess<AuditRecord>();
    }

    [TearDown]
    public void TearDown() => _auditRecords.Dispose();

    private async Task<SlotActivationHandler> StartHandlerAsync(
        bool requireInstanceToken = true,
        ICoreCredentialClient? coreClient = null)
    {
        var settings = Options.Create(new WorkflowDispatcherSettings
        {
            RequireInstanceToken = requireInstanceToken,
            SlotCredentialLifetime = CredentialLifetime
        });
        var handler = new SlotActivationHandler(
            _bus,
            coreClient ?? new StubCoreCredentialClient(),
            _instanceRegistry,
            _tokenRegistry,
            new AuditLog(_auditRecords, _time),
            settings,
            NullLogger<SlotActivationHandler>.Instance);
        await handler.StartAsync(CancellationToken.None);
        return handler;
    }

    /// <summary>
    /// Persists the instance's dispatch command carrying a run-scoped resolution token — the
    /// context the Core-only handler needs to resolve a slot. Returns the resolved run id.
    /// </summary>
    private async Task<Guid> SeedDispatchedInstanceAsync(
        Guid instanceId, string? resolutionToken, string workflowType = "TestWorkflow")
    {
        var coreRunId = Guid.NewGuid();
        var command = new RunWorkflowCommand(
            coreRunId, workflowType, "docker://img",
            new Dictionary<string, string>(), ResolutionToken: resolutionToken);
        await _instanceRegistry.CreateAsync(
            instanceId, workflowType, "Queued",
            dispatchCommandJson: JsonSerializer.Serialize(command));
        return coreRunId;
    }

    private ResolvedSlotCredential SampleCredential(string providerType = "ProviderX", string cipher = "CIPHERTEXT==")
        => new(providerType, cipher, _time.Now + CredentialLifetime);

    private async Task<List<AuditRecord>> AuditEntriesAsync(string action)
        => (await _auditRecords.ReadAsync()).Where(r => r.Action == action).ToList();

    private static string ValidPublicKey()
    {
        using var rsa = RSA.Create(2048);
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }

    [Test]
    public async Task HandleAsync_ValidTokenAndRegisteredInstance_RelaysCoreCredentialOnCanonicalQueue()
    {
        var issued = _tokenRegistry.Issue("TestWorkflow");
        await SeedDispatchedInstanceAsync(issued.WorkflowInstanceId, "run-token-abc");
        var stub = new StubCoreCredentialClient { Result = SampleCredential() };
        await StartHandlerAsync(coreClient: stub);

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
            // The runner relays exactly what the Core resolved — provider, ciphertext, and expiry.
            Assert.That(response.Slot!.ProviderType, Is.EqualTo("ProviderX"));
            Assert.That(response.Slot.EncryptedSettings, Is.EqualTo("CIPHERTEXT=="));
            Assert.That(response.ExpiresUtc, Is.EqualTo(_time.Now + CredentialLifetime));
        });
        // The Core is asked to resolve THIS slot with the run's resolution token.
        Assert.That(stub.LastCall!.Value.Token, Is.EqualTo("run-token-abc"));
        Assert.That(stub.LastCall.Value.Slot, Is.EqualTo("slotA"));
    }

    [Test]
    public async Task HandleAsync_SuccessfulActivation_AuditsSlotActivated()
    {
        var issued = _tokenRegistry.Issue("TestWorkflow");
        await SeedDispatchedInstanceAsync(issued.WorkflowInstanceId, "run-token");
        await StartHandlerAsync(coreClient: new StubCoreCredentialClient { Result = SampleCredential() });

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
        await SeedDispatchedInstanceAsync(issued.WorkflowInstanceId, "run-token");
        await StartHandlerAsync(coreClient: new StubCoreCredentialClient { Result = SampleCredential() });

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
        await SeedDispatchedInstanceAsync(issued.WorkflowInstanceId, "run-token");
        await StartHandlerAsync(coreClient: new StubCoreCredentialClient { Result = SampleCredential() });

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
    public async Task HandleAsync_RunWithoutResolutionToken_PublishesFailureAndAuditsRejection()
    {
        // The instance exists, but its dispatch command carries no run-scoped resolution token —
        // the runner has no way to ask the Core, so it fails the activation.
        var issued = _tokenRegistry.Issue("TestWorkflow");
        await SeedDispatchedInstanceAsync(issued.WorkflowInstanceId, resolutionToken: null);
        await StartHandlerAsync();

        await _bus.InvokeAsync(new SlotActivationRequest(
            issued.WorkflowInstanceId, "slotA", ValidPublicKey(), issued.Token), CancellationToken.None);

        Assert.That(_bus.Published, Has.Count.EqualTo(1));
        var response = (SlotActivationResponse)_bus.Published[0].Message;
        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.ErrorMessage, Does.Contain("resolution context"));
            Assert.That(response.Slot, Is.Null);
        });
        var rejections = await AuditEntriesAsync("workflow.slot-activation.rejected");
        Assert.That(rejections, Has.Count.EqualTo(1));
        Assert.That(rejections[0].Outcome, Is.EqualTo("no-resolution-token"));
    }

    [Test]
    public async Task HandleAsync_CoreResolutionFails_PublishesFailureResponseAndAuditsRejection()
    {
        var issued = _tokenRegistry.Issue("TestWorkflow");
        await SeedDispatchedInstanceAsync(issued.WorkflowInstanceId, "run-token");
        // The Core declines to resolve (unknown slot / expired binding) — returns null.
        await StartHandlerAsync(coreClient: new StubCoreCredentialClient { Result = null });

        await _bus.InvokeAsync(new SlotActivationRequest(
            issued.WorkflowInstanceId, "ghost-slot", ValidPublicKey(), issued.Token), CancellationToken.None);

        Assert.That(_bus.Published, Has.Count.EqualTo(1));
        var response = (SlotActivationResponse)_bus.Published[0].Message;
        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.ErrorMessage, Does.Contain("resolution failed"));
            Assert.That(response.Slot, Is.Null);
        });
        var rejections = await AuditEntriesAsync("workflow.slot-activation.rejected");
        Assert.That(rejections, Has.Count.EqualTo(1));
        Assert.That(rejections[0].Outcome, Is.EqualTo("core-resolution-failed"));
    }

    [Test]
    public async Task HandleAsync_TokenNotRequired_ActivatesWithoutToken()
    {
        var instanceId = Guid.NewGuid();
        await SeedDispatchedInstanceAsync(instanceId, "run-token");
        await StartHandlerAsync(
            requireInstanceToken: false,
            coreClient: new StubCoreCredentialClient { Result = SampleCredential() });

        await _bus.InvokeAsync(new SlotActivationRequest(
            instanceId, "slotA", ValidPublicKey()), CancellationToken.None);

        Assert.That(_bus.Published, Has.Count.EqualTo(1));
        var response = (SlotActivationResponse)_bus.Published[0].Message;
        Assert.That(response.Success, Is.True);
        Assert.That(response.Slot, Is.Not.Null);
    }

    [Test]
    public async Task HandleAsync_CoreDispatchedRun_RelaysCoreResolvedCredential()
    {
        // A Core-dispatched run carries a resolution token in its persisted dispatch command; the
        // handler resolves via the Core and relays the ciphertext, keyed to the resolved run id.
        var issued = _tokenRegistry.Issue("TestWorkflow");
        var coreRunId = await SeedDispatchedInstanceAsync(issued.WorkflowInstanceId, "run-token-abc");

        var stub = new StubCoreCredentialClient
        {
            Result = new ResolvedSlotCredential("github", "CIPHERTEXT==", _time.Now + CredentialLifetime)
        };
        await StartHandlerAsync(coreClient: stub);

        await _bus.InvokeAsync(new SlotActivationRequest(
            issued.WorkflowInstanceId, "sc", ValidPublicKey(), issued.Token), CancellationToken.None);

        Assert.That(_bus.Published, Has.Count.EqualTo(1));
        var response = (SlotActivationResponse)_bus.Published[0].Message;
        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.Slot!.ProviderType, Is.EqualTo("github"));
            Assert.That(response.Slot.EncryptedSettings, Is.EqualTo("CIPHERTEXT=="));
            Assert.That(response.ExpiresUtc, Is.EqualTo(_time.Now + CredentialLifetime));
        });
        Assert.That(stub.LastCall!.Value.RunId, Is.EqualTo(coreRunId));
        Assert.That(stub.LastCall.Value.Token, Is.EqualTo("run-token-abc"));
    }

    private sealed class StubCoreCredentialClient : ICoreCredentialClient
    {
        public ResolvedSlotCredential? Result { get; init; }
        public (Guid RunId, string Token, string Slot)? LastCall { get; private set; }

        public Task<ResolvedSlotCredential?> ResolveAsync(
            Guid coreRunId, string resolutionToken, string slotName, string publicKey, CancellationToken ct)
        {
            LastCall = (coreRunId, resolutionToken, slotName);
            return Task.FromResult(Result);
        }
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
