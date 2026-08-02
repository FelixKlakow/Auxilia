using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.Testing.DummyWorkflows;

/// <summary>
/// Dummy workflow that proves a connector-backed slot credential is resolved just-in-time
/// through the Core and decrypted inside the workflow.
///
/// It declares a single credentialed slot ("secret"), resolves the slot's
/// <see cref="ICredentialProbe"/>, reads its "token" setting, and compares it to the expected
/// value passed in the run context (<c>WORKFLOW_CONTEXT__EXPECTED_SECRET</c>). A missing or
/// mismatched value throws and fails the run — so reaching Success proves the Core-resolved
/// secret was delivered JIT and decrypted (it is never baked into the workflow image).
/// </summary>
public static class CredentialResolutionWorkflow
{
    public const string WorkflowName = "credential-resolution-workflow";

    public static Task RunAsync(string[] args) =>
        WorkflowBuilder
            .Create(WorkflowName)
            .RequiresCredentialProbe("secret", new CredentialProbeCapabilities())
            .WithApplication(ExecuteAsync)
            .Run(args);

    private static Task ExecuteAsync(IServiceProvider provider, CancellationToken ct)
    {
        var expected = System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__EXPECTED_SECRET");
        if (string.IsNullOrEmpty(expected))
            throw new InvalidOperationException(
                "WORKFLOW_CONTEXT__EXPECTED_SECRET is not set — cannot verify credential delivery.");

        var probe = provider.GetRequiredKeyedService<ICredentialProbe>("secret");
        var actual = probe.GetSetting("token");

        if (actual != expected)
            throw new InvalidOperationException(
                $"Core-resolved slot credential did not match the expected secret (token present: {actual is not null}).");

        Console.WriteLine("[CredentialResolutionWorkflow] Core-resolved slot credential verified.");
        return Task.CompletedTask;
    }
}
