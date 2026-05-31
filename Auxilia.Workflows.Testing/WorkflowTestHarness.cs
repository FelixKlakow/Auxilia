using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows.Testing;

public sealed class WorkflowTestHarness
{
    private readonly Func<Task> _entryPoint;
    private readonly WorkflowDirectiveKind _directive;
    private readonly IReadOnlyList<(string Name, FakeSlotConfiguration Config)> _slots;
    private readonly TimeSpan _timeout;

    private WorkflowTestHarness(
        Func<Task> entryPoint,
        WorkflowDirectiveKind directive,
        IReadOnlyList<(string Name, FakeSlotConfiguration Config)> slots,
        TimeSpan timeout)
    {
        _entryPoint = entryPoint;
        _directive = directive;
        _slots = slots;
        _timeout = timeout;
    }

    public static WorkflowTestHarnessBuilder For(Func<Task> entryPoint) => new(entryPoint);

    public async Task<HarnessResult> RunAsync()
    {
        var bus = new InProcessMessageBus();
        IWorkflowRunContext harnessContext = new HarnessWorkflowRunContext(bus);

        var announcementTcs = new TaskCompletionSource<WorkflowAnnouncementMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await bus.SubscribeAsync<WorkflowAnnouncementMessage>(
            "workflow.announcements",
            (msg, _) => { announcementTcs.TrySetResult(msg); return Task.CompletedTask; });

        TaskCompletionSource<WorkflowSchemaMessage>? schemaTcs = null;
        TaskCompletionSource<WorkflowRegistrationRequest>? registrationTcs = null;
        TaskCompletionSource<WorkflowStateMessage>? stateTcs = null;

        if (_directive == WorkflowDirectiveKind.EmitSchema)
        {
            schemaTcs = new TaskCompletionSource<WorkflowSchemaMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await bus.SubscribeAsync<WorkflowSchemaMessage>(
                "workflow.schema",
                (msg, _) => { schemaTcs.TrySetResult(msg); return Task.CompletedTask; });
        }
        else
        {
            registrationTcs = new TaskCompletionSource<WorkflowRegistrationRequest>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await bus.SubscribeAsync<WorkflowRegistrationRequest>(
                "workflow-registration",
                (msg, _) => { registrationTcs.TrySetResult(msg); return Task.CompletedTask; });

            stateTcs = new TaskCompletionSource<WorkflowStateMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await bus.SubscribeAsync<WorkflowStateMessage>(
                "workflow.state",
                (msg, _) => { stateTcs.TrySetResult(msg); return Task.CompletedTask; });
        }

        WorkflowBuilder.TestContext = harnessContext;
        _ = Task.Run(() => _entryPoint());

        using var timeoutCts = new CancellationTokenSource(_timeout);
        try
        {
            var announcementMsg = await announcementTcs.Task.WaitAsync(timeoutCts.Token);
            var responseTopic = announcementMsg.ResponseTopic;
            var publicKey = announcementMsg.PublicKey;

            await bus.PublishAsync(responseTopic,
                new WorkflowDirective(announcementMsg.WorkflowInstanceId, _directive));

            if (_directive == WorkflowDirectiveKind.EmitSchema)
            {
                var schemaMsg = await schemaTcs!.Task.WaitAsync(timeoutCts.Token);
                return new HarnessResult(null, null, schemaMsg.Schema);
            }
            else
            {
                var registrationMsg = await registrationTcs!.Task.WaitAsync(timeoutCts.Token);

                var missingSlots = registrationMsg.Manifest.Slots
                    .Where(s => !_slots.Any(ps => ps.Name == s.SlotName))
                    .Select(s => s.SlotName)
                    .ToList();

                if (missingSlots.Count > 0)
                {
                    var errorMsg = $"Required slots not configured: {string.Join(", ", missingSlots)}";
                    await bus.PublishAsync(registrationMsg.ResponseTopic,
                        new WorkflowConfigurationResponse(
                            registrationMsg.WorkflowInstanceId,
                            false,
                            errorMsg,
                            new Dictionary<string, EncryptedSlotConfiguration>()));
                    return new HarnessResult(WorkflowState.Failed, errorMsg, null);
                }

                var encryptedSlots = new Dictionary<string, EncryptedSlotConfiguration>();
                using var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);

                foreach (var (name, slot) in _slots)
                {
                    var json = JsonSerializer.Serialize(slot.Settings);
                    var plainBytes = Encoding.UTF8.GetBytes(json);
                    var encryptedBytes = rsa.Encrypt(plainBytes, RSAEncryptionPadding.OaepSHA256);
                    encryptedSlots[name] = new EncryptedSlotConfiguration(
                        slot.ProviderType,
                        Convert.ToBase64String(encryptedBytes));
                }

                await bus.PublishAsync(registrationMsg.ResponseTopic,
                    new WorkflowConfigurationResponse(
                        registrationMsg.WorkflowInstanceId,
                        true,
                        null,
                        encryptedSlots));

                var stateMsg = await stateTcs!.Task.WaitAsync(timeoutCts.Token);
                return new HarnessResult(stateMsg.State, stateMsg.ErrorMessage, null);
            }
        }
        catch (OperationCanceledException)
        {
            throw new WorkflowHarnessTimeoutException(_timeout);
        }
        finally
        {
            WorkflowBuilder.TestContext = null;
        }
    }

    public sealed class WorkflowTestHarnessBuilder
    {
        private readonly Func<Task> _entryPoint;
        private WorkflowDirectiveKind _directive = WorkflowDirectiveKind.Run;
        private TimeSpan _timeout = TimeSpan.FromSeconds(10);
        private readonly List<(string Name, FakeSlotConfiguration Config)> _slots = [];

        internal WorkflowTestHarnessBuilder(Func<Task> entryPoint)
        {
            _entryPoint = entryPoint;
        }

        public WorkflowTestHarnessBuilder WithDirective(WorkflowDirectiveKind directive)
        {
            _directive = directive;
            return this;
        }

        public WorkflowTestHarnessBuilder WithSlot(string name, string providerType, Dictionary<string, string> settings)
        {
            _slots.Add((name, new FakeSlotConfiguration(providerType, settings)));
            return this;
        }

        public WorkflowTestHarnessBuilder WithTimeout(TimeSpan timeout)
        {
            _timeout = timeout;
            return this;
        }

        public WorkflowTestHarness Build() =>
            new(_entryPoint, _directive, _slots.AsReadOnly(), _timeout);
    }
}
