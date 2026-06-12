using Auxilia.BackendService.Dashboard;
using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;
using Moq;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class DashboardComposerTests
{
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _instanceId = Guid.NewGuid();

    private IDataAccess<DashboardRecord> _dashboards = null!;
    private IDataAccess<WorkflowInstanceRecord> _instances = null!;
    private IDataAccess<AuditRecord> _auditRecords = null!;
    private Mock<IPolicyEngine> _policyEngine = null!;
    private DashboardComposer _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dashboards = new InMemoryDataAccess<DashboardRecord>();
        _instances = new InMemoryDataAccess<WorkflowInstanceRecord>();
        _auditRecords = new InMemoryDataAccess<AuditRecord>();
        _policyEngine = new Mock<IPolicyEngine>(MockBehavior.Strict);
        _sut = new DashboardComposer(
            _dashboards, _instances, _policyEngine.Object,
            new AuditLog(_auditRecords, TimeProvider.System));

        await _instances.SaveAsync(new WorkflowInstanceRecord
        {
            Id = _instanceId,
            WorkflowType = "code-review",
            State = "Running",
            CreatedUtc = DateTimeOffset.UtcNow
        });
    }

    [TearDown]
    public void TearDown()
    {
        (_dashboards as IDisposable)?.Dispose();
        (_instances as IDisposable)?.Dispose();
        (_auditRecords as IDisposable)?.Dispose();
    }

    private void AllowViewSubscribe(Guid actor)
        => _policyEngine
            .Setup(p => p.EvaluateAsync(
                It.Is<PolicyContext>(c =>
                    c.PrincipalId == actor &&
                    c.Action == PermissionActions.ViewSubscribe &&
                    c.WorkflowType == "code-review"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PolicyDecision.Allow("role"));

    [Test]
    public async Task PinAsync_OwnDashboard_PersistsOrderedPinsAndAudits()
    {
        AllowViewSubscribe(_owner);

        await _sut.PinAsync(_owner, _owner, new DashboardPin(_instanceId, "log", "Review log"));
        await _sut.PinAsync(_owner, _owner, new DashboardPin(_instanceId, "findings", "Findings"));

        var pins = await _sut.GetPinsAsync(_owner);
        Assert.Multiple(async () =>
        {
            Assert.That(pins.Select(p => p.ViewName), Is.EqualTo(new[] { "log", "findings" }));
            var audit = await _auditRecords.ReadAsync();
            Assert.That(audit.Count(a => a.Action == "dashboard.view-pinned"), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task PinAsync_SameViewTwice_ReplacesInsteadOfDuplicating()
    {
        AllowViewSubscribe(_owner);

        await _sut.PinAsync(_owner, _owner, new DashboardPin(_instanceId, "log", "Old title"));
        await _sut.PinAsync(_owner, _owner, new DashboardPin(_instanceId, "log", "New title"));

        var pins = await _sut.GetPinsAsync(_owner);
        Assert.Multiple(() =>
        {
            Assert.That(pins, Has.Count.EqualTo(1));
            Assert.That(pins[0].DisplayTitle, Is.EqualTo("New title"));
        });
    }

    [Test]
    public void PinAsync_ViewSubscribeDenied_Throws()
    {
        _policyEngine
            .Setup(p => p.EvaluateAsync(
                It.Is<PolicyContext>(c => c.Action == PermissionActions.ViewSubscribe),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PolicyDecision.Deny("no role grants view.subscribe"));

        Assert.That(
            () => _sut.PinAsync(_owner, _owner, new DashboardPin(_instanceId, "log", "Log")),
            Throws.InvalidOperationException.With.Message.Contains("view.subscribe denied"));
    }

    [Test]
    public void PinAsync_UnknownRun_Throws()
    {
        Assert.That(
            () => _sut.PinAsync(_owner, _owner, new DashboardPin(Guid.NewGuid(), "log", "Log")),
            Throws.InvalidOperationException.With.Message.Contains("Unknown run"));
    }

    [Test]
    public void PinAsync_ForeignDashboardWithoutDashboardManage_Throws()
    {
        var actor = Guid.NewGuid();
        _policyEngine
            .Setup(p => p.EvaluateAsync(
                It.Is<PolicyContext>(c =>
                    c.PrincipalId == actor && c.Action == PermissionActions.DashboardManage),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PolicyDecision.Deny("no role grants dashboard.manage"));

        Assert.That(
            () => _sut.PinAsync(actor, _owner, new DashboardPin(_instanceId, "log", "Log")),
            Throws.InvalidOperationException.With.Message.Contains("dashboard.manage denied"));
    }

    [Test]
    public async Task PinAsync_ForeignDashboardWithDashboardManage_IsAllowed()
    {
        var actor = Guid.NewGuid();
        _policyEngine
            .Setup(p => p.EvaluateAsync(
                It.Is<PolicyContext>(c =>
                    c.PrincipalId == actor && c.Action == PermissionActions.DashboardManage),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PolicyDecision.Allow("role"));
        AllowViewSubscribe(actor);

        await _sut.PinAsync(actor, _owner, new DashboardPin(_instanceId, "log", "Log"));

        Assert.That(await _sut.GetPinsAsync(_owner), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task UnpinAsync_RemovesPinAndAudits()
    {
        AllowViewSubscribe(_owner);
        await _sut.PinAsync(_owner, _owner, new DashboardPin(_instanceId, "log", "Log"));

        await _sut.UnpinAsync(_owner, _owner, _instanceId, "log");

        Assert.Multiple(async () =>
        {
            Assert.That(await _sut.GetPinsAsync(_owner), Is.Empty);
            var audit = await _auditRecords.ReadAsync();
            Assert.That(audit.Count(a => a.Action == "dashboard.view-unpinned"), Is.EqualTo(1));
        });
    }
}
