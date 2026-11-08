using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows.Tests;

/// <summary>
/// Checkpoints a workflow declares and saves: the declaration rides schema and manifest, a save
/// only NAMES a declared checkpoint — the platform reads its path from the declaration — and the
/// platform's refusal surfaces in the workflow instead of passing for a save.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RunCheckpointsTests
{
    private const string ProxyQueue = "workflow-resource-proxy";
    private const string ResponseTopic = "workflow-response-test";

    [Test]
    public void DeclaredCheckpoints_RideSchemaAndManifest()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test")
            .DeclaresCheckpoint("session", @"state\session", "the session to hand back", TimeSpan.FromDays(2))
            .DeclaresCheckpoint("summary", "summary.md");

        var expected = new[]
        {
            new WorkflowCheckpointDescriptor("session", "state/session", "the session to hand back", LifetimeHours: 48),
            new WorkflowCheckpointDescriptor("summary", "summary.md"),
        };
        Assert.Multiple(() =>
        {
            Assert.That(builder.BuildSchema().Checkpoints, Is.EqualTo(expected));
            Assert.That(builder.BuildManifest().Checkpoints, Is.EqualTo(expected));
        });
    }

    [TestCase("Session", "state")]
    [TestCase("session", "")]
    [TestCase("session", "/etc/passwd")]
    [TestCase("session", "../outside")]
    [TestCase("session", "state/../../outside")]
    [TestCase("session", "state//double")]
    public void ACheckpoint_HasAName_AndAPathInsideTheOutputDirectory(string name, string path)
        => Assert.Throws<ArgumentException>(() => WorkflowBuilder.Create("test").DeclaresCheckpoint(name, path));

    [Test]
    public void ACheckpoint_IsDeclaredOnce_WithAPositiveLifetime()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidOperationException>(() =>
                WorkflowBuilder.Create("test").DeclaresCheckpoint("a", "a").DeclaresCheckpoint("a", "b"));
            Assert.Throws<ArgumentException>(() =>
                WorkflowBuilder.Create("test").DeclaresCheckpoint("a", "a", lifetime: TimeSpan.Zero));
        });
    }

    [Test]
    public async Task ASave_NamesTheCheckpoint_AndReturnsWhatThePlatformKept()
    {
        var bus = new RespondingBus(request => new ResourceResponse(request.RequestId, true, null,
            """{"id":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","version":3,"sizeBytes":42,"contentHash":"abc","expiresUtc":"2026-11-10T12:00:00+00:00","kind":"checkpoint"}"""));
        await using var proxy = await StartProxyAsync(bus);
        var checkpoints = new ResourceProxyRunCheckpoints(proxy, [new WorkflowCheckpointDescriptor("session", "state/session")]);

        var saved = await checkpoints.SaveAsync("session");

        var request = bus.Requests.Single();
        Assert.Multiple(() =>
        {
            Assert.That(request.ResourceName, Is.EqualTo(RunCheckpointsResource.Name));
            Assert.That(request.Operation, Is.EqualTo(RunCheckpointsResource.SaveOperation));
            Assert.That(JsonDocument.Parse(request.PayloadJson).RootElement.GetProperty("name").GetString(), Is.EqualTo("session"));
            Assert.That(request.PayloadJson, Does.Not.Contain("state/session"), "the path comes from the declaration");
            Assert.That(saved, Is.EqualTo(new RunCheckpoint(
                Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"), 3, 42, "abc",
                new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero))));
        });
    }

    [Test]
    public async Task AnUndeclaredName_IsRefusedBeforeAnythingIsSent_AndARefusedSaveThrows()
    {
        var bus = new RespondingBus(request => new ResourceResponse(request.RequestId, false, "the checkpoint exceeds the limit", null));
        await using var proxy = await StartProxyAsync(bus);
        var checkpoints = new ResourceProxyRunCheckpoints(proxy, [new WorkflowCheckpointDescriptor("session", "state")]);

        var undeclared = Assert.ThrowsAsync<InvalidOperationException>(() => checkpoints.SaveAsync("secrets"));
        Assert.That(bus.Requests, Is.Empty);
        var refused = Assert.ThrowsAsync<InvalidOperationException>(() => checkpoints.SaveAsync("session"));

        Assert.Multiple(() =>
        {
            Assert.That(undeclared!.Message, Does.Contain("not declared"));
            Assert.That(refused!.Message, Does.Contain("exceeds the limit"));
        });
    }

    private static async Task<ResourceProxyClient> StartProxyAsync(RespondingBus bus)
    {
        var proxy = new ResourceProxyClient(bus, Guid.NewGuid(), "token", ProxyQueue, ResponseTopic);
        await proxy.StartAsync();
        return proxy;
    }

    /// <summary>Answers every resource request on the response queue, the way the runner does.</summary>
    private sealed class RespondingBus(Func<ResourceRequest, ResourceResponse> respond) : IMessageBusClient
    {
        private Func<ResourceResponse, CancellationToken, Task>? _responses;

        public List<ResourceRequest> Requests { get; } = [];

        public Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeclareExchangeAsync(string exchangeName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IAsyncDisposable> SubscribeAsync<T>(
            string queueName, Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
        {
            if (queueName == ResponseTopic && handler is Func<ResourceResponse, CancellationToken, Task> responses)
                _responses = responses;
            return Task.FromResult<IAsyncDisposable>(new Nothing());
        }

        public Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(
            string exchangeName, Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
            => SubscribeAsync(exchangeName, handler, cancellationToken);

        public async Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
        {
            if (topic != ProxyQueue || message is not ResourceRequest request)
                return;
            Requests.Add(request);
            await _responses!(respond(request), cancellationToken);
        }

        public Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken cancellationToken = default)
            => PublishAsync(exchangeName, message, cancellationToken);

        private sealed class Nothing : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
