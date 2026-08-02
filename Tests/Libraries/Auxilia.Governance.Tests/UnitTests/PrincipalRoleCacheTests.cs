using Auxilia.Core.Contracts;
using Auxilia.Governance.Identity;
using Auxilia.Governance.Policy;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Governance.Tests.UnitTests;

/// <summary>
/// The short-TTL principal/role cache behind bearer authentication and policy checks:
/// disabled by default, TTL-bounded staleness when enabled, and EAGER invalidation on the
/// writes where staleness would be a security bug (disable, role revoke, group changes).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class PrincipalRoleCacheTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private ManualTimeProvider _time = null!;
    private InMemoryDataAccess<PrincipalRecord> _principals = null!;
    private InMemoryDataAccess<RoleAssignmentRecord> _roles = null!;
    private InMemoryDataAccess<CredentialRecord> _credentials = null!;
    private InMemoryDataAccess<AuditRecord> _audit = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider();
        _principals = new InMemoryDataAccess<PrincipalRecord>();
        _roles = new InMemoryDataAccess<RoleAssignmentRecord>();
        _credentials = new InMemoryDataAccess<CredentialRecord>();
        _audit = new InMemoryDataAccess<AuditRecord>();
    }

    [TearDown]
    public void TearDown()
    {
        _principals.Dispose();
        _roles.Dispose();
        _credentials.Dispose();
        _audit.Dispose();
    }

    private PrincipalRoleCache Cache(int seconds)
        => new(new GovernanceSettings { PrincipalCacheSeconds = seconds }, _time);

    private PrincipalDirectory Directory(PrincipalRoleCache cache)
        => new(_principals, _roles, _credentials, new AuditLog(_audit, _time), cache);

    private LocalIdentityProvider Provider(PrincipalRoleCache cache)
        => new(_credentials, _principals, _roles, cache);

    private PolicyEngine Engine(PrincipalRoleCache cache, GroupRoleResolver? groups = null)
        => new(_principals, _roles,
            new WorkflowTypeAccessStore(new InMemoryDataAccess<WorkflowTypeAccessRecord>()),
            new AuditLog(_audit, _time), groups, cache);

    private async Task<(Guid PrincipalId, string ApiKey)> SeedOperatorAsync(PrincipalDirectory directory)
    {
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync("svc");
        await directory.AssignRoleAsync(principal.Id, BuiltInRoles.Operator);
        return (principal.Id, apiKey);
    }

    [Test]
    public async Task DisabledCache_AlwaysReadsTheStore()
    {
        var cache = Cache(0);
        var directory = Directory(cache);
        var provider = Provider(cache);
        var (id, apiKey) = await SeedOperatorAsync(directory);

        Assert.That(await provider.AuthenticateApiKeyAsync(apiKey), Is.Not.Null);
        // Bypass the directory (no invalidation) — a disabled cache must still see the change.
        await _principals.SaveAsync((await _principals.ReadAsync(id))! with { Status = "Disabled" });
        Assert.That(await provider.AuthenticateApiKeyAsync(apiKey), Is.Null);
    }

    [Test]
    public async Task EnabledCache_ServesWithinTtl_AndExpiresAfterIt()
    {
        var cache = Cache(5);
        var directory = Directory(cache);
        var provider = Provider(cache);
        var (id, apiKey) = await SeedOperatorAsync(directory);

        Assert.That(await provider.AuthenticateApiKeyAsync(apiKey), Is.Not.Null);
        // A store write WITHOUT invalidation (simulating another node's write) stays invisible…
        await _principals.SaveAsync((await _principals.ReadAsync(id))! with { Status = "Disabled" });
        Assert.That(await provider.AuthenticateApiKeyAsync(apiKey), Is.Not.Null,
            "within the TTL the cached session serves");

        // …until the TTL elapses.
        _time.Now = _time.Now.AddSeconds(6);
        Assert.That(await provider.AuthenticateApiKeyAsync(apiKey), Is.Null,
            "cross-node staleness is bounded by the TTL");
    }

    [Test]
    public async Task DisablingAPrincipal_InvalidatesEagerly_NoTtlWindow()
    {
        var cache = Cache(60);
        var directory = Directory(cache);
        var provider = Provider(cache);
        var (id, apiKey) = await SeedOperatorAsync(directory);

        Assert.That(await provider.AuthenticateApiKeyAsync(apiKey), Is.Not.Null);
        await directory.SetEnabledAsync(id, false);
        Assert.That(await provider.AuthenticateApiKeyAsync(apiKey), Is.Null,
            "a disabled principal must stop authenticating immediately, not at TTL expiry");
    }

    [Test]
    public async Task RoleRevocation_InvalidatesThePolicyPath()
    {
        var cache = Cache(60);
        var directory = Directory(cache);
        var engine = Engine(cache);
        var (id, _) = await SeedOperatorAsync(directory);

        var before = await engine.EvaluateAsync(new PolicyContext(id, PermissionActions.WorkflowTrigger, "x"));
        Assert.That(before.Allowed, Is.True);

        await directory.RevokeRoleAsync(id, BuiltInRoles.Operator);

        var after = await engine.EvaluateAsync(new PolicyContext(id, PermissionActions.WorkflowTrigger, "x"));
        Assert.That(after.Allowed, Is.False, "a revoked role must lose its permissions immediately");
    }

    [Test]
    public async Task GroupRoleChange_ClearsTheCache_PolicySeesItImmediately()
    {
        var cache = Cache(60);
        var directory = Directory(cache);
        using var groups = new InMemoryDataAccess<GroupRecord>();
        using var memberships = new InMemoryDataAccess<GroupMembershipRecord>();
        using var groupRoles = new InMemoryDataAccess<GroupRoleRecord>();
        var groupDirectory = new GroupDirectory(
            groups, memberships, groupRoles, new AuditLog(_audit, _time), cache);
        var engine = Engine(cache, new GroupRoleResolver(memberships, groupRoles));

        var (principal, _) = await directory.CreateApiKeyPrincipalAsync("svc");
        var group = await groupDirectory.CreateAsync("ops");
        await groupDirectory.AddMemberAsync(group.Id, principal.Id);

        var before = await engine.EvaluateAsync(
            new PolicyContext(principal.Id, PermissionActions.WorkflowTrigger, "x"));
        Assert.That(before.Allowed, Is.False);

        await groupDirectory.AssignRoleAsync(group.Id, BuiltInRoles.Operator);

        var after = await engine.EvaluateAsync(
            new PolicyContext(principal.Id, PermissionActions.WorkflowTrigger, "x"));
        Assert.That(after.Allowed, Is.True,
            "a group role grant must reach members immediately (cache cleared)");
    }
}
