using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.Steering;
using Auxilia.Workflows.TaskSource;
using Auxilia.Workflows.Views;

namespace Auxilia.Implementation.Workflow;

/// <summary>
/// The assisted-delivery pipeline (docs/implementation-workflow-design.md): one DRIVEN author
/// console spans completeness check, plan, and implementation (context never lost); AI review
/// passes and operator gates sit between the phases; the WORKFLOW commits and pushes; the
/// story's state is set from its source's own vocabulary. Every gate obeys the idle-compaction
/// rule so a slow operator never forces a full context re-read.
/// </summary>
public sealed class ImplementationPipeline(
    IWorkItemAccess workItems,
    IViewPublisher? views,
    IWorkflowInputs? inputs,
    ImplementationContext context,
    DrivenConsoleSession author,
    CodingAgentCredentials? authorCredentials,
    IReviewRunner? reviewer,
    IGitRunner git,
    TimeProvider time)
{
    private OperatorChannel? _channel;

    // Author base without a provider system-prompt seam rides the FIRST drive instead.
    private string? _pendingAuthorBase;

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
            await PublishFlowAsync(activeStep: "workspace", ct);
            var branch = context.BranchNameFor(workItem.Title);
            await git.RunAsync(context.WorkspaceDirectory, $"checkout -b {branch}", ct);
            await ProgressAsync("workspace", $"branch '{branch}' ready", ct);

            await author.StartAsync(BuildAuthorSession(), ct);
            await ProgressAsync("session", "author console up — visible in the run terminal", ct);

            await MaterializeStoryAsync(workItem, ct);
            if (context.CompletenessCheck)
            {
                await PublishFlowAsync("completeness", ct);
                await CompletenessLoopAsync(workItem, ct);
            }

            await PublishFlowAsync("plan", ct);
            var plan = await PlanLoopAsync(workItem, ct);
            await PublishFlowAsync("implement", ct);
            await ImplementationLoopAsync(workItem, plan, ct);

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
            await author.DisposeAsync();
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
        "completeness" => !context.CompletenessCheck,
        _ => false
    };

    private TerminalSessionInfo BuildAuthorSession()
    {
        var cli = authorCredentials?.CliPath is { Length: > 0 } path ? path : "claude";
        var unattended = authorCredentials?.UnattendedCliArguments is { Length: > 0 } arguments
            ? " " + arguments
            : "";
        var environment = new Dictionary<string, string>(
            authorCredentials?.ToEnvironment() ?? new Dictionary<string, string>());
        var command = cli + unattended;
        if (context.AuthorBaseInstructions is { Length: > 0 } baseInstructions)
        {
            if (authorCredentials?.SystemPromptCliArgument is { Length: > 0 } systemPromptArgument)
            {
                environment[AgentConsoleApplication.BasePromptVariable] = baseInstructions;
                command += $" {systemPromptArgument} \"${AgentConsoleApplication.BasePromptVariable}\"";
            }
            else
            {
                _pendingAuthorBase = baseInstructions;
            }
        }

        return new TerminalSessionInfo(
            context.WorkspaceDirectory, command, AgentConsoleApplication.TerminalPort)
        {
            Environment = environment,
        };
    }

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

    private async Task CompletenessLoopAsync(WorkItem workItem, CancellationToken ct)
    {
        for (var round = 0; round < 2; round++)
        {
            await DriveAuthorAsync(
                $"Read the user story in {Rel(context.ExchangeDirectory)}/story.md (attachments beside it). "
                + $"Assess whether it is COMPLETE enough to implement. Write any open questions to "
                + $"{Rel(context.QuestionsPath)}, one per line; if nothing is missing, write exactly NONE.",
                ct);
            var questions = ReadLines(context.QuestionsPath)
                .Where(l => !string.Equals(l, "NONE", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (questions.Count == 0 || _channel is null)
                return;

            await ProgressAsync("completeness", $"{questions.Count} open question(s) for the operator", ct);
            var answers = await GateAsync(questions
                .Select((q, i) => new OperatorQuestion(
                    $"q{i}", q, [], MultiSelect: false, AllowFreeText: true, Detail: null))
                .ToList(), ct);
            var answered = string.Join("\n", answers.Select(a =>
                $"- {questions.ElementAtOrDefault(int.TryParse(a.QuestionId.TrimStart('q'), out var i) ? i : 0)}: "
                + (a.FreeText ?? string.Join(", ", a.SelectedIds))));
            await DriveAuthorAsync(
                $"The operator answered the open questions:\n{answered}\n"
                + $"Update {Rel(context.ExchangeDirectory)}/story.md accordingly, then re-assess completeness "
                + $"the same way (questions to {Rel(context.QuestionsPath)} or NONE).", ct);
            var remaining = ReadLines(context.QuestionsPath)
                .Where(l => !string.Equals(l, "NONE", StringComparison.OrdinalIgnoreCase)).ToList();
            if (remaining.Count == 0)
                return;
        }
    }

    private async Task<string> PlanLoopAsync(WorkItem workItem, CancellationToken ct)
    {
        var prompt = $"Create a detailed implementation plan (markdown) for the story in "
                     + $"{Rel(context.ExchangeDirectory)}/story.md and write it to {Rel(context.PlanPath)}. "
                     + "Cover approach, files to touch, tests, and risks. Do NOT implement yet.";
        while (true)
        {
            await DriveAuthorAsync(prompt, ct);
            var plan = await ReadFileAsync(context.PlanPath, ct)
                ?? throw new InvalidOperationException("The author produced no plan file.");
            await ChatAsync(AgentChatRole.Assistant, plan, "Implementation plan", ct);

            if (context.AiPlanReview && reviewer is not null)
                plan = await AiReviewLoopAsync(plan, isPlan: true, ct);

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

    private async Task ImplementationLoopAsync(WorkItem workItem, string plan, CancellationToken ct)
    {
        var prompt = $"Implement the approved plan in {Rel(context.PlanPath)}. Work the plan point by "
                     + "point, run the tests you touch, and commit NOTHING — the workflow handles git.";
        while (true)
        {
            await DriveAuthorAsync(prompt, ct);

            if (context.AiCodeReview && reviewer is not null)
            {
                var verdict = await AiCodeReviewAsync(ct);
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
    private async Task<string> AiReviewLoopAsync(string plan, bool isPlan, CancellationToken ct)
    {
        for (var round = 0; round < context.MaxAiReviewRounds; round++)
        {
            var verdict = await reviewer!.ReviewAsync(
                WithReviewerBase($"Review the implementation plan at {Rel(context.PlanPath)} against the story at "
                + $"{Rel(context.ExchangeDirectory)}/story.md. Write your verdict to "
                + $"{Rel(context.PlanReviewPath)}: first line APPROVE or REVISE, then your notes."),
                context.PlanReviewPath, ct);
            await ChatAsync(AgentChatRole.System, verdict.Notes,
                verdict.Approved ? "AI plan review — approved" : "AI plan review — revise", ct);
            if (verdict.Approved)
                return plan;
            await DriveAuthorAsync(
                $"A reviewer asks for plan changes:\n{verdict.Notes}\nRework {Rel(context.PlanPath)}.", ct);
            plan = await ReadFileAsync(context.PlanPath, ct) ?? plan;
            await ChatAsync(AgentChatRole.Assistant, plan, "Implementation plan (revised)", ct);
        }
        return plan;
    }

    private async Task<ReviewVerdict> AiCodeReviewAsync(CancellationToken ct)
    {
        ReviewVerdict verdict = new(false, "no review ran");
        for (var round = 0; round < context.MaxAiReviewRounds; round++)
        {
            verdict = await reviewer!.ReviewAsync(
                WithReviewerBase($"Review the UNCOMMITTED changes in this repository (git diff / git status) against the "
                + $"plan at {Rel(context.PlanPath)}. Write your verdict to {Rel(context.CodeReviewPath)}: "
                + "first line APPROVE or REVISE, then concrete findings."),
                context.CodeReviewPath, ct);
            await ChatAsync(AgentChatRole.System, verdict.Notes,
                verdict.Approved ? "AI code review — approved" : "AI code review — revise", ct);
            if (verdict.Approved)
                return verdict;
            await DriveAuthorAsync(
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
                     + $"## Changed files\n```\n{status}\n```\n\n"
                     + $"## Plan\n{plan}\n\n"
                     + (planVerdict is null ? "" : $"## AI plan review\n{planVerdict}\n\n")
                     + (codeVerdict is null ? "" : $"## AI code review\n{codeVerdict}\n\n")
                     + $"## Diff\n```diff\n{diff}\n```\n";
        Directory.CreateDirectory(context.OutputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(context.OutputDirectory, "review-bundle.md"), bundle, ct);
        if (File.Exists(context.PlanPath))
            File.Copy(context.PlanPath, Path.Combine(context.OutputDirectory, "plan.md"), overwrite: true);
        return bundle;
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
            && authorCredentials?.CompactCommand is { Length: > 0 } compact)
        {
            var idle = Task.Delay(context.GateIdleCompaction, ct);
            if (await Task.WhenAny(ask, idle) == idle && !ask.IsCompleted)
            {
                await ProgressAsync("gate",
                    "operator idle — compacting the author console so resuming stays cheap", ct);
                await author.SendCommandAsync(compact, TimeSpan.FromSeconds(20), ct);
            }
        }
        return await ask;
    }

    /// <summary>Every author drive; a base without a provider seam rides the first one.</summary>
    private Task<string> DriveAuthorAsync(string prompt, CancellationToken ct)
    {
        if (_pendingAuthorBase is { } baseInstructions)
        {
            _pendingAuthorBase = null;
            prompt = baseInstructions + "\n\n" + prompt;
        }
        return author.DriveAsync(prompt, ct);
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
