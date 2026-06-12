using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class WorkflowInstanceTokenRegistryTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private ManualTimeProvider _time = null!;
    private WorkflowInstanceTokenRegistry _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider();
        _sut = new WorkflowInstanceTokenRegistry(
            Options.Create(new WorkflowDispatcherSettings { InstanceTokenLifetime = TimeSpan.FromMinutes(15) }),
            _time);
    }

    [Test]
    public void Issue_ProducesDistinctInstanceIdsAndTokens()
    {
        var first = _sut.Issue("wf");
        var second = _sut.Issue("wf");

        Assert.That(first.WorkflowInstanceId, Is.Not.EqualTo(second.WorkflowInstanceId));
        Assert.That(first.Token, Is.Not.EqualTo(second.Token));
    }

    [Test]
    public void Validate_IssuedToken_ReturnsTrue()
    {
        var issued = _sut.Issue("wf");
        Assert.That(_sut.Validate(issued.WorkflowInstanceId, issued.Token), Is.True);
    }

    [Test]
    public void Validate_WrongToken_ReturnsFalse()
    {
        var issued = _sut.Issue("wf");
        Assert.That(_sut.Validate(issued.WorkflowInstanceId, "wrong"), Is.False);
    }

    [Test]
    public void Validate_NullOrEmptyToken_ReturnsFalse()
    {
        var issued = _sut.Issue("wf");
        Assert.Multiple(() =>
        {
            Assert.That(_sut.Validate(issued.WorkflowInstanceId, null), Is.False);
            Assert.That(_sut.Validate(issued.WorkflowInstanceId, string.Empty), Is.False);
        });
    }

    [Test]
    public void Validate_UnknownInstance_ReturnsFalse()
    {
        Assert.That(_sut.Validate(Guid.NewGuid(), "anything"), Is.False);
    }

    [Test]
    public void Validate_AfterConsume_ReturnsFalse()
    {
        var issued = _sut.Issue("wf");
        _sut.Consume(issued.WorkflowInstanceId);
        Assert.That(_sut.Validate(issued.WorkflowInstanceId, issued.Token), Is.False);
    }

    [Test]
    public void Validate_ExpiredToken_ReturnsFalse()
    {
        var issued = _sut.Issue("wf");
        _time.Now += TimeSpan.FromMinutes(16);
        Assert.That(_sut.Validate(issued.WorkflowInstanceId, issued.Token), Is.False);
    }

    [Test]
    public void Validate_JustBeforeExpiry_ReturnsTrue()
    {
        var issued = _sut.Issue("wf");
        _time.Now += TimeSpan.FromMinutes(14);
        Assert.That(_sut.Validate(issued.WorkflowInstanceId, issued.Token), Is.True);
    }
}
