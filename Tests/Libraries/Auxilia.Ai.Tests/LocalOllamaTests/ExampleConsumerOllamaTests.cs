using Auxilia.AI;
using Auxilia.AI.AgenticFramework.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Ai.Tests.LocalOllamaTests;

/// <summary>
/// Manual integration smoke-tests that talk directly to the locally running Ollama instance.
/// These tests are marked <c>[Explicit]</c> so they are NEVER run automatically (pre-commit,
/// CI, etc.).  Trigger them by hand from the IDE or with:
/// <code>
///   dotnet test --filter "Name~OllamaTests" --no-build
/// </code>
/// </summary>
[TestFixture]
[Explicit("Requires a local Ollama instance on http://localhost:11434")]
[Category("LocalOllama")]
public class ExampleConsumerOllamaTests
{
    private ServiceProvider _serviceProvider = null!;
    private ExampleAiSessionConsumer _consumer = null!;

    [OneTimeSetUp]
    public void BuildContainer()
    {
        var services = new ServiceCollection();

        // Supply an empty IConfiguration so BindConfiguration() inside
        // AddOllamaAgentFramework doesn't throw; the configure delegate below
        // then overrides every value we care about.
        services.AddSingleton<IConfiguration>(_ => new ConfigurationBuilder().Build());

        services.AddOllamaAgentFramework(settings =>
        {
            settings.OllamaBaseUrl = "http://localhost:11434";
            // Use a small, fast model for the smoke test.
            // Change to whichever model you have pulled locally.
            settings.DefaultModel = "deepseek-r1:8b";
        });

        _serviceProvider = services.BuildServiceProvider();

        var builder = _serviceProvider.GetRequiredService<IAgentSessionBuilder>();
        _consumer = new ExampleAiSessionConsumer(builder);
    }

    [OneTimeTearDown]
    public async Task DisposeContainer()
    {
        await _serviceProvider.DisposeAsync();
    }

    [Test]
    [CancelAfter(120_000)] // 2-minute safety net – Ollama can be slow on first run
    public async Task FindAgentsFavoriteColor_ReturnsValidRgbColor(CancellationToken cancellationToken)
    {
        var colorName = await _consumer.FindAgentsFavoriteColor(cancellationToken);

        Assert.That(colorName, Is.Not.Null.And.Not.Empty,
            "Expected the agent to return a non-empty color name.");

        // Verify the JSON file was actually written with valid values.
        Assert.That(File.Exists("C:/Temp/favColor.json"),
            "Expected the agent to create C:/Temp/favColor.json");

        var json = await File.ReadAllTextAsync("C:/Temp/favColor.json", cancellationToken);
        Assert.That(json, Is.Not.Empty, "favColor.json should not be empty.");

        // Optionally print the raw output so you can inspect it in the test runner.
        TestContext.Out.WriteLine($"Color name : {colorName}");
        TestContext.Out.WriteLine($"JSON output: {json}");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task AgentSession_PublishesEvents_DuringStreaming(CancellationToken cancellationToken)
    {
        var eventsReceived = new List<AgentEvent>();

        using var session = await _serviceProvider
            .GetRequiredService<IAgentSessionBuilder>()
            .WithSystemPrompt("You are a helpful assistant. Reply concisely.")
            .BuildAsync();

        using var _ = session.Events.Subscribe(e => eventsReceived.Add(e));

        var request = session.PrepareRequest("Say hello in exactly three words.");
        await request.ExecuteRequestAsync(new NoOpValidator(), cancellationToken);

        Assert.That(eventsReceived, Is.Not.Empty,
            "Expected at least one AgentEvent to be published via the Observable.");

        var chunks = eventsReceived.OfType<AgentResponseChunkEvent>().ToList();
        Assert.That(chunks, Is.Not.Empty,
            "Expected at least one AgentResponseChunkEvent to be published.");

        TestContext.Out.WriteLine($"Total events : {eventsReceived.Count}");
        TestContext.Out.WriteLine($"Text chunks  : {chunks.Count}");
        TestContext.Out.WriteLine($"Full response: {string.Concat(chunks.Select(c => c.Chunk))}");
    }
}

/// <summary>A pass-through validator used when we only care about side-effects.</summary>
file sealed class NoOpValidator : IAgentResultValidator<string>
{
    public Task<string> ValidateAsync(IAgentRequest request, string agentTextOutput)
        => Task.FromResult(agentTextOutput);
}








