using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.Steering;
using Auxilia.Workflows.TaskSource;
using Auxilia.Workflows.Views;

namespace Auxilia.Implementation.Workflow;

/// <summary>
/// The assisted-delivery pipeline (docs/implementation-workflow-design.md): driven consoles
/// span refinement, plan, and implementation — ONE shared author console by default (context
/// never lost), per-stage overrides for a fresh instance or the reviewer binding. AI review
/// passes and operator gates sit between the phases; the WORKFLOW commits and pushes; the
/// story's state is set from its source's own vocabulary. Every gate obeys the idle-compaction
/// rule so a slow operator never forces a full context re-read.
/// </summary>
public sealed class ImplementationPipeline(
    IWorkItemAccess workItems,
    IViewPublisher? views,
    IWorkflowInputs? inputs,
    ImplementationContext context,
    AgentConsolePool consoles,
    IReviewRunner? reviewer,
    IGitRunner git,
    TimeProvider time)
{
    private OperatorChannel? _channel;
    private List<WorkflowStepState> _flow = [];

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
        var workItem = await workItems.GetWorkItemAsync(context.WorkItemId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Work item '{context.WorkItemId}' was not found at the bound source.");
        await ProgressAsync("story", $"implementing '{workItem.Title}' ({workItem.Id})", cancellationToken);
        await ChatAsync(AgentChatRole.User,
            $"# {workItem.Title}\n\n{workItem.Description}", "User story", cancellationToken);

        if (views is not null && inputs is not null)
            _channel = await OperatorChannel.StartAsync(views, inputs, cancellationToken);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _channel?.HaltToken ?? CancellationToken.None);
        var ct = session.Token;
        var success = false;
        string? error = null;
        try
        {
            Directory.CreateDirectory(context.ExchangeDirectory);
            // The exchange directory and MCP registration are RUN mechanics, not story
            // content — repo-locally excluded so review bundles stay clean and the final
            // commit never picks them up.
            if (Directory.Exists(Path.Combine(context.WorkspaceDirectory, ".git")))
            {
                var exclude = Path.Combine(context.WorkspaceDirectory, ".git", "info", "exclude");
                Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
                await File.AppendAllTextAsync(exclude, "\n.auxilia/\n.mcp.json\n", ct);
            }
            await PublishFlowAsync(activeStep: "workspace", ct);
            var branch = context.BranchNameFor(workItem.Title);
            await git.RunAsync(context.WorkspaceDirectory, $"checkout -b {branch}", ct);
            await ProgressAsync("workspace", $"branch '{branch}' ready", ct);

            await consoles.StartAuthorAsync(ct);
            await ProgressAsync("session", "author console up — visible in the run terminal", ct);

            await MaterializeStoryAsync(workItem, ct);
            if (context.RefinementCheck)
            {
                await PublishFlowAsync("refinement", ct);
                await RefinementLoopAsync(workItem, ct);
            }

            await PublishFlowAsync("plan", ct);
            var planConsole = StageConsole(context.PlanAgent, consoles.Author, "plan");
            var plan = await PlanLoopAsync(planConsole, ct);
            await PublishFlowAsync("implement", ct);
            var implementConsole = StageConsole(context.ImplementAgent, planConsole, "implement");
            await ImplementationLoopAsync(implementConsole, plan, ct);

            await PublishFlowAsync("finalization", ct);
            var pushed = await PushAsync(workItem, branch, ct);
            await SetStoryStateAsync(workItem, branch, pushed, ct);
            await PublishFlowAsync(activeStep: null, ct);
            success = true;
        }
        catch (OperationCanceledException) when (_channel?.HaltToken.IsCancellationRequested == true)
        {
            error = "The run was halted by the operator.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
        }
        finally
        {
            await consoles.DisposeAsync();
            var ended = time.GetUtcNow();
            await new SessionReportWriter(context.OutputDirectory).WriteAsync(
                new SessionReport(
                    $"Implement {workItem.Id}: {workItem.Title}", success,
                    success ? "Implementation run completed." : null,
                    TurnCount: 0, TotalCostUsd: null,
                    (long)(ended - started).TotalMilliseconds, error, ended),
                cancellationToken);
            await ProgressAsync("session", success ? "completed" : $"failed — {error}", cancellationToken);
            if (_channel is not null)
            {
                await _channel.EndSessionAsync(success, error, cancellationToken);
                await _channel.DisposeAsync();
            }
        }

        if (!success)
            throw new InvalidOperationException(error ?? "The implementation run failed.");
    }

    /// <summary>Resolves which console a stage drives; Shared = the preceding stage's console.</summary>
    private AgentConsolePool.AgentConsole StageConsole(
        string choice, AgentConsolePool.AgentConsole shared, string stage) => choice switch
    {
        StageAgents.Fresh => consoles.FreshAuthor(stage),
        StageAgents.Reviewer => consoles.ReviewerConsole(),
        _ => shared
    };

    /// <summary>
    /// The reviewer for the AI passes: the review-agent binding by default, or — review-by
    /// refinement-agent — the console that refined the story, driven with review instructions.
    /// </summary>
    private IReviewRunner? ResolveReviewer()
        => context.ReviewBy == ReviewAgents.RefinementAgent
            ? new DrivenReviewRunner(consoles, consoles.Author)
            : reviewer;

    /// <summary>
    /// The run's step STATES against the declared flow (full snapshot per transition):
    /// everything BEFORE the active step becomes done, toggled-off steps stay "skipped", a null
    /// active step marks the whole flow done. Labels and descriptions ride the schema.
    /// </summary>
    private async Task PublishFlowAsync(string? activeStep, CancellationToken ct)
    {
        if (views is null)
            return;
        if (_flow.Count == 0)
            _flow = ImplementationFlow.Steps
                .Select(step => new WorkflowStepState(step.Id, IsSkipped(step.Id) ? "skipped" : "pending"))
                .ToList();

        var reached = activeStep is null
            ? _flow.Count
            : _flow.FindIndex(s => s.Id == activeStep);
        _flow = _flow.Select((step, index) => step.State == "skipped"
                ? step
                : step with
                {
                    State = index < reached ? "done" : index == reached ? "active" : "pending"
                })
            .ToList();
        await views.PublishAsync(WorkflowStepFlow.ViewName, new WorkflowStepFlow(_flow), ct);
    }

    private bool IsSkipped(string stepId) => stepId switch
    {
        "refinement" => !context.RefinementCheck,
        _ => false
    };

    /// <summary>The story lands as a FILE so drives can reference it without re-pasting it.</summary>
    private async Task MaterializeStoryAsync(WorkItem workItem, CancellationToken ct)
    {
        var storyPath = Path.Combine(context.ExchangeDirectory, "story.md");
        await File.WriteAllTextAsync(storyPath,
            $"# {workItem.Title}\n\nId: {workItem.Id}\nState: {workItem.Status}\n\n{workItem.Description}\n", ct);
        foreach (var attachment in await workItems.GetAttachmentsAsync(workItem.Id, ct))
        {
            var safe = Path.GetFileName(attachment.FileName);
            if (safe.Length > 0)
                await File.WriteAllBytesAsync(
                    Path.Combine(context.ExchangeDirectory, safe), attachment.Content, ct);
        }
    }

    private async Task RefinementLoopAsync(WorkItem workItem, CancellationToken ct)
    {
        for (var round = 0; round < 2; round++)
        {
            await DriveAsync(consoles.Author,
                $"{context.RefinementInstructions}\n"
                + $"The story is at {Rel(context.ExchangeDirectory)}/story.md (attachments beside it). "
                + $"Write any open questions to {Rel(context.QuestionsPath)}, one per line; "
                + "if nothing is missing, write exactly NONE.",
                ct);
            var questions = ReadLines(context.QuestionsPath)
                .Where(l => !string.Equals(l, "NONE", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (questions.Count == 0 || _channel is null)
                return;

            await ProgressAsync("refinement", $"{questions.Count} open question(s) for the operator", ct);
            var answers = await GateAsync(questions
                .Select((q, i) => new OperatorQuestion(
                    $"q{i}", q, [], MultiSelect: false, AllowFreeText: true, Detail: null))
                .ToList(), ct);
            var answered = string.Join("\n", answers.Select(a =>
                $"- {questions.ElementAtOrDefault(int.TryParse(a.QuestionId.TrimStart('q'), out var i) ? i : 0)}: "
                + (a.FreeText ?? string.Join(", ", a.SelectedIds))));
            await DriveAsync(consoles.Author,
                $"The operator answered the open questions:\n{answered}\n"
                + $"Update {Rel(context.ExchangeDirectory)}/story.md accordingly, then re-assess the story "
                + $"the same way (questions to {Rel(context.QuestionsPath)} or NONE).", ct);
            var remaining = ReadLines(context.QuestionsPath)
                .Where(l => !string.Equals(l, "NONE", StringComparison.OrdinalIgnoreCase)).ToList();
            if (remaining.Count == 0)
                return;
        }
    }

    private async Task<string> PlanLoopAsync(AgentConsolePool.AgentConsole console, CancellationToken ct)
    {
        var prompt = $"{context.PlanInstructions}\n"
                     + $"The story is at {Rel(context.ExchangeDirectory)}/story.md. "
                     + $"Write the plan (markdown) to {Rel(context.PlanPath)}.";
        while (true)
        {
            await DriveAsync(console, prompt, ct);
            var plan = await ReadFileAsync(context.PlanPath, ct)
                ?? throw new InvalidOperationException("The author produced no plan file.");
            await ChatAsync(AgentChatRole.Assistant, plan, "Implementation plan", ct);
            PersistPlanArtifact();

            if (context.AiPlanReview && ResolveReviewer() is { } planReviewer)
                plan = await AiReviewLoopAsync(planReviewer, console, plan, ct);

            if (!context.UserPlanGate || _channel is null)
                return plan;
            var answer = (await GateAsync([ApprovalQuestion(
                "plan-gate", "Approve the implementation plan?", plan)], ct)).FirstOrDefault();
            if (IsApproved(answer))
                return plan;
            prompt = $"The operator wants the plan revised:\n{FeedbackOf(answer)}\n"
                     + $"Rework {Rel(context.PlanPath)} accordingly. Do NOT implement yet.";
        }
    }

    private async Task ImplementationLoopAsync(
        AgentConsolePool.AgentConsole console, string plan, CancellationToken ct)
    {
        var prompt = $"{context.ImplementInstructions}\n"
                     + $"The approved plan is at {Rel(context.PlanPath)}.";
        while (true)
        {
            await DriveAsync(console, prompt, ct);

            if (context.AiCodeReview && ResolveReviewer() is { } codeReviewer)
            {
                var verdict = await AiCodeReviewAsync(codeReviewer, console, ct);
                if (!verdict.Approved && context.MaxAiReviewRounds > 0)
                {
                    // Bounded refinement happened inside AiCodeReviewAsync; a still-failing
                    // verdict surfaces to the operator instead of looping forever.
                    await ChatAsync(AgentChatRole.System, verdict.Notes, "AI code review (unresolved)", ct);
                }
            }

            var bundle = await BuildReviewBundleAsync(plan, ct);
            if (!context.UserCodeGate || _channel is null)
                return;
            var answer = (await GateAsync([ApprovalQuestion(
                "code-gate", "Approve the implementation?", Truncate(bundle, 6000))], ct)).FirstOrDefault();
            if (IsApproved(answer))
            {
                if (!context.ArtifactReview)
                    return;
                var artifactAnswer = (await GateAsync([ApprovalQuestion(
                    "artifact-gate", "Approve the review bundle (diff, plan, verdicts)?",
                    Truncate(bundle, 6000))], ct)).FirstOrDefault();
                if (IsApproved(artifactAnswer))
                    return;
                prompt = "The operator rejected the review bundle:\n" + FeedbackOf(artifactAnswer)
                         + "\nAddress the feedback in the implementation.";
                continue;
            }

            prompt = $"The operator wants the implementation revised:\n{FeedbackOf(answer)}\n"
                     + "Address the feedback; run affected tests; commit nothing.";
        }
    }

    /// <summary>Bounded refine loop: review → REVISE feedback driven into the author → re-review.</summary>
    private async Task<string> AiReviewLoopAsync(
        IReviewRunner planReviewer, AgentConsolePool.AgentConsole console, string plan, CancellationToken ct)
    {
        for (var round = 0; round < context.MaxAiReviewRounds; round++)
        {
            var verdict = await planReviewer.ReviewAsync(
                WithReviewerBase($"{context.ReviewInstructions} "
                + $"Review the implementation plan at {Rel(context.PlanPath)} against the story at "
                + $"{Rel(context.ExchangeDirectory)}/story.md. Write your verdict to "
                + $"{Rel(context.PlanReviewPath)}: first line APPROVE or REVISE, then your notes."),
                context.PlanReviewPath, ct);
            await ChatAsync(AgentChatRole.System, verdict.Notes,
                verdict.Approved ? "AI plan review — approved" : "AI plan review — revise", ct);
            if (verdict.Approved)
                return plan;
            await DriveAsync(console,
                $"A reviewer asks for plan changes:\n{verdict.Notes}\nRework {Rel(context.PlanPath)}.", ct);
            plan = await ReadFileAsync(context.PlanPath, ct) ?? plan;
            await ChatAsync(AgentChatRole.Assistant, plan, "Implementation plan (revised)", ct);
            PersistPlanArtifact();
        }
        return plan;
    }

    private async Task<ReviewVerdict> AiCodeReviewAsync(
        IReviewRunner codeReviewer, AgentConsolePool.AgentConsole console, CancellationToken ct)
    {
        ReviewVerdict verdict = new(false, "no review ran");
        for (var round = 0; round < context.MaxAiReviewRounds; round++)
        {
            verdict = await codeReviewer.ReviewAsync(
                WithReviewerBase($"{context.ReviewInstructions} "
                + $"Review the UNCOMMITTED changes in this repository (git diff / git status) against the "
                + $"plan at {Rel(context.PlanPath)}. Write your verdict to {Rel(context.CodeReviewPath)}: "
                + "first line APPROVE or REVISE, then concrete findings."),
                context.CodeReviewPath, ct);
            await ChatAsync(AgentChatRole.System, verdict.Notes,
                verdict.Approved ? "AI code review — approved" : "AI code review — revise", ct);
            if (verdict.Approved)
                return verdict;
            await DriveAsync(console,
                $"A code reviewer found issues:\n{verdict.Notes}\nFix them; run affected tests; commit nothing.",
                ct);
        }
        return verdict;
    }

    private async Task<string> BuildReviewBundleAsync(string plan, CancellationToken ct)
    {
        var (_, status) = await git.RunAsync(context.WorkspaceDirectory, "status --porcelain", ct);
        var (_, diff) = await git.RunAsync(context.WorkspaceDirectory, "diff", ct);
        var planVerdict = await ReadFileAsync(context.PlanReviewPath, ct);
        var codeVerdict = await ReadFileAsync(context.CodeReviewPath, ct);
        var bundle = $"# Review bundle — {context.WorkItemId}\n\n"
                     + $"## Changed files\n{DescribeChanges(status)}\n\n"
                     + $"## Plan\n{plan}\n\n"
                     + (planVerdict is null ? "" : $"## AI plan review\n{planVerdict}\n\n")
                     + (codeVerdict is null ? "" : $"## AI code review\n{codeVerdict}\n\n")
                     + $"## Diff\n```diff\n{diff}\n```\n";
        Directory.CreateDirectory(context.OutputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(context.OutputDirectory, "review-bundle.md"), bundle, ct);
        PersistPlanArtifact();
        return bundle;
    }

    /// <summary>
    /// git porcelain codes read like line noise in a review card — translate them. The
    /// two-letter code's staged/unstaged nuance doesn't matter pre-commit; the kind does.
    /// </summary>
    internal static string DescribeChanges(string porcelain)
    {
        var lines = porcelain.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Length > 3)
            .Select(l =>
            {
                var kind = l[..2].Trim() switch
                {
                    "??" => "new",
                    "A" => "added",
                    "D" => "deleted",
                    "R" => "renamed",
                    "C" => "copied",
                    _ => "modified"
                };
                return $"- {kind}: `{l[3..].Trim()}`";
            })
            .ToList();
        return lines.Count == 0 ? "_(no changes)_" : string.Join("\n", lines);
    }

    /// <summary>The plan is an artifact from the moment it exists — reviewable at the gate.</summary>
    private void PersistPlanArtifact()
    {
        if (!File.Exists(context.PlanPath))
            return;
        Directory.CreateDirectory(context.OutputDirectory);
        File.Copy(context.PlanPath, Path.Combine(context.OutputDirectory, "plan.md"), overwrite: true);
    }

    private async Task<bool> PushAsync(WorkItem workItem, string branch, CancellationToken ct)
    {
        if (context.PushMode == PushModes.Skip)
            return false;
        if (context.PushMode == PushModes.Prompt && _channel is not null)
        {
            var answer = (await GateAsync([ApprovalQuestion(
                "push-gate", $"Commit and push branch '{branch}'?", null)], ct)).FirstOrDefault();
            if (!IsApproved(answer))
            {
                await ProgressAsync("push", "skipped by the operator", ct);
                return false;
            }
        }

        await git.RunAsync(context.WorkspaceDirectory, "add -A", ct);
        await git.RunAsync(context.WorkspaceDirectory,
            $"commit -m \"Implement {context.WorkItemId}: {Sanitize(workItem.Title)}\"", ct);
        var (exit, output) = await git.RunAsync(
            context.WorkspaceDirectory, $"push -u origin {branch}", ct);
        if (exit != 0)
            throw new InvalidOperationException($"git push failed: {Truncate(output, 400)}");
        await ProgressAsync("push", $"branch '{branch}' pushed", ct);
        return true;
    }

    private async Task SetStoryStateAsync(WorkItem workItem, string branch, bool pushed, CancellationToken ct)
    {
        var states = await workItems.GetStatesAsync(workItem.Id, ct);
        string? target = null;
        if (states.Count > 0 && _channel is not null)
        {
            // The choice list is the SOURCE's vocabulary — never a platform list.
            var answer = (await GateAsync([new OperatorQuestion(
                "state-gate", "Set the story's state to:",
                states.Select(s => new OperatorOption(s, s,
                    string.Equals(s, context.TargetState, StringComparison.OrdinalIgnoreCase)
                        ? "preferred target" : null)).ToList(),
                MultiSelect: false, AllowFreeText: false, Detail: null)], ct)).FirstOrDefault();
            target = answer?.SelectedIds.FirstOrDefault();
        }
        target ??= context.TargetState;
        if (target is { Length: > 0 } && states.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            await workItems.SetStateAsync(workItem.Id, target, ct);
            await ProgressAsync("story", $"state set to '{target}'", ct);
        }

        await workItems.PostCommentAsync(workItem.Id,
            pushed
                ? $"Implementation pushed on branch '{branch}' (Auxilia implementation run)."
                : "Implementation run finished without a push (Auxilia implementation run).", ct);
    }

    /// <summary>
    /// One operator gate. When the operator idles past the threshold, the author console is
    /// compacted ONCE (provider command, e.g. /compact) so resuming later re-reads a small
    /// context instead of the whole transcript.
    /// </summary>
    internal async Task<IReadOnlyList<OperatorAnswer>> GateAsync(
        IReadOnlyList<OperatorQuestion> questions, CancellationToken ct)
    {
        if (_channel is null)
            return [];
        var ask = _channel.AskAsync(questions, ct);
        if (context.GateIdleCompaction > TimeSpan.Zero
            && consoles.AuthorCredentials?.CompactCommand is { Length: > 0 } compact)
        {
            var idle = Task.Delay(context.GateIdleCompaction, ct);
            if (await Task.WhenAny(ask, idle) == idle && !ask.IsCompleted)
            {
                await ProgressAsync("gate",
                    "operator idle — compacting the author console so resuming stays cheap", ct);
                await consoles.SendCommandAsync(
                    consoles.Author, compact, TimeSpan.FromSeconds(20), ct);
            }
        }
        return await ask;
    }

    private Task<string> DriveAsync(
        AgentConsolePool.AgentConsole console, string prompt, CancellationToken ct)
        => consoles.DriveAsync(console, prompt, ct);

    /// <summary>Reviews driven into a console (review-by refinement-agent) — same file contract.</summary>
    private sealed class DrivenReviewRunner(
        AgentConsolePool pool, AgentConsolePool.AgentConsole console) : IReviewRunner
    {
        public async Task<ReviewVerdict> ReviewAsync(
            string instruction, string verdictPath, CancellationToken ct)
        {
            if (File.Exists(verdictPath))
                File.Delete(verdictPath);
            await pool.DriveAsync(console, instruction, ct);
            return ReviewVerdict.FromFile(verdictPath);
        }
    }

    private string WithReviewerBase(string instruction)
        => context.ReviewerBaseInstructions is { Length: > 0 } baseInstructions
            ? baseInstructions + "\n\n" + instruction
            : instruction;

    private static OperatorQuestion ApprovalQuestion(string id, string prompt, string? detail)
        => new(id, prompt,
            [new OperatorOption("approve", "Approve", null), new OperatorOption("revise", "Revise", null)],
            MultiSelect: false, AllowFreeText: true, Detail: detail)
        {
            // Plans and review bundles ARE markdown — clients render them as such.
            DetailFormat = "markdown"
        };

    private static bool IsApproved(OperatorAnswer? answer)
        => answer?.SelectedIds.Contains("approve") == true;

    private static string FeedbackOf(OperatorAnswer? answer)
        => answer?.FreeText is { Length: > 0 } text ? text : "(no details given)";

    private string Rel(string path)
        => Path.GetRelativePath(context.WorkspaceDirectory, path).Replace('\\', '/');

    private static async Task<string?> ReadFileAsync(string path, CancellationToken ct)
        => File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;

    private static IReadOnlyList<string> ReadLines(string path)
        => File.Exists(path)
            ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
            : [];

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "\n…(truncated)";

    private static string Sanitize(string title)
        => title.Replace('"', '\'');

    private Task ChatAsync(AgentChatRole role, string content, string label, CancellationToken ct)
        => views?.PublishAsync(AgentSessionApplication.ChatViewName,
               new AgentChatEntry(role, content, time.GetUtcNow(), Label: label), ct)
           ?? Task.CompletedTask;

    private Task ProgressAsync(string phase, string message, CancellationToken ct)
        => views?.PublishAsync(AgentSessionApplication.ProgressViewName,
               new SessionProgressEntry(phase, message, time.GetUtcNow()), ct)
           ?? Task.CompletedTask;
}
