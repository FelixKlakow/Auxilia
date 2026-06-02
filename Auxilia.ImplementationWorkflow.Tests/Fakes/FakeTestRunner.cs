using Auxilia.Workflows.TestRunner;

namespace Auxilia.ImplementationWorkflow.Tests.Fakes;

public sealed class FakeTestRunner : ITestRunner
{
    public Queue<TestRunResult> Results { get; } = new();
    public bool ThrowAsync { get; set; }

    public Task<TestRunResult> RunTestsAsync(TestRunRequest request, CancellationToken cancellationToken = default)
    {
        if (ThrowAsync)
            throw new InvalidOperationException("Scripted async test runner failure.");

        if (Results.Count == 0)
            return Task.FromResult(new TestRunResult(true, 0, 0, 0, 0, "No results scripted."));

        return Task.FromResult(Results.Dequeue());
    }
}
