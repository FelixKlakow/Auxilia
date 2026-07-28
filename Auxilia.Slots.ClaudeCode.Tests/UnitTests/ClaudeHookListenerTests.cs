using System.Text;
using Auxilia.Workflows.AiAgent.CodingAgent;

namespace Auxilia.Slots.ClaudeCode.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class ClaudeHookListenerTests
{
    [Test]
    public void Parse_MapsTheHookEvents_ToSessionEvents()
    {
        Assert.Multiple(() =>
        {
            var attention = ClaudeHookListener.Parse(
                "{\"hook_event_name\":\"Notification\",\"message\":\"Claude needs your permission to use Bash\"}");
            Assert.That(attention!.Kind, Is.EqualTo(ConsoleSessionEvent.Attention));
            Assert.That(attention.Message, Does.Contain("permission to use Bash"));

            var started = ClaudeHookListener.Parse(
                "{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"ls\"}}");
            Assert.That(started!.Kind, Is.EqualTo(ConsoleSessionEvent.ToolStarted));
            Assert.That(started.ToolName, Is.EqualTo("Bash"));
            Assert.That(started.Message, Does.Contain("ls"));

            var finished = ClaudeHookListener.Parse(
                "{\"hook_event_name\":\"PostToolUse\",\"tool_name\":\"Bash\"}");
            Assert.That(finished!.Kind, Is.EqualTo(ConsoleSessionEvent.ToolFinished));

            var stop = ClaudeHookListener.Parse("{\"hook_event_name\":\"Stop\"}");
            Assert.That(stop!.Kind, Is.EqualTo(ConsoleSessionEvent.TurnEnded));

            Assert.That(ClaudeHookListener.Parse("{\"hook_event_name\":\"SomethingNew\"}"), Is.Null,
                "unknown hooks are ignored, never an error");
            Assert.That(ClaudeHookListener.Parse("not json"), Is.Null);
        });
    }

    [Test]
    public void Parse_TruncatesOversizedToolInput()
    {
        var payload = "{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Write\",\"tool_input\":{\"content\":\""
                      + new string('x', 2000) + "\"}}";

        var evt = ClaudeHookListener.Parse(payload);

        Assert.That(evt!.Message, Has.Length.LessThan(300));
    }

    [Test]
    public async Task Listener_ReceivesPostedHookPayloads_AndAlwaysAnswersEmpty200()
    {
        var listener = new ClaudeHookListener();
        var received = new TaskCompletionSource<ConsoleSessionEvent>();
        await listener.StartAsync((evt, _) =>
        {
            received.TrySetResult(evt);
            return Task.CompletedTask;
        }, CancellationToken.None);

        try
        {
            using var http = new HttpClient();
            var response = await http.PostAsync(
                $"http://127.0.0.1:{listener.Port}/",
                new StringContent(
                    "{\"hook_event_name\":\"Notification\",\"message\":\"waiting\"}",
                    Encoding.UTF8, "application/json"));

            var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That((int)response.StatusCode, Is.EqualTo(200));
                Assert.That(response.Content.ReadAsStringAsync().Result, Is.Empty,
                    "hook stdout feeds CLI decisions — the listener must stay silent");
                Assert.That(evt.Kind, Is.EqualTo(ConsoleSessionEvent.Attention));
                Assert.That(evt.Message, Is.EqualTo("waiting"));
            });
        }
        finally
        {
            await listener.StopAsync();
        }
    }
}
