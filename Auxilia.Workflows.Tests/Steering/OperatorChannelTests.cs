using System.Text.Json;
using System.Threading.Channels;
using Auxilia.Workflows.Steering;
using Auxilia.Workflows.Views;

namespace Auxilia.Workflows.Tests.Steering;

[TestFixture]
[Category("Unit")]
public sealed class OperatorChannelTests
{
    [Test]
    public async Task Start_AnnouncesTheSteeringCapabilities()
    {
        var views = new RecordingViewPublisher();
        await using var channel = await OperatorChannel.StartAsync(views, new FakeInputs());

        var capabilities = views.Single("capabilities");
        Assert.That(capabilities.GetProperty("accepts").EnumerateArray().Select(a => a.GetString()),
            Is.EquivalentTo(new[] { "guidance", "form-answer", "halt" }));
        Assert.That(views.ViewNames, Has.All.EqualTo(OperatorChannel.ViewName));
    }

    [Test]
    public async Task Ask_PublishesTheForm_AndResolvesWithTheOperatorsAnswer()
    {
        var views = new RecordingViewPublisher();
        var inputs = new FakeInputs();
        await using var channel = await OperatorChannel.StartAsync(views, inputs);

        var asking = channel.AskAsync(
        [
            new OperatorQuestion("q1", "Deploy to production?",
                [new OperatorOption("yes", "Yes"), new OperatorOption("no", "No")],
                MultiSelect: false, AllowFreeText: true)
        ], CancellationToken.None);

        // The wire item is the protocol's form-requested shape, camel-cased, $type first.
        var request = views.WaitForSingle("form-requested");
        var requestId = request.GetProperty("requestId").GetString()!;
        var question = request.GetProperty("questions").EnumerateArray().Single();
        Assert.Multiple(() =>
        {
            Assert.That(question.GetProperty("id").GetString(), Is.EqualTo("q1"));
            Assert.That(question.GetProperty("prompt").GetString(), Is.EqualTo("Deploy to production?"));
            Assert.That(question.GetProperty("allowFreeText").GetBoolean(), Is.True);
            Assert.That(question.GetProperty("options").GetArrayLength(), Is.EqualTo(2));
        });

        inputs.Push($$"""
            {"$type":"form-answer","requestId":"{{requestId}}","answers":[
                {"questionId":"q1","selectedIds":["yes"],"freeText":"but only the api"}]}
            """);

        var answers = await asking.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(answers.Single().SelectedIds, Is.EqualTo(new[] { "yes" }));
            Assert.That(answers.Single().FreeText, Is.EqualTo("but only the api"));
        });
        Assert.That(views.WaitForSingle("form-resolved").GetProperty("requestId").GetString(),
            Is.EqualTo(requestId), "Answered forms are resolved so no stale card survives a replay.");
    }

    [Test]
    public async Task Guidance_ReachesTheWaiter_AndHaltCancelsTheToken()
    {
        var inputs = new FakeInputs();
        await using var channel = await OperatorChannel.StartAsync(new RecordingViewPublisher(), inputs);

        var waiting = channel.WaitForGuidanceAsync(CancellationToken.None);
        inputs.Push("""{"$type":"guidance","text":"prefer small commits"}""");
        Assert.That(await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
            Is.EqualTo("prefer small commits"));

        Assert.That(channel.HaltToken.IsCancellationRequested, Is.False);
        inputs.Push("""{"$type":"halt","reason":"stop"}""");
        await WaitUntilAsync(() => channel.HaltToken.IsCancellationRequested);
    }

    [Test]
    public async Task UnparseableOperatorInput_IsIgnored()
    {
        var inputs = new FakeInputs();
        await using var channel = await OperatorChannel.StartAsync(new RecordingViewPublisher(), inputs);

        inputs.Push("this is not json");
        inputs.Push("""{"$type":"guidance","text":"still alive"}""");
        Assert.That(await channel.WaitForGuidanceAsync(CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
            Is.EqualTo("still alive"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);
        Assert.That(condition(), Is.True);
    }

    private sealed class FakeInputs : IWorkflowInputs
    {
        private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();

        public void Push(string payloadJson) => _channel.Writer.TryWrite(payloadJson);

        public async Task<string> ReceiveAsync(CancellationToken ct = default)
            => await _channel.Reader.ReadAsync(ct);
    }

    private sealed class RecordingViewPublisher : IViewPublisher
    {
        private readonly List<(string View, string Json)> _published = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<string> ViewNames
        {
            get { lock (_gate) return _published.Select(p => p.View).ToList(); }
        }

        public Task PublishAsync<TItem>(string viewName, TItem item, CancellationToken ct = default)
        {
            lock (_gate)
                _published.Add((viewName, JsonSerializer.Serialize(item)));
            return Task.CompletedTask;
        }

        public JsonElement Single(string type) => Find(type)
            ?? throw new InvalidOperationException($"No '{type}' item was published.");

        public JsonElement WaitForSingle(string type)
        {
            for (var i = 0; i < 100; i++)
            {
                if (Find(type) is { } found)
                    return found;
                Thread.Sleep(20);
            }
            throw new InvalidOperationException($"No '{type}' item was published.");
        }

        private JsonElement? Find(string type)
        {
            lock (_gate)
                foreach (var (_, json) in _published)
                {
                    var element = JsonDocument.Parse(json).RootElement;
                    if (element.TryGetProperty("$type", out var t) && t.GetString() == type)
                        return element;
                }
            return null;
        }
    }
}
