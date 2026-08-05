using System.Text.Json;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SystemTestSuite.CoreClientSurface;

/// <summary>Everything one <see cref="ICoreClient.StreamRunAsync"/> enumeration observed.</summary>
internal sealed record ObservedStream(
    IReadOnlyList<int> ConnectAttempts,
    IReadOnlyList<int> ReconnectingAttempts,
    string? FirstEventKind,
    IReadOnlyList<string> States,
    IReadOnlyList<long> ViewSequences,
    IReadOnlyList<string> ViewPayloads);

/// <summary>Shared run-observation helpers for the client-surface fixtures — client calls only.</summary>
internal static class ClientStreamProbe
{
    internal static readonly string[] TerminalStates = ["Success", "Failed", "PreFlightFailed", "Cancelled"];

    /// <summary>
    /// Consumes the run's resilient stream to completion (completion reliably means terminal)
    /// and returns the final state plus everything observed on the way.
    /// </summary>
    internal static async Task<(string State, ObservedStream Frames)> AwaitTerminalAsync(
        ICoreClient client, Guid runId, CancellationToken ct)
    {
        var connects = new List<int>();
        var reconnects = new List<int>();
        string? firstEventKind = null;
        var states = new List<string>();
        var viewSequences = new List<long>();
        var viewPayloads = new List<string>();

        try
        {
        await foreach (var frame in client.StreamRunAsync(runId, ct))
        {
            switch (frame)
            {
                case StreamConnectionFrame<RunStreamEvent> { State: StreamConnectionState.Connected } c:
                    connects.Add(c.Attempt);
                    break;
                case StreamConnectionFrame<RunStreamEvent> { State: StreamConnectionState.Reconnecting } r:
                    reconnects.Add(r.Attempt);
                    break;
                case StreamEventFrame<RunStreamEvent> evt:
                    firstEventKind ??= evt.Event.Kind;
                    if (evt.Event.Kind == RunStreamEvent.StatusKind)
                    {
                        var status = JsonSerializer.Deserialize<WorkflowStatusEvent>(
                            evt.Event.PayloadJson, JsonSerializerOptions.Web)!;
                        states.Add(status.State);
                    }
                    else if (evt.Event.Kind == RunStreamEvent.ViewKind)
                    {
                        var view = JsonSerializer.Deserialize<ViewDataMessage>(
                            evt.Event.PayloadJson, JsonSerializerOptions.Web)!;
                        viewSequences.Add(evt.Event.Sequence);
                        viewPayloads.Add(view.PayloadJson);
                    }
                    break;
            }
        }

        }
        catch (OperationCanceledException)
        {
            // Timing out while awaiting terminal is a diagnosis moment — say what WAS seen.
            throw new AssertionException(
                $"stream for {runId} never reached terminal — observed: "
                + $"connects=[{string.Join(',', connects)}] reconnects=[{string.Join(',', reconnects)}] "
                + $"states=[{string.Join(',', states)}] views={viewSequences.Count}");
        }

        var terminal = states.LastOrDefault(s => TerminalStates.Contains(s))
                       ?? states.LastOrDefault() ?? "(no status observed)";
        return (terminal, new ObservedStream(connects, reconnects, firstEventKind, states, viewSequences, viewPayloads));
    }

    /// <summary>Observes the stream until the run reports <paramref name="state"/> (or terminal).</summary>
    internal static async Task AwaitStateAsync(ICoreClient client, Guid runId, string state, CancellationToken ct)
    {
        await foreach (var frame in client.StreamRunAsync(runId, ct))
        {
            if (frame is not StreamEventFrame<RunStreamEvent> { Event.Kind: RunStreamEvent.StatusKind } evt)
                continue;
            var status = JsonSerializer.Deserialize<WorkflowStatusEvent>(
                evt.Event.PayloadJson, JsonSerializerOptions.Web)!;
            if (status.State == state || TerminalStates.Contains(status.State))
                return;
        }
    }

    /// <summary>
    /// The run's status by its ACCEPTED id (the command id): the record is rekeyed onto the
    /// instance id at the runner's claim, and the read surface must keep resolving the alias.
    /// </summary>
    internal static async Task<RunStatus> GetRunResolvedAsync(
        ICoreClient client, RunAccepted accepted, CancellationToken ct)
        => await client.GetRunAsync(accepted.RunId, ct)
           ?? throw new InvalidOperationException(
               $"run {accepted.RunId} is not resolvable by its accepted id — the command-id alias is broken");
}
