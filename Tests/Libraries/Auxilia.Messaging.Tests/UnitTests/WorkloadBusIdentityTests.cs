namespace Auxilia.Messaging.Tests.UnitTests;

/// <summary>
/// Publisher attribution: a workload identity may speak only for its own instance; the platform
/// for any; an unattributed publish for none.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class WorkloadBusIdentityTests
{
    private static readonly Guid Own = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    [Test]
    public void UserNameFor_IsPrefixedAndStable()
    {
        Assert.That(WorkloadBusIdentity.UserNameFor(Own), Is.EqualTo($"auxilia-wf-{Own:N}"));
        Assert.That(WorkloadBusIdentity.IsWorkloadUser(WorkloadBusIdentity.UserNameFor(Own)), Is.True);
        Assert.That(WorkloadBusIdentity.IsWorkloadUser("auxilia-runner"), Is.False);
    }

    [Test]
    public void MayPublishFor_WorkloadIdentity_OnlyItsOwnInstance()
    {
        var user = WorkloadBusIdentity.UserNameFor(Own);

        Assert.That(WorkloadBusIdentity.MayPublishFor(user, Own), Is.True);
        Assert.That(WorkloadBusIdentity.MayPublishFor(user, Other), Is.False,
            "a container must never speak for another run, whatever its message body claims");
    }

    [Test]
    public void MayPublishFor_PlatformIdentity_AnyInstance()
    {
        Assert.That(WorkloadBusIdentity.MayPublishFor("auxilia-runner", Own), Is.True);
        Assert.That(WorkloadBusIdentity.MayPublishFor("guest", Other), Is.True);
    }

    [TestCase(null)]
    [TestCase("")]
    public void MayPublishFor_Unattributed_Nothing(string? publisher)
        => Assert.That(WorkloadBusIdentity.MayPublishFor(publisher, Own), Is.False,
            "an omitted user_id must not be a way around attribution");
}
