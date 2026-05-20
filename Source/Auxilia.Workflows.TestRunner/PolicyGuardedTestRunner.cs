using Auxilia.Workflows.Policy;

namespace Auxilia.Workflows.TestRunner;

/// <summary>
/// Policy-guarded decorator for <see cref="ITestRunner"/>.
/// Every <c>*Async</c> method checks <see cref="IToolPolicy.IsAllowed"/> before delegating.
/// </summary>
public sealed class PolicyGuardedTestRunner : ITestRunner
{
    private readonly ITestRunner _inner;
    private readonly IToolPolicy _policy;
    private readonly string _slotName;

    public PolicyGuardedTestRunner(ITestRunner inner, IToolPolicy policy, string slotName)
    {
        _inner = inner;
        _policy = policy;
        _slotName = slotName;
    }

    public Task<TestRunResult> RunTestsAsync(TestRunRequest request, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed("test_runner.run_tests"))
            throw new ToolPolicyDeniedException("test_runner.run_tests", _slotName);
        return _inner.RunTestsAsync(request, cancellationToken);
    }
}
