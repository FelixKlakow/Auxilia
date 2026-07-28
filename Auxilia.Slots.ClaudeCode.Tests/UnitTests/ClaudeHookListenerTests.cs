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
    public void Interpret_FoldsPlanToolCalls_IntoPlanSnapshots()
    {
        var listener = new ClaudeHookListener();

        var todoWrite = listener.Interpret(
            "{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"TodoWrite\",\"tool_input\":"
            + "{\"todos\":[{\"content\":\"step one\",\"status\":\"in_progress\"},{\"content\":\"step two\",\"status\":\"pending\"}]}}");
        var taskUpdate = listener.Interpret(
            "{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"TaskUpdate\",\"tool_input\":"
            + "{\"taskId\":\"1\",\"status\":\"completed\"}}");

        Assert.Multiple(() =>
        {
            var snapshot = todoWrite.Single(e => e.Kind == ConsoleSessionEvent.PlanUpdated);
            Assert.That(snapshot.DetailJson, Does.Contain("step one").And.Contain("in_progress"));
            Assert.That(todoWrite.Any(e => e.Kind == ConsoleSessionEvent.ToolStarted), Is.True,
                "the plan call still counts as tool activity");

            var updated = taskUpdate.Single(e => e.Kind == ConsoleSessionEvent.PlanUpdated);
            Assert.That(updated.DetailJson, Does.Contain("step one").And.Contain("completed"),
                "TaskUpdate folds incrementally onto the tracked snapshot");
        });
    }

    [Test]
    public void Interpret_AskUserQuestion_RaisesImmediateAttention()
    {
        // The CLI's own Notification hook only fires for questions after its idle threshold —
        // the question tool call itself is the immediate cue.
        var events = new ClaudeHookListener().Interpret(
            "{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"AskUserQuestion\",\"tool_input\":"
            + "{\"questions\":[{\"question\":\"Coffee or tea?\"}]}}");

        var attention = events.Single(e => e.Kind == ConsoleSessionEvent.Attention);
        Assert.That(attention.Message, Is.EqualTo("Claude asks: Coffee or tea?"));
    }

    [Test]
    public void Interpret_TurnEnded_CarriesTheTranscriptsClosingAssistantText()
    {
        var transcript = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(transcript,
        [
            "{\"type\":\"user\",\"message\":{\"content\":\"do it\"}}",
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"working on it\"}]}}",
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"}]}}",
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"All done — tests pass.\"}]}}",
        ]);
        try
        {
            var events = new ClaudeHookListener().Interpret(
                $"{{\"hook_event_name\":\"Stop\",\"transcript_path\":{System.Text.Json.JsonSerializer.Serialize(transcript)}}}");

            var turn = events.Single();
            Assert.Multiple(() =>
            {
                Assert.That(turn.Kind, Is.EqualTo(ConsoleSessionEvent.TurnEnded));
                Assert.That(turn.Message, Is.EqualTo("All done — tests pass."),
                    "the LAST assistant text is the turn's message");
            });
        }
        finally
        {
            File.Delete(transcript);
        }
    }

    [Test]
    public void Interpret_TurnEnded_WithoutReadableTranscript_StillFiresTheCue()
    {
        var events = new ClaudeHookListener().Interpret(
            "{\"hook_event_name\":\"Stop\",\"transcript_path\":\"/nonexistent/t.jsonl\"}");

        Assert.That(events.Single().Kind, Is.EqualTo(ConsoleSessionEvent.TurnEnded));
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
