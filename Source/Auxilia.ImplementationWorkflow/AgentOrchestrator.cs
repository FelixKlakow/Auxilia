using System.Net;
using System.Net.Sockets;
using Auxilia.ImplementationWorkflow.Context;
using Auxilia.ImplementationWorkflow.Signals;
using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.SourceControl.Mcp;
using Auxilia.Workflows.TaskSource;
using Auxilia.Workflows.TaskSource.Mcp;
using Auxilia.Workflows.TestRunner;
using Auxilia.Workflows.TestRunner.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auxilia.ImplementationWorkflow;

public sealed class AgentOrchestrator(
    [FromKeyedServices("implementation-agent")] IAiAgent agent,
    [FromKeyedServices("repository")] ISourceControlWriteAccess repository,
    [FromKeyedServices("test-runner")] ITestRunner testRunner,
    [FromKeyedServices("task-source")] ITaskSourceAccess taskSource,
    ISignalEmitter signalEmitter,
    ILoggerFactory? loggerFactory = null)
{
    public async Task<AgentCompletionResult> RunAsync(ImplementationContext context, CancellationToken cancellationToken = default)
    {
        var scmTools = new SourceControlWriteAccessMcpTools("repository", repository, loggerFactory);
        var testRunnerTools = new TestRunnerMcpTools("test-runner", testRunner, loggerFactory);
        var taskSourceTools = new TaskSourceMcpTools("task-source", taskSource, loggerFactory);

        try
        {
            await scmTools.StartAsync(new HttpMcpTransportConfig("http://localhost:0/mcp", "repository"), cancellationToken);
            await testRunnerTools.StartAsync(new HttpMcpTransportConfig("http://localhost:0/mcp", "test-runner"), cancellationToken);
            await taskSourceTools.StartAsync(new HttpMcpTransportConfig("http://localhost:0/mcp", "task-source"), cancellationToken);

            var options = new AiSessionOptions
            {
                SystemPrompt = BuildSystemPrompt(context),
                CapabilityTools = [scmTools, testRunnerTools, taskSourceTools]
            };

            try
            {
                await using var session = await agent.OpenSessionAsync(options, cancellationToken);
                var prompt = BuildImplementationPrompt(context);
                var response = await session.ExecuteAsync(prompt, cancellationToken);
                return new AgentCompletionResult(response, context.BranchName, context.WorkItem.Id);
            }
            catch (Exception ex)
            {
                await signalEmitter.EmitAsync("Failed", new FailedSignalPayload
                {
                    FailureReason = ex.Message,
                    WorkItemId = context.WorkItem.Id,
                    PartialBranchName = context.BranchName
                }, cancellationToken);
                throw;
            }
        }
        finally
        {
            await scmTools.StopAsync(CancellationToken.None);
            await testRunnerTools.StopAsync(CancellationToken.None);
            await taskSourceTools.StopAsync(CancellationToken.None);
        }
    }

    private static string BuildSystemPrompt(ImplementationContext context)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(context.InstructionsContent))
        {
            parts.Add(context.InstructionsContent);
            parts.Add(string.Empty);
        }

        parts.Add(
            "You are an expert software engineer. Your task is to implement the requested changes " +
            "using the provided source control, test runner, and task source tools. " +
            "Write clean, idiomatic code that follows existing conventions. " +
            "Use the repository tools to read files, write changes, run tests to verify correctness, " +
            "commit your changes with a descriptive message, and push the branch when done.");

        return string.Join("\n", parts);
    }

    private static string BuildImplementationPrompt(ImplementationContext context)
    {
        var desc = string.IsNullOrEmpty(context.WorkItem.Description)
            ? "(no description)"
            : context.WorkItem.Description;

        return $"""
            ## Work Item: {context.WorkItem.Title}

            **ID:** {context.WorkItem.Id}

            **Description:**
            {desc}

            ## Instructions

            Please implement the work item described above. Follow these steps:
            1. Read the relevant existing source files using the repository tools.
            2. Implement the required changes, writing files using the repository write tools.
            3. Run the tests using the test-runner.run_tests tool to verify correctness. Fix any failures before proceeding.
            4. Commit all changes with a descriptive commit message using the repository.commit tool.
            5. Push the branch using the repository.push tool.

            Do NOT open a pull request — that will be handled separately.
            """;
    }
}
