using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// A caller's W3C trace continues through the Core: the request joins it, and the dispatch the
/// Core sends the runner travels under it (the bus client carries the current trace in each
/// message's headers, and the runner continues from there).
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class TracePropagationTests : CoreApiComponentTestBase
{
    private const string WorkflowType = "simple-git-commit-workflow";

    [Test]
    public async Task ACallersTrace_ContinuesThroughTheCore_IntoTheRunItDispatches()
    {
        var client = CreateClient();
        await RegisterActiveTypeAsync(client, WorkflowType);
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/runs")
        {
            Content = JsonContent.Create(new RunRequest(WorkflowType)),
        };
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01");

        using var response = await client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        var dispatch = MessageBus.PublishedTraces.Single(p => p.Message is RunWorkflowCommand);
        Assert.That(dispatch.TraceId, Is.EqualTo(traceId), "the run's dispatch carries the caller's trace to the runner");
    }

    [Test]
    public async Task ACallWithoutATrace_StartsOne_ThatTheDispatchCarries()
    {
        var client = CreateClient();
        await RegisterActiveTypeAsync(client, WorkflowType);

        using var response = await client.PostAsJsonAsync("/api/runs", new RunRequest(WorkflowType));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var dispatch = MessageBus.PublishedTraces.Single(p => p.Message is RunWorkflowCommand);
        Assert.That(dispatch.TraceId, Is.Not.Null.And.Not.EqualTo(default(ActivityTraceId).ToHexString()),
            "a trace of its own, which the runner continues");
    }
}
