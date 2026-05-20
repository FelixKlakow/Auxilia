using Moq;
using Auxilia.Workflows.Policy;
using Auxilia.Workflows.TestRunner;

namespace Auxilia.Workflows.Tests.Policy;

[TestFixture]
[Category("Unit")]
public class PolicyGuardedTestRunnerTests
{
    private const string SlotName = "test-slot";
    private const string PolicyKey = "test_runner.run_tests";

    private static readonly TestRunRequest SampleRequest = new("dotnet test");

    [Test]
    public async Task RunTestsAsync_WhenAllowed_DelegatesToInner()
    {
        var expected = new TestRunResult(true, 0, 5, 0, 0, "All tests passed.");
        var innerMock = new Mock<ITestRunner>();
        innerMock.Setup(r => r.RunTestsAsync(SampleRequest, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(expected);
        var policy = new Mock<IToolPolicy>();
        policy.Setup(p => p.IsAllowed(PolicyKey)).Returns(true);

        var sut = new PolicyGuardedTestRunner(innerMock.Object, policy.Object, SlotName);
        var result = await sut.RunTestsAsync(SampleRequest);

        Assert.That(result, Is.SameAs(expected));
        innerMock.Verify(r => r.RunTestsAsync(SampleRequest, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void RunTestsAsync_WhenDenied_ThrowsToolPolicyDeniedException()
    {
        var innerMock = new Mock<ITestRunner>();
        var policy = new Mock<IToolPolicy>();
        policy.Setup(p => p.IsAllowed(PolicyKey)).Returns(false);

        var sut = new PolicyGuardedTestRunner(innerMock.Object, policy.Object, SlotName);

        Assert.ThrowsAsync<ToolPolicyDeniedException>(() => sut.RunTestsAsync(SampleRequest));
    }

    [Test]
    public void RunTestsAsync_WhenDenied_InnerIsNeverCalled()
    {
        var innerMock = new Mock<ITestRunner>();
        var policy = new Mock<IToolPolicy>();
        policy.Setup(p => p.IsAllowed(PolicyKey)).Returns(false);

        var sut = new PolicyGuardedTestRunner(innerMock.Object, policy.Object, SlotName);

        Assert.ThrowsAsync<ToolPolicyDeniedException>(() => sut.RunTestsAsync(SampleRequest));

        innerMock.Verify(r => r.RunTestsAsync(It.IsAny<TestRunRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void RunTestsAsync_WhenDenied_ExceptionContainsCorrectFields()
    {
        var innerMock = new Mock<ITestRunner>();
        var policy = new Mock<IToolPolicy>();
        policy.Setup(p => p.IsAllowed(PolicyKey)).Returns(false);

        var sut = new PolicyGuardedTestRunner(innerMock.Object, policy.Object, SlotName);

        var ex = Assert.ThrowsAsync<ToolPolicyDeniedException>(() => sut.RunTestsAsync(SampleRequest));

        Assert.That(ex!.CapabilityOperation, Is.EqualTo(PolicyKey));
        Assert.That(ex.SlotName, Is.EqualTo(SlotName));
    }
}
