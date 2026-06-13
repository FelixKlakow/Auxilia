using Auxilia.BackendService.Dashboard;
using Auxilia.PlatformData.Entities;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class LiveNowTests
{
    private static WorkflowInstanceRecord Run(string state, DateTimeOffset createdUtc)
        => new()
        {
            Id = Guid.NewGuid(),
            WorkflowType = "code-review",
            State = state,
            CreatedUtc = createdUtc
        };

    [TestCase("Queued", ExpectedResult = true)]
    [TestCase("Running", ExpectedResult = true)]
    [TestCase("Draining", ExpectedResult = true)]
    [TestCase("Received", ExpectedResult = false)]
    [TestCase("Success", ExpectedResult = false)]
    [TestCase("Failed", ExpectedResult = false)]
    [TestCase("Cancelled", ExpectedResult = false)]
    [TestCase("PreFlightFailed", ExpectedResult = false)]
    public bool IsActive_OnlyForActiveLifecycleStates(string state)
        => LiveNow.IsActive(state);

    [Test]
    public void ActiveOf_FiltersToActiveStates_NewestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var older = Run("Running", now.AddMinutes(-10));
        var newer = Run("Queued", now.AddMinutes(-1));
        var runs = new[]
        {
            Run("Success", now.AddMinutes(-30)),
            older,
            Run("Failed", now.AddMinutes(-5)),
            newer,
            Run("Cancelled", now)
        };

        var active = LiveNow.ActiveOf(runs);

        Assert.That(active.Select(r => r.Id), Is.EqualTo(new[] { newer.Id, older.Id }));
    }

    [Test]
    public void ActiveOf_WithNoActiveRuns_IsEmpty()
        => Assert.That(LiveNow.ActiveOf([Run("Success", DateTimeOffset.UtcNow)]), Is.Empty);
}
