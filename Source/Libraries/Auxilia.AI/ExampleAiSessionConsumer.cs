using System.Text.Json;

namespace Auxilia.AI;

public class ExampleAiSessionConsumer
{
    private IAgentSessionBuilder _sessionBuilder;


    public ExampleAiSessionConsumer(IAgentSessionBuilder sessionBuilder)
    {
        _sessionBuilder = sessionBuilder;
    }

    public async Task<string> FindAgentsFavoriteColor(CancellationToken cancellationToken)
    {
        using var session = await _sessionBuilder
            .WithSystemPrompt("You are an example agent in a test environment of the Auxilia AI Agent Orchestration System. You are triggered autonomously by the algorithmic section of Auxilia, finish the task given autonomously, interactive communication with a human is not possible.")
            //.WithMcpServerTools("http://localhost:5080/mcp/randomNumber","random-number-mcp")
            .WithRootDirectory("C:/Temp")
            .BuildAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var request = session.PrepareRequest("Create a json file at C:/Temp/favColor.json describing a favorite color of yours or whichever color you think of first. It should have the properties \"red\", \"green\", \"blue\" as scalar RGB Values between 0.0 and 1.0, also add a property \"colorName\" to give a named description of the color. Which one is picked does not really matter, we're merely testing the capabilities of interaction between neuronal AI and algorithmic systems by creating structured output data");
        request.WithNonDefaultModel("ministral-3");
        ExampleAgentResult result;
        int i = 0;
        do
        {
            result = await request.ExecuteRequestAsync(new ExampleValidator(), cancellationToken);
            request = session.PrepareRequest("The file does not exist or is not formatted correctly, try again!");
            i++;
        } while (!result.IsValid || i >= 3);

        return result.ColorName;
    }
}
public record ExampleAgentResult(double Red, double Green, double Blue, string ColorName, bool IsValid);

public class ExampleValidator : IAgentResultValidator<ExampleAgentResult>
{
    public async Task<ExampleAgentResult> ValidateAsync(IAgentRequest request, string agentTextOutput)
    {
        if (!File.Exists("C:/Temp/favColor.json"))
        {
            return new ExampleAgentResult(0, 0, 0, string.Empty, false);
        }

        var result = await JsonSerializer.DeserializeAsync<ExampleAgentResult>(File.OpenRead("C:/Temp/favColor.json"));
        if (result is null)
        {
            return new ExampleAgentResult(0, 0, 0, string.Empty, false);
        }

        return result with { IsValid = true };
    }
}