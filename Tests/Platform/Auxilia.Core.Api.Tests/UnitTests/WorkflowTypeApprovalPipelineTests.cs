using System.Net;
using Auxilia.Core.Api;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// The pluggable approval pipeline: configured handlers run in order over a Pending registration;
/// the first Approve/Deny verdict is applied through the registry as the signing authority, an
/// all-defer outcome leaves the registration pending for a human, and unknown handler names are
/// skipped (name-keyed catalog, no compiled enum).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class WorkflowTypeApprovalPipelineTests
{
    private sealed class StubHandler(string name, ApprovalHandlerResult result) : IWorkflowTypeApprovalHandler
    {
        public int Calls { get; private set; }
        public string Name => name;

        public Task<ApprovalHandlerResult> EvaluateAsync(WorkflowTypeRegistrationDto registration, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static WorkflowTypeRegistryService NewRegistry(CoreApiSettings settings) => new(
        new InMemoryDataAccess<CoreWorkflowTypeRecord>(),
        new StubHttpClientFactory(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
        Options.Create(settings),
        new AuditLog(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System),
        TimeProvider.System,
        NullLogger<WorkflowTypeRegistryService>.Instance);

    private static WorkflowTypeApprovalPipeline NewPipeline(
        WorkflowTypeRegistryService registry, CoreApiSettings settings, params IWorkflowTypeApprovalHandler[] handlers)
        => new(registry, handlers, Options.Create(settings), NullLogger<WorkflowTypeApprovalPipeline>.Instance);

    private static async Task<WorkflowTypeRegistryService> RegistryWithPendingAsync(CoreApiSettings settings, string type)
    {
        var registry = NewRegistry(settings);
        var outcome = await registry.RegisterAsync(
            new RegisterWorkflowTypeRequest(type, "docker://img:1"), null, CancellationToken.None);
        Assert.That(outcome.Registration!.Status, Is.EqualTo(WorkflowTypeStatus.Pending));
        return registry;
    }

    [Test]
    public async Task FirstApproveVerdict_Activates_AndStopsThePipeline()
    {
        var settings = new CoreApiSettings { ApprovalHandlers = { "checker", "late" } };
        var registry = await RegistryWithPendingAsync(settings, "wf");
        var late = new StubHandler("late", ApprovalHandlerResult.Deferred);
        var pipeline = NewPipeline(registry, settings,
            new StubHandler("checker", new ApprovalHandlerResult(ApprovalHandlerResult.Approve, "safe")), late);

        await pipeline.ProcessPendingAsync("wf", CancellationToken.None);

        var registration = await registry.GetRegistrationAsync("wf", CancellationToken.None);
        Assert.That(registration!.Status, Is.EqualTo(WorkflowTypeStatus.Active));
        Assert.That(late.Calls, Is.Zero, "a decisive verdict short-circuits the pipeline");
    }

    [Test]
    public async Task DenyVerdict_RecordsTheHandlersReason()
    {
        var settings = new CoreApiSettings { ApprovalHandlers = { "ai-safety" } };
        var registry = await RegistryWithPendingAsync(settings, "wf");
        var pipeline = NewPipeline(registry, settings,
            new StubHandler("ai-safety", new ApprovalHandlerResult(ApprovalHandlerResult.Deny, "exfiltrates data")));

        await pipeline.ProcessPendingAsync("wf", CancellationToken.None);

        var registration = await registry.GetRegistrationAsync("wf", CancellationToken.None);
        Assert.That(registration!.Status, Is.EqualTo(WorkflowTypeStatus.Denied));
        Assert.That(registration.StatusReason, Is.EqualTo("exfiltrates data"));
    }

    [Test]
    public async Task AllDefer_LeavesTheRegistrationPending_ForAHuman()
    {
        var settings = new CoreApiSettings { ApprovalHandlers = { "email", "unknown-handler" } };
        var registry = await RegistryWithPendingAsync(settings, "wf");
        var email = new StubHandler("email", ApprovalHandlerResult.Deferred);
        var pipeline = NewPipeline(registry, settings, email);

        await pipeline.ProcessPendingAsync("wf", CancellationToken.None);

        var registration = await registry.GetRegistrationAsync("wf", CancellationToken.None);
        Assert.That(registration!.Status, Is.EqualTo(WorkflowTypeStatus.Pending));
        Assert.That(email.Calls, Is.EqualTo(1));
    }

    /// <summary>A handler whose evaluation has a side effect on the registry (a race stand-in).</summary>
    private sealed class RacingHandler(string name, Func<Task> duringEvaluation, ApprovalHandlerResult result)
        : IWorkflowTypeApprovalHandler
    {
        public string Name => name;

        public async Task<ApprovalHandlerResult> EvaluateAsync(WorkflowTypeRegistrationDto registration, CancellationToken ct)
        {
            await duringEvaluation();
            return result;
        }
    }

    [Test]
    public async Task ReRegistrationDuringTheReview_KeepsTheReplacementPending()
    {
        var settings = new CoreApiSettings { ApprovalHandlers = { "checker" } };
        var registry = await RegistryWithPendingAsync(settings, "wf");
        var checker = new RacingHandler("checker",
            async () => await registry.RegisterAsync(
                new RegisterWorkflowTypeRequest("wf", "docker://img:2"), null, CancellationToken.None),
            new ApprovalHandlerResult(ApprovalHandlerResult.Approve, "the OLD package looked safe"));
        var pipeline = NewPipeline(registry, settings, checker);

        await pipeline.ProcessPendingAsync("wf", CancellationToken.None);

        var registration = await registry.GetRegistrationAsync("wf", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(registration!.Status, Is.EqualTo(WorkflowTypeStatus.Pending),
                "the verdict was reached over a package that is no longer the registered one");
            Assert.That(registration.PackageUri, Is.EqualTo("docker://img:2"));
        });
    }

    [Test]
    public async Task HumanDenialDuringTheReview_IsNotOverriddenByALateApprove()
    {
        var settings = new CoreApiSettings { ApprovalHandlers = { "checker" } };
        var registry = await RegistryWithPendingAsync(settings, "wf");
        var checker = new RacingHandler("checker",
            async () => await registry.DenyAsync("wf", "operator said no", "human", CancellationToken.None),
            new ApprovalHandlerResult(ApprovalHandlerResult.Approve, "safe"));
        var pipeline = NewPipeline(registry, settings, checker);

        await pipeline.ProcessPendingAsync("wf", CancellationToken.None);

        var registration = await registry.GetRegistrationAsync("wf", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(registration!.Status, Is.EqualTo(WorkflowTypeStatus.Denied));
            Assert.That(registration.StatusReason, Is.EqualTo("operator said no"));
        });
    }

    [Test]
    public async Task NonPendingRegistrations_AreNeverProcessed()
    {
        var settings = new CoreApiSettings { ApprovalHandlers = { "checker" } };
        var registry = NewRegistry(settings);
        await registry.EnsureSeededAsync(
            new StaticWorkflowType { WorkflowType = "wf", PackageUri = "docker://img" }, CancellationToken.None);
        var checker = new StubHandler("checker", new ApprovalHandlerResult(ApprovalHandlerResult.Deny, "nope"));
        var pipeline = NewPipeline(registry, settings, checker);

        await pipeline.ProcessPendingAsync("wf", CancellationToken.None);

        Assert.That(checker.Calls, Is.Zero);
        var registration = await registry.GetRegistrationAsync("wf", CancellationToken.None);
        Assert.That(registration!.Status, Is.EqualTo(WorkflowTypeStatus.Active));
    }
}
