using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SystemTestSuite.CoreApiDispatch;

/// <summary>
/// Credentialed end-to-end acceptance: a connector-backed workflow run resolves its slot
/// credential just-in-time through the Core, on real Docker.
///
/// Security model exercised end-to-end:
/// <c>Core.Api resolves the connector + RSA-encrypts it for the workflow instance's public key
/// → the runner (Auxilia.Core.Runner) relays the ciphertext → the workflow decrypts it via its
/// slot handler</c>. The secret is a fresh random value stored only in the Core connector — it
/// is never baked into the workflow image, so reaching <see cref="WorkflowState.Success"/>
/// proves the value travelled through the Core and was decrypted inside the workflow.
/// </summary>
[TestFixture]
[Category("System")]
public class CredentialResolutionSystemTests
{
    private static HttpClient Client => CoreApiDispatchEnvironment.CoreApiClient;
    private static IMessageBusClient Bus => CoreApiDispatchEnvironment.MessageBusClient;

    [Test]
    [CancelAfter(150_000)]
    public async Task ConnectorBackedSlot_ResolvesCredentialThroughCore_ToSuccess(CancellationToken cancellationToken)
    {
        // A random secret that exists ONLY in the Core connector — never in the workflow image.
        var secret = "s3cr3t-" + Guid.NewGuid().ToString("N");

        // 1. Create the connector that holds the secret under the "token" setting.
        var createConnector = new CreateConnector(
            Name: "probe-" + Guid.NewGuid().ToString("N"),
            ProviderType: CoreApiDispatchEnvironment.CredentialProviderType,
            Settings: new Dictionary<string, string> { ["token"] = secret });
        var connectorResp = await Client.PostAsJsonAsync("/api/connectors", createConnector, cancellationToken);
        connectorResp.EnsureSuccessStatusCode();
        var connector = await connectorResp.Content.ReadFromJsonAsync<Connector>(cancellationToken);
        Assert.That(connector, Is.Not.Null);

        await using var success = await SubscribeSuccessAsync(cancellationToken);

        // 2. Create a run configuration binding the "secret" slot to the connector, and passing
        //    the same secret as the expected value the workflow checks after decryption.
        var createConfig = new CreateRunConfiguration(
            Name: "cred-" + Guid.NewGuid().ToString("N"),
            WorkflowType: CoreApiDispatchEnvironment.CredentialWorkflowType,
            Context: new Dictionary<string, string>
            {
                ["WORKFLOW_NAME"] = CoreApiDispatchEnvironment.CredentialWorkflowType,
                ["EXPECTED_SECRET"] = secret
            },
            SlotBindings: new[]
            {
                new SlotBinding("secret", CoreApiDispatchEnvironment.CredentialProviderType, connector!.Id)
            });
        var configResp = await Client.PostAsJsonAsync("/api/configurations", createConfig, cancellationToken);
        configResp.EnsureSuccessStatusCode();
        var config = await configResp.Content.ReadFromJsonAsync<RunConfiguration>(cancellationToken);
        Assert.That(config, Is.Not.Null);

        // 3. Run it.
        var runResp = await Client.PostAsync($"/api/configurations/{config!.Id}/run", null, cancellationToken);
        runResp.EnsureSuccessStatusCode();

        // 4. The workflow only reaches Success if the Core-resolved token matched the secret.
        var state = await success.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
        Assert.That(state.State, Is.EqualTo(WorkflowState.Success),
            $"Credentialed run ended {state.State}. Error: {state.ErrorMessage}");
    }

    /// <summary>Subscribes fresh to the workflow.state exchange; completes on the next terminal message.</summary>
    private static async Task<SuccessWaiter> SubscribeSuccessAsync(CancellationToken ct)
    {
        const string stateExchange = "workflow.state";
        await Bus.DeclareExchangeAsync(stateExchange, ct);
        var tcs = new TaskCompletionSource<WorkflowStateMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = await Bus.SubscribeToExchangeAsync<WorkflowStateMessage>(
            stateExchange, (msg, _) => { tcs.TrySetResult(msg); return Task.CompletedTask; }, ct);
        return new SuccessWaiter(tcs.Task, subscription);
    }

    private sealed record SuccessWaiter(
        Task<WorkflowStateMessage> Task, IAsyncDisposable Subscription) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Subscription.DisposeAsync();
    }
}
