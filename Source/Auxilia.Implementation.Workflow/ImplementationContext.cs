using Auxilia.Workflows;

namespace Auxilia.Implementation.Workflow;

/// <summary>Push behavior after the final gate.</summary>
public static class PushModes
{
    public const string Prompt = "prompt";
    public const string Auto = "auto";
    public const string Skip = "skip";
}

/// <summary>How the AI reviewer runs.</summary>
public static class ReviewerModes
{
    public const string Headless = "headless";
    public const string Console = "console";
}

/// <summary>
/// Everything one implementation run reads from its launch environment: the story, the step
/// toggles, and the directories. Every value comes from a declared run input.
/// </summary>
public sealed record ImplementationContext(
    string WorkItemId,
    string WorkspaceDirectory,
    string OutputDirectory)
{
    public bool CompletenessCheck { get; init; } = true;
    public bool AiReview { get; init; } = true;
    public int MaxAiReviewRounds { get; init; } = 2;
    public bool UserPlanGate { get; init; } = true;
    public bool UserCodeGate { get; init; } = true;
    public bool ArtifactReview { get; init; }
    public string PushMode { get; init; } = PushModes.Prompt;
    public string ReviewerMode { get; init; } = ReviewerModes.Headless;
    public TimeSpan GateIdleCompaction { get; init; } = TimeSpan.FromMinutes(10);
    public string? TargetState { get; init; }

    /// <summary>The file-based exchange between workflow and agent lives here, inside the workspace.</summary>
    public string ExchangeDirectory => Path.Combine(WorkspaceDirectory, ".auxilia");

    public string PlanPath => Path.Combine(ExchangeDirectory, "plan.md");
    public string QuestionsPath => Path.Combine(ExchangeDirectory, "questions.md");
    public string PlanReviewPath => Path.Combine(ExchangeDirectory, "plan-review.md");
    public string CodeReviewPath => Path.Combine(ExchangeDirectory, "code-review.md");

    public static ImplementationContext FromEnvironment()
        => FromValues(
            Get("WORKFLOW_CONTEXT__WORK-ITEM-ID"),
            System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.WorkspaceDirectory),
            System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.OutputDirectory),
            Get("WORKFLOW_CONTEXT__COMPLETENESS-CHECK"),
            Get("WORKFLOW_CONTEXT__AI-REVIEW"),
            Get("WORKFLOW_CONTEXT__MAX-AI-REVIEW-ROUNDS"),
            Get("WORKFLOW_CONTEXT__USER-PLAN-GATE"),
            Get("WORKFLOW_CONTEXT__USER-CODE-GATE"),
            Get("WORKFLOW_CONTEXT__ARTIFACT-REVIEW"),
            Get("WORKFLOW_CONTEXT__PUSH-MODE"),
            Get("WORKFLOW_CONTEXT__REVIEWER-MODE"),
            Get("WORKFLOW_CONTEXT__GATE-IDLE-COMPACTION"),
            Get("WORKFLOW_CONTEXT__TARGET-STATE"));

    private static string? Get(string name) => System.Environment.GetEnvironmentVariable(name);

    public static ImplementationContext FromValues(
        string? workItemId, string? workspaceDirectory, string? outputDirectory,
        string? completenessCheck = null, string? aiReview = null, string? maxRounds = null,
        string? userPlanGate = null, string? userCodeGate = null, string? artifactReview = null,
        string? pushMode = null, string? reviewerMode = null, string? gateIdleCompaction = null,
        string? targetState = null)
    {
        if (workItemId is not { Length: > 0 })
            throw new InvalidOperationException(
                "The dispatch context carries no work-item-id — an implementation run needs its story.");

        return new ImplementationContext(
            workItemId.Trim(),
            Fallback(workspaceDirectory, "implementation-workspace"),
            Fallback(outputDirectory, "implementation-output"))
        {
            CompletenessCheck = Toggle(completenessCheck, defaultOn: true),
            AiReview = Toggle(aiReview, defaultOn: true),
            MaxAiReviewRounds = int.TryParse(maxRounds, out var rounds) && rounds >= 0 ? rounds : 2,
            UserPlanGate = Toggle(userPlanGate, defaultOn: true),
            UserCodeGate = Toggle(userCodeGate, defaultOn: true),
            ArtifactReview = Toggle(artifactReview, defaultOn: false),
            PushMode = pushMode?.Trim().ToLowerInvariant() is PushModes.Auto or PushModes.Skip
                ? pushMode.Trim().ToLowerInvariant()
                : PushModes.Prompt,
            ReviewerMode = string.Equals(
                reviewerMode?.Trim(), ReviewerModes.Console, StringComparison.OrdinalIgnoreCase)
                ? ReviewerModes.Console
                : ReviewerModes.Headless,
            GateIdleCompaction = int.TryParse(gateIdleCompaction, out var minutes) && minutes >= 0
                ? TimeSpan.FromMinutes(minutes)
                : TimeSpan.FromMinutes(10),
            TargetState = targetState is { Length: > 0 } ? targetState.Trim() : null,
        };

        static bool Toggle(string? value, bool defaultOn)
            => value?.Trim() is { Length: > 0 } trimmed
                ? bool.TryParse(trimmed, out var parsed) ? parsed : trimmed == "1"
                : defaultOn;

        static string Fallback(string? configured, string tempName)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;
            var fallback = Path.Combine(Path.GetTempPath(), $"{tempName}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    /// <summary>The old generation's branch naming, kept: impl/{id}-{slug}.</summary>
    public string BranchNameFor(string title)
    {
        var slug = System.Text.RegularExpressions.Regex
            .Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 50)
            slug = slug[..50].TrimEnd('-');
        return slug.Length == 0 ? $"impl/{WorkItemId}" : $"impl/{WorkItemId}-{slug}";
    }
}
