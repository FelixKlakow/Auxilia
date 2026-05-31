using System.Text.Json;
using System.Text.Json.Serialization;
using Auxilia.ImplementationWorkflow.Context;
using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.PullRequestAccess.Mcp;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.SourceControl.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auxilia.ImplementationWorkflow;

public sealed class ReviewerOrchestrator(
    [FromKeyedServices("reviewer-agent")] IAiAgent reviewerAgent,
    [FromKeyedServices("repository")] ISourceControlAccess repository,
    [FromKeyedServices("pull-request")] IPullRequestAccess pullRequestAccess,
    ImplementationWorkflowConfiguration configuration,
    ILoggerFactory? loggerFactory = null)
{
    public async Task<IReadOnlyList<ReviewNote>> RunAsync(
        AgentCompletionResult agentResult,
        ImplementationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.ReviewerEnabled)
            return [];

        var scmTools = new SourceControlAccessMcpTools("repository", repository, loggerFactory);
        var prTools = new PullRequestAccessMcpTools("pull-request", pullRequestAccess, loggerFactory);

        try
        {
            await scmTools.StartAsync(new HttpMcpTransportConfig("http://localhost:0/mcp", "repository"), cancellationToken);
            await prTools.StartAsync(new HttpMcpTransportConfig("http://localhost:0/mcp", "pull-request"), cancellationToken);

            var options = new AiSessionOptions
            {
                SystemPrompt = "You are an expert code reviewer. Review the implementation for quality, correctness, and adherence to acceptance criteria. Return a JSON array of issues found.",
                CapabilityTools = [scmTools, prTools]
            };

            await using var session = await reviewerAgent.OpenSessionAsync(options, cancellationToken);

            var prompt = BuildReviewerPrompt(context);
            var response = await session.ExecuteAsync(prompt, cancellationToken);

            return ParseReviewNotes(response, loggerFactory?.CreateLogger<ReviewerOrchestrator>());
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await scmTools.StopAsync(stopCts.Token);
            await prTools.StopAsync(stopCts.Token);
        }
    }

    private static string BuildReviewerPrompt(ImplementationContext context)
    {
        var desc = string.IsNullOrEmpty(context.WorkItem.Description)
            ? "(no description)"
            : context.WorkItem.Description;

        return $"""
            ## Code Review Task

            **Work Item:** {context.WorkItem.Title}
            **Description:** {desc}

            Please review the implementation on the current branch. Use the repository tools to read changed files.
            Return a JSON array of issues found. Each issue should have:
            - "description": string — describe the issue
            - "filePath": string or null — file path if applicable
            - "severity": string — one of: "info", "warning", "error"

            If there are no issues, return an empty array: []

            Respond with only the JSON array, no additional text.
            """;
    }

    private static IReadOnlyList<ReviewNote> ParseReviewNotes(string response, ILogger? logger)
    {
        try
        {
            var trimmed = response.Trim();
            var start = trimmed.IndexOf('[');
            var end = trimmed.LastIndexOf(']');
            if (start < 0 || end < 0)
                return [];

            var json = trimmed[start..(end + 1)];
            var dtos = JsonSerializer.Deserialize<List<ReviewNoteDto>>(json, _jsonOptions);
            if (dtos is null)
                return [];

            return dtos.Select(d => new ReviewNote(d.Description, d.FilePath, Enum.Parse<ReviewNoteSeverity>(d.Severity, ignoreCase: true))).ToList();
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Failed to parse reviewer JSON response; treating as no issues.");
            return [];
        }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record ReviewNoteDto(
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("filePath")] string? FilePath,
        [property: JsonPropertyName("severity")] string Severity);
}
