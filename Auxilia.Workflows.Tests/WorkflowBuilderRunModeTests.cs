using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Auxilia.Workflows.Tests;

[TestFixture]
public class WorkflowBuilderRunModeTests
{
    [SetUp]
    public void SetUp()
    {
        System.Environment.SetEnvironmentVariable("WORKFLOW_CONFIG_TIMEOUT_SECONDS", null);
    }

    [TearDown]
    public void TearDown()
    {
        System.Environment.SetEnvironmentVariable("WORKFLOW_CONFIG_TIMEOUT_SECONDS", null);
    }

    [Test]
    public async Task Run_WhenResponseNotReceivedBeforeTimeout_CallsExitWithCode1()
    {
        System.Environment.SetEnvironmentVariable("WORKFLOW_CONFIG_TIMEOUT_SECONDS", "1");

        var bus = new NeverDeliverBus();
        var mockExit = new Mock<IProcessExitService>();
        var services = new ServiceCollection();

        var builder = WorkflowBuilder.Create("test-workflow");

        await builder.Run(["run"], bus, services, null, mockExit.Object);

        mockExit.Verify(e => e.Exit(1), Times.Once);
    }

    [Test]
    public async Task Run_WhenResponseSuccessIsFalse_CallsExitWithCode1()
    {
        System.Environment.SetEnvironmentVariable("WORKFLOW_CONFIG_TIMEOUT_SECONDS", "10");

        var bus = new ImmediateResponseBus(req =>
            new WorkflowConfigurationResponse(req.WorkflowInstanceId, false, "Something failed", new Dictionary<string, EncryptedSlotConfiguration>()));

        var mockExit = new Mock<IProcessExitService>();
        var services = new ServiceCollection();

        var builder = WorkflowBuilder.Create("test-workflow");

        await builder.Run(["run"], bus, services, null, mockExit.Object);

        mockExit.Verify(e => e.Exit(1), Times.Once);
    }

    [Test]
    public async Task Run_WhenResponseSuccessIsTrue_CallsBootstrapperApply()
    {
        System.Environment.SetEnvironmentVariable("WORKFLOW_CONFIG_TIMEOUT_SECONDS", "10");

        var providerType = $"run-mode-test-{Guid.NewGuid()}";
        var spy = new SpySlotHandler();
        SlotHandlerRegistry.Register(providerType, spy);

        var bus = new ImmediateResponseBus(req =>
        {
            var publicKeyBytes = Convert.FromBase64String(req.PublicKey);
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);

            var dto = new { ProviderType = providerType, Settings = new Dictionary<string, string> { ["k"] = "v" } };
            var json = JsonSerializer.Serialize(dto);
            var ciphertext = Convert.ToBase64String(
                rsa.Encrypt(Encoding.UTF8.GetBytes(json), RSAEncryptionPadding.OaepSHA256));

            var slots = new Dictionary<string, EncryptedSlotConfiguration>
            {
                ["slot1"] = new EncryptedSlotConfiguration(providerType, ciphertext)
            };
            return new WorkflowConfigurationResponse(req.WorkflowInstanceId, true, null, slots);
        });

        var mockExit = new Mock<IProcessExitService>();
        var services = new ServiceCollection();

        var builder = WorkflowBuilder.Create("test-workflow");

        await builder.Run(["run"], bus, services, null, mockExit.Object);

        Assert.That(spy.Calls, Has.Count.EqualTo(1));
        mockExit.Verify(e => e.Exit(It.IsAny<int>()), Times.Never);
    }

    // Fake bus that never delivers a message (used for timeout test)
    private sealed class NeverDeliverBus : IMessageBusClient
    {
        public Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IAsyncDisposable> SubscribeAsync<T>(
            string queueName,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IAsyncDisposable>(NoopDisposable.Instance);

        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    // Fake bus that immediately delivers a response based on the registration request
    private sealed class ImmediateResponseBus(
        Func<WorkflowRegistrationRequest, WorkflowConfigurationResponse> responseFactory) : IMessageBusClient
    {
        private Func<WorkflowConfigurationResponse, CancellationToken, Task>? _handler;

        public Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IAsyncDisposable> SubscribeAsync<T>(
            string queueName,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            if (typeof(T) == typeof(WorkflowConfigurationResponse))
                _handler = (msg, ct) => handler((T)(object)msg, ct);
            return Task.FromResult<IAsyncDisposable>(NoopDisposable.Instance);
        }

        public async Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
        {
            if (_handler != null && message is WorkflowRegistrationRequest req)
            {
                var response = responseFactory(req);
                await _handler(response, cancellationToken);
            }
        }
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SpySlotHandler : ISlotHandler
    {
        public List<SlotConfiguration> Calls { get; } = new();
        public void Register(IServiceCollection services, SlotConfiguration configuration)
            => Calls.Add(configuration);
    }
}
