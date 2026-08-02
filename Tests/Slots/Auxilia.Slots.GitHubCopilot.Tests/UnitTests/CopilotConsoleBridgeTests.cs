using System.Text;
using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.GitHubCopilot.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class CopilotConsoleBridgeTests
{
    [Test]
    public void Parse_MapsTheHookWirePayloads_ToSessionEvents()
    {
        Assert.Multiple(() =>
        {
            var stop = CopilotConsoleBridge.Parse("{\"hook_event_name\":\"Stop\"}");
            Assert.That(stop!.Kind, Is.EqualTo(ConsoleSessionEvent.TurnEnded));

            var attention = CopilotConsoleBridge.Parse(
                "{\"hook_event_name\":\"Notification\",\"message\":\"waiting\"}");
            Assert.That(attention!.Kind, Is.EqualTo(ConsoleSessionEvent.Attention));
            Assert.That(attention.Message, Is.EqualTo("waiting"));

            var started = CopilotConsoleBridge.Parse(
                "{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"bash\"}");
            Assert.That(started!.Kind, Is.EqualTo(ConsoleSessionEvent.ToolStarted));
            Assert.That(started.ToolName, Is.EqualTo("bash"));

            var finished = CopilotConsoleBridge.Parse(
                "{\"hook_event_name\":\"PostToolUse\",\"tool_name\":\"bash\"}");
            Assert.That(finished!.Kind, Is.EqualTo(ConsoleSessionEvent.ToolFinished));

            Assert.That(CopilotConsoleBridge.Parse("{\"hook_event_name\":\"SomethingNew\"}"), Is.Null,
                "unknown payloads are ignored, never an error");
            Assert.That(CopilotConsoleBridge.Parse("not json"), Is.Null);
        });
    }

    [Test]
    public async Task Prepare_AnnouncesTheListenerPort_WhereTheStubReadsIt()
    {
        var home = Directory.CreateTempSubdirectory("copilot-bridge-").FullName;
        var bridge = new CopilotConsoleBridge { HomeDirectory = home };
        try
        {
            await bridge.StartAsync((_, _) => Task.CompletedTask, CancellationToken.None);
            await bridge.PrepareAsync("/workspace", CancellationToken.None);

            var announced = await File.ReadAllTextAsync(
                Path.Combine(home, CopilotConsoleBridge.PortFileRelativePath));
            Assert.That(announced, Is.EqualTo(bridge.Port.ToString()));
        }
        finally
        {
            await bridge.StopAsync();
            Directory.Delete(home, recursive: true);
        }
    }

    [Test]
    public async Task Bridge_ReceivesPostedPayloads_AndAlwaysAnswersEmpty200()
    {
        var bridge = new CopilotConsoleBridge();
        var received = new TaskCompletionSource<ConsoleSessionEvent>();
        await bridge.StartAsync((evt, _) =>
        {
            received.TrySetResult(evt);
            return Task.CompletedTask;
        }, CancellationToken.None);

        try
        {
            using var http = new HttpClient();
            var response = await http.PostAsync(
                $"http://127.0.0.1:{bridge.Port}/",
                new StringContent("{\"hook_event_name\":\"Stop\"}", Encoding.UTF8, "application/json"));

            var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That((int)response.StatusCode, Is.EqualTo(200));
                Assert.That(response.Content.ReadAsStringAsync().Result, Is.Empty,
                    "the wire is observe-only — the listener must stay silent");
                Assert.That(evt.Kind, Is.EqualTo(ConsoleSessionEvent.TurnEnded));
            });
        }
        finally
        {
            await bridge.StopAsync();
        }
    }

    [Test]
    public void CodingAgentSlot_RegistersTheDrivenConsoleSeams()
    {
        var services = new ServiceCollection();
        new CopilotCliSlotHandler().Register(services, "coding-agent", typeof(ICodingAgent),
            new SlotConfiguration("github-copilot-cli", new Dictionary<string, string>
            {
                ["token"] = "gh-test-token"
            }));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Multiple(() =>
        {
            Assert.That(scope.ServiceProvider.GetService<IConsoleSessionEventSource>(),
                Is.InstanceOf<CopilotConsoleBridge>());
            Assert.That(scope.ServiceProvider.GetService<IConsoleSessionPreparer>(),
                Is.SameAs(scope.ServiceProvider.GetService<IConsoleSessionEventSource>()),
                "one bridge instance serves both seams — preparation announces ITS port");
        });
    }
}
