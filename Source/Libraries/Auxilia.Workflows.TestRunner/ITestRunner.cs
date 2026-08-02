namespace Auxilia.Workflows.TestRunner;

public interface ITestRunner
{
    Task<TestRunResult> RunTestsAsync(TestRunRequest request, CancellationToken cancellationToken = default);
}
