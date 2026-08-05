using System.Text;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Artifacts;
using Auxilia.UniversalDataAccess.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// The "verdict-workflow" approval handler: dispatches the configured type over a pending
/// registration and applies the run's verdict artifact. Everything inconclusive defers —
/// a denial requires an explicit verdict, never an infrastructure failure.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class VerdictWorkflowApprovalHandlerTests
{
    private sealed class FakeDispatcher : IVerdictRunDispatcher
    {
        public RunRequest? LastRequest;
        public RunAccepted Accepted = new(Guid.NewGuid(), Guid.NewGuid());
        public bool Throw;

        public Task<RunAccepted> DispatchAsync(RunRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Throw
                ? throw new InvalidOperationException("no runner")
                : Task.FromResult(Accepted);
        }
    }

    private sealed class FakePayloads : IArtifactPayloadReader
    {
        public readonly Dictionary<Guid, string> Payloads = new();

        public Task<Stream?> OpenReadAsync(Guid artifactId, CancellationToken ct = default)
            => Task.FromResult<Stream?>(Payloads.TryGetValue(artifactId, out var json)
                ? new MemoryStream(Encoding.UTF8.GetBytes(json))
                : null);
    }

    private static readonly WorkflowTypeRegistrationDto Registration = new(
        "under-review", "docker://img:1", WorkflowTypeStatus.Pending, null,
        "publisher-key", false, Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private FakeDispatcher _dispatcher = null!;
    private InMemoryDataAccess<CoreRunRecord> _runs = null!;
    private InMemoryDataAccess<CoreArtifactRecord> _artifacts = null!;
    private FakePayloads _payloads = null!;
    private CoreApiSettings _settings = null!;

    [SetUp]
    public void SetUp()
    {
        _dispatcher = new FakeDispatcher();
        _runs = new InMemoryDataAccess<CoreRunRecord>();
        _artifacts = new InMemoryDataAccess<CoreArtifactRecord>();
        _payloads = new FakePayloads();
        _settings = new CoreApiSettings
        {
            ApprovalVerdictWorkflow = new ApprovalVerdictWorkflowSettings
            {
                WorkflowType = "safety-check",
                TimeoutSeconds = 5,
                PollIntervalSeconds = 0,
            }
        };
    }

    [TearDown]
    public void TearDown()
    {
        _runs.Dispose();
        _artifacts.Dispose();
    }

    private VerdictWorkflowApprovalHandler NewHandler() => new(
        _dispatcher, _runs, _artifacts, _payloads, Options.Create(_settings),
        TimeProvider.System, NullLogger<VerdictWorkflowApprovalHandler>.Instance);

    private async Task CompleteRunAsync(string state = "Success")
        => await _runs.SaveAsync(new CoreRunRecord
        {
            Id = _dispatcher.Accepted.RunId,
            WorkflowType = "safety-check",
            State = state,
            CommandId = _dispatcher.Accepted.CommandId,
        }, CancellationToken.None);

    private async Task<Guid> AddVerdictArtifactAsync(string json)
    {
        var artifactId = Guid.NewGuid();
        await _artifacts.SaveAsync(new CoreArtifactRecord
        {
            Id = artifactId,
            ArtifactType = "approval-verdict",
            WorkflowType = "safety-check",
            WorkItemId = "",
            RunInstanceId = _dispatcher.Accepted.RunId,
            Version = 1,
            ContentHash = "",
            CreatedUtc = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        _payloads.Payloads[artifactId] = json;
        return artifactId;
    }

    [Test]
    public async Task Unconfigured_Defers_WithoutDispatching()
    {
        _settings.ApprovalVerdictWorkflow.WorkflowType = "";

        var result = await NewHandler().EvaluateAsync(Registration, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Decision, Is.EqualTo(ApprovalHandlerResult.Defer));
            Assert.That(_dispatcher.LastRequest, Is.Null);
        });
    }

    [Test]
    public async Task ApproveVerdict_Approves_AndPassesTheRegistrationAsContext()
    {
        await CompleteRunAsync();
        await AddVerdictArtifactAsync("""{"decision":"approve","reason":"clean package"}""");

        var result = await NewHandler().EvaluateAsync(Registration, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Decision, Is.EqualTo(ApprovalHandlerResult.Approve));
            Assert.That(result.Reason, Is.EqualTo("clean package"));
            Assert.That(_dispatcher.LastRequest!.WorkflowType, Is.EqualTo("safety-check"));
            Assert.That(_dispatcher.LastRequest.Context!["approval-workflow-type"], Is.EqualTo("under-review"));
            Assert.That(_dispatcher.LastRequest.Context!["approval-package-uri"], Is.EqualTo("docker://img:1"));
        });
    }

    [Test]
    public async Task DenyVerdict_Denies_WithTheWorkflowsReason()
    {
        await CompleteRunAsync();
        await AddVerdictArtifactAsync("""{"decision":"DENY","reason":"exfiltrates credentials"}""");

        var result = await NewHandler().EvaluateAsync(Registration, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Decision, Is.EqualTo(ApprovalHandlerResult.Deny), "decision matching is case-insensitive");
            Assert.That(result.Reason, Is.EqualTo("exfiltrates credentials"));
        });
    }

    [Test]
    public async Task FailedRun_Defers_NeverDenies()
    {
        await CompleteRunAsync(state: "Failed");

        var result = await NewHandler().EvaluateAsync(Registration, CancellationToken.None);

        Assert.That(result.Decision, Is.EqualTo(ApprovalHandlerResult.Defer),
            "an infrastructure failure is not a safety verdict");
    }

    [Test]
    public async Task Timeout_Defers()
    {
        _settings.ApprovalVerdictWorkflow.TimeoutSeconds = 0;

        var result = await NewHandler().EvaluateAsync(Registration, CancellationToken.None);

        Assert.That(result.Decision, Is.EqualTo(ApprovalHandlerResult.Defer));
    }

    [Test]
    public async Task SuccessWithoutVerdictArtifact_Defers()
    {
        await CompleteRunAsync();

        var result = await NewHandler().EvaluateAsync(Registration, CancellationToken.None);

        Assert.That(result.Decision, Is.EqualTo(ApprovalHandlerResult.Defer));
    }

    [Test]
    public async Task UnparseableVerdict_Defers()
    {
        await CompleteRunAsync();
        await AddVerdictArtifactAsync("this is not json");

        var result = await NewHandler().EvaluateAsync(Registration, CancellationToken.None);

        Assert.That(result.Decision, Is.EqualTo(ApprovalHandlerResult.Defer));
    }

    [Test]
    public async Task DispatchFailure_Defers()
    {
        _dispatcher.Throw = true;

        var result = await NewHandler().EvaluateAsync(Registration, CancellationToken.None);

        Assert.That(result.Decision, Is.EqualTo(ApprovalHandlerResult.Defer));
    }

    [Test]
    public async Task RunFoundByCommandId_WhenTheRecordWasRekeyedOnClaim()
    {
        // The claim rekeys the record to the instance id — only CommandId still matches.
        await _runs.SaveAsync(new CoreRunRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "safety-check",
            State = "Success",
            CommandId = _dispatcher.Accepted.CommandId,
        }, CancellationToken.None);

        var result = await NewHandler().EvaluateAsync(Registration, CancellationToken.None);

        Assert.That(result.Decision, Is.EqualTo(ApprovalHandlerResult.Defer),
            "the rekeyed run is found (no timeout) but has no verdict artifact — defer");
    }
}
