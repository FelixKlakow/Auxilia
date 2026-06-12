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

    // ------------------------------------------------------------------ TryBeginRegistration

    [Test]
    public void TryBeginRegistration_ValidToken_ReturnsTrue()
    {
        var issued = _sut.Issue("wf");
        Assert.That(_sut.TryBeginRegistration(issued.WorkflowInstanceId, issued.Token), Is.True);
    }

    [Test]
    public void TryBeginRegistration_SecondCall_ReturnsFalse()
    {
        var issued = _sut.Issue("wf");
        Assert.That(_sut.TryBeginRegistration(issued.WorkflowInstanceId, issued.Token), Is.True);
        Assert.That(_sut.TryBeginRegistration(issued.WorkflowInstanceId, issued.Token), Is.False,
            "Registration must be single-use.");
    }

    [Test]
    public void TryBeginRegistration_WrongToken_ReturnsFalse()
    {
        var issued = _sut.Issue("wf");
        Assert.That(_sut.TryBeginRegistration(issued.WorkflowInstanceId, "wrong"), Is.False);
        Assert.That(_sut.IsRegistered(issued.WorkflowInstanceId), Is.False);
    }

    [Test]
    public void TryBeginRegistration_ExpiredUnregisteredToken_ReturnsFalse()
    {
        var issued = _sut.Issue("wf");
        _time.Now += TimeSpan.FromMinutes(16);
        Assert.That(_sut.TryBeginRegistration(issued.WorkflowInstanceId, issued.Token), Is.False);
    }

    // ------------------------------------------------------------------ Post-registration token lifetime

    [Test]
    public void Validate_AfterTryBeginRegistration_StillReturnsTrue()
    {
        // The token remains the instance credential for just-in-time slot activations.
        var issued = _sut.Issue("wf");
        _sut.TryBeginRegistration(issued.WorkflowInstanceId, issued.Token);

        Assert.That(_sut.Validate(issued.WorkflowInstanceId, issued.Token), Is.True);
    }

    [Test]
    public void Validate_AfterExpiry_WhenRegistered_ReturnsTrue()
    {
        // The issuance lifetime bounds only the launch→registration window.
        var issued = _sut.Issue("wf");
        _sut.TryBeginRegistration(issued.WorkflowInstanceId, issued.Token);

        _time.Now += TimeSpan.FromMinutes(16);

        Assert.That(_sut.Validate(issued.WorkflowInstanceId, issued.Token), Is.True);
    }

    [Test]
    public void Validate_AfterExpiry_WhenNotRegistered_ReturnsFalse()
    {
        var issued = _sut.Issue("wf");
        _time.Now += TimeSpan.FromMinutes(16);
        Assert.That(_sut.Validate(issued.WorkflowInstanceId, issued.Token), Is.False);
    }

    // ------------------------------------------------------------------ IsRegistered / Consume

    [Test]
    public void IsRegistered_BeforeRegistration_ReturnsFalse()
    {
        var issued = _sut.Issue("wf");
        Assert.That(_sut.IsRegistered(issued.WorkflowInstanceId), Is.False);
    }

    [Test]
    public void IsRegistered_AfterRegistration_ReturnsTrue()
    {
        var issued = _sut.Issue("wf");
        _sut.TryBeginRegistration(issued.WorkflowInstanceId, issued.Token);
        Assert.That(_sut.IsRegistered(issued.WorkflowInstanceId), Is.True);
    }

    [Test]
    public void IsRegistered_UnknownInstance_ReturnsFalse()
    {
        Assert.That(_sut.IsRegistered(Guid.NewGuid()), Is.False);
    }

    [Test]
    public void Consume_RegisteredInstance_RemovesEntry()
    {
        var issued = _sut.Issue("wf");
        _sut.TryBeginRegistration(issued.WorkflowInstanceId, issued.Token);

        _sut.Consume(issued.WorkflowInstanceId);

        Assert.Multiple(() =>
        {
            Assert.That(_sut.Validate(issued.WorkflowInstanceId, issued.Token), Is.False);
            Assert.That(_sut.IsRegistered(issued.WorkflowInstanceId), Is.False);
        });
    }
}
