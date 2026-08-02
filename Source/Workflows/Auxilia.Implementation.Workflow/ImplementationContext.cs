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
/// Which console a stage drives. Shared = the same console as the preceding stage (context is
/// kept — the default); Fresh = a new instance of the author binding; Reviewer = a driven
/// console on the review-agent binding.
/// </summary>
public static class StageAgents
{
    public const string Shared = "shared";
    public const string Fresh = "fresh";
    public const string Reviewer = "reviewer";
}

/// <summary>Who performs the AI review passes.</summary>
public static class ReviewAgents
{
    /// <summary>The review-agent binding (headless or console per reviewer-mode) — the default.</summary>
    public const string Reviewer = "reviewer";

    /// <summary>The console that refined the story reviews with its full context.</summary>
    public const string RefinementAgent = "refinement-agent";
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
    public bool RefinementCheck { get; init; } = true;
    public bool AiPlanReview { get; init; } = true;
    public bool AiCodeReview { get; init; } = true;
    public int MaxAiReviewRounds { get; init; } = 2;
    public bool UserPlanGate { get; init; } = true;
    public bool UserCodeGate { get; init; } = true;
    public bool ArtifactReview { get; init; }
    public string PushMode { get; init; } = PushModes.Prompt;
    public string ReviewerMode { get; init; } = ReviewerModes.Headless;
    public TimeSpan GateIdleCompaction { get; init; } = TimeSpan.FromMinutes(10);
    public string? TargetState { get; init; }

    /// <summary>The console planning drives — shared with refinement by default.</summary>
    public string PlanAgent { get; init; } = StageAgents.Shared;

    /// <summary>The console implementation drives — shared with planning by default.</summary>
    public string ImplementAgent { get; init; } = StageAgents.Shared;

    /// <summary>Who reviews: the review-agent binding, or the console that refined the story.</summary>
    public string ReviewBy { get; init; } = ReviewAgents.Reviewer;

    public string RefinementInstructions { get; init; } = ImplementationPrompts.Refinement;
    public string PlanInstructions { get; init; } = ImplementationPrompts.Plan;
    public string ImplementInstructions { get; init; } = ImplementationPrompts.Implement;
    public string ReviewInstructions { get; init; } = ImplementationPrompts.Review;

    /// <summary>Base instructions the AUTHOR console always receives; null = none.</summary>
    public string? AuthorBaseInstructions { get; init; }

    /// <summary>Base instructions prepended to every REVIEWER instruction; null = none.</summary>
    public string? ReviewerBaseInstructions { get; init; }

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
            Get("WORKFLOW_CONTEXT__REFINEMENT-CHECK"),
            Get("WORKFLOW_CONTEXT__AI-PLAN-REVIEW"),
            Get("WORKFLOW_CONTEXT__MAX-AI-REVIEW-ROUNDS"),
            Get("WORKFLOW_CONTEXT__USER-PLAN-GATE"),
            Get("WORKFLOW_CONTEXT__USER-CODE-GATE"),
            Get("WORKFLOW_CONTEXT__ARTIFACT-REVIEW"),
            Get("WORKFLOW_CONTEXT__PUSH-MODE"),
            Get("WORKFLOW_CONTEXT__REVIEWER-MODE"),
            Get("WORKFLOW_CONTEXT__GATE-IDLE-COMPACTION"),
            Get("WORKFLOW_CONTEXT__TARGET-STATE"),
            Get("WORKFLOW_CONTEXT__AUTHOR-BASE-PROMPT"),
            Get("WORKFLOW_CONTEXT__REVIEWER-BASE-PROMPT"),
            Get("WORKFLOW_CONTEXT__AI-CODE-REVIEW"),
            Get("WORKFLOW_CONTEXT__PLAN-AGENT"),
            Get("WORKFLOW_CONTEXT__IMPLEMENT-AGENT"),
            Get("WORKFLOW_CONTEXT__REVIEW-BY"),
            Get("WORKFLOW_CONTEXT__REFINEMENT-INSTRUCTIONS"),
            Get("WORKFLOW_CONTEXT__PLAN-INSTRUCTIONS"),
            Get("WORKFLOW_CONTEXT__IMPLEMENT-INSTRUCTIONS"),
            Get("WORKFLOW_CONTEXT__REVIEW-INSTRUCTIONS"));

    private static string? Get(string name) => System.Environment.GetEnvironmentVariable(name);

    public static ImplementationContext FromValues(
        string? workItemId, string? workspaceDirectory, string? outputDirectory,
        string? refinementCheck = null, string? aiPlanReview = null, string? maxRounds = null,
        string? userPlanGate = null, string? userCodeGate = null, string? artifactReview = null,
        string? pushMode = null, string? reviewerMode = null, string? gateIdleCompaction = null,
        string? targetState = null, string? authorBasePrompt = null, string? reviewerBasePrompt = null,
        string? aiCodeReview = null, string? planAgent = null, string? implementAgent = null,
        string? reviewBy = null, string? refinementInstructions = null, string? planInstructions = null,
        string? implementInstructions = null, string? reviewInstructions = null)
    {
        if (workItemId is not { Length: > 0 })
            throw new InvalidOperationException(
                "The dispatch context carries no work-item-id — an implementation run needs its story.");

        return new ImplementationContext(
            workItemId.Trim(),
            Fallback(workspaceDirectory, "implementation-workspace"),
            Fallback(outputDirectory, "implementation-output"))
        {
            RefinementCheck = Toggle(refinementCheck, defaultOn: true),
            AiPlanReview = Toggle(aiPlanReview, defaultOn: true),
            AiCodeReview = Toggle(aiCodeReview, defaultOn: true),
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
            PlanAgent = StageAgent(planAgent),
            ImplementAgent = StageAgent(implementAgent),
            ReviewBy = string.Equals(reviewBy?.Trim(), ReviewAgents.RefinementAgent,
                StringComparison.OrdinalIgnoreCase)
                ? ReviewAgents.RefinementAgent
                : ReviewAgents.Reviewer,
            RefinementInstructions = Instructions(refinementInstructions, ImplementationPrompts.Refinement),
            PlanInstructions = Instructions(planInstructions, ImplementationPrompts.Plan),
            ImplementInstructions = Instructions(implementInstructions, ImplementationPrompts.Implement),
            ReviewInstructions = Instructions(reviewInstructions, ImplementationPrompts.Review),
            AuthorBaseInstructions = authorBasePrompt is { Length: > 0 } ? authorBasePrompt.Trim() : null,
            ReviewerBaseInstructions = reviewerBasePrompt is { Length: > 0 } ? reviewerBasePrompt.Trim() : null,
        };

        static bool Toggle(string? value, bool defaultOn)
            => value?.Trim() is { Length: > 0 } trimmed
                ? bool.TryParse(trimmed, out var parsed) ? parsed : trimmed == "1"
                : defaultOn;

        static string StageAgent(string? value)
            => value?.Trim().ToLowerInvariant() is StageAgents.Fresh or StageAgents.Reviewer
                ? value.Trim().ToLowerInvariant()
                : StageAgents.Shared;

        static string Instructions(string? value, string fallback)
            => value is { Length: > 0 } ? value.Trim() : fallback;

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
