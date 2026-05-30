using Moq;
using Auxilia.Workflows.Policy;
using Auxilia.Workflows.SourceControl;

namespace Auxilia.Workflows.Tests.Policy;

[TestFixture]
[Category("Unit")]
public class PolicyGuardedSourceControlWriteAccessTests
{
    private const string SlotName = "scm-slot";
    private const string WorkingPathValue = "/repo/workspace";

    [Test]
    public void WorkingPath_ReturnsInnerWorkingPath_WithoutPolicyCheck()
    {
        var innerMock = new Mock<ISourceControlWriteAccess>();
        innerMock.Setup(a => a.WorkingPath).Returns(WorkingPathValue);
        var policyMock = new Mock<IToolPolicy>();

        var sut = new PolicyGuardedSourceControlWriteAccess(innerMock.Object, policyMock.Object, SlotName);

        var result = sut.WorkingPath;

        Assert.That(result, Is.EqualTo(WorkingPathValue));
        policyMock.Verify(p => p.IsAllowed(It.IsAny<SourceControlOperation>()), Times.Never);
    }
}
