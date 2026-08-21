using Auxilia.Core.Contracts;

namespace Auxilia.Core.Client.Tests.UnitTests;

/// <summary>
/// Typed decoding of run-stream frames: clients read <c>AsStatus()</c>/<c>AsView()</c> instead
/// of hand-parsing PayloadJson — the shapes mirror the Core's web-cased wire payloads.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RunStreamEventDecodingTests
{
    [Test]
    public void AsStatus_DecodesTheWebCasedStatusPayload()
    {
        var instanceId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var frame = new RunStreamEvent(
            RunStreamEvent.StatusKind, commandId, 0,
            $$"""
            {"workflowInstanceId":"{{instanceId}}","workflowType":"demo","state":"Failed",
             "errorMessage":"boom","timestampUtc":"2026-08-16T10:00:00+00:00",
             "commandId":"{{commandId}}"}
            """,
            DateTimeOffset.UtcNow);

        var status = frame.AsStatus();

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.Not.Null);
            Assert.That(status!.WorkflowInstanceId, Is.EqualTo(instanceId));
            Assert.That(status.State, Is.EqualTo(RunStates.Failed));
            Assert.That(status.ErrorMessage, Is.EqualTo("boom"));
            Assert.That(status.IsTerminal, Is.True);
            Assert.That(status.CommandId, Is.EqualTo(commandId));
        });
    }

    [Test]
    public void AsView_DecodesViewFrames_AndKindsNeverCrossDecode()
    {
        var instanceId = Guid.NewGuid();
        var view = new RunStreamEvent(
            RunStreamEvent.ViewKind, instanceId, 7,
            $$"""{"workflowInstanceId":"{{instanceId}}","viewName":"progress","sequence":7,"payloadJson":"{}"}""",
            DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(view.AsView()!.ViewName, Is.EqualTo("progress"));
            Assert.That(view.AsView()!.Sequence, Is.EqualTo(7));
            Assert.That(view.AsStatus(), Is.Null, "a view frame never decodes as a status");
        });
    }

    [Test]
    public void AsStatus_UnparseablePayload_IsNullNotAnException()
    {
        var frame = new RunStreamEvent(
            RunStreamEvent.StatusKind, Guid.NewGuid(), 0, "not json", DateTimeOffset.UtcNow);

        Assert.That(frame.AsStatus(), Is.Null);
    }
}
