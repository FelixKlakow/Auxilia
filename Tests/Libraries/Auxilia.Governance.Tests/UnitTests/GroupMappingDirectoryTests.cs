namespace Auxilia.Governance.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class GroupMappingDirectoryTests
{
    private GovernanceTestContext _ctx = null!;

    [SetUp]
    public void SetUp() => _ctx = new GovernanceTestContext();

    [Test]
    public async Task Create_PersistsMapping_AndIsResolvableByTheSignInResolver()
    {
        await _ctx.GroupMappingDirectory.CreateAsync("entra", "group-ops", BuiltInRoles.Operator);

        var roles = await _ctx.GroupMappingResolver.ResolveRolesAsync("entra", ["group-ops"]);
        Assert.That(roles, Is.EquivalentTo(new[] { BuiltInRoles.Operator }));
    }

    [Test]
    public void Create_WithUnknownRole_Throws()
        => Assert.ThatAsync(
            () => _ctx.GroupMappingDirectory.CreateAsync("entra", "group-ops", "Wizard"),
            Throws.ArgumentException);

    [Test]
    public async Task Create_IsIdempotent_PerProviderGroupRole()
    {
        await _ctx.GroupMappingDirectory.CreateAsync("entra", "group-ops", BuiltInRoles.Operator);
        await _ctx.GroupMappingDirectory.CreateAsync("entra", "group-ops", BuiltInRoles.Operator);

        Assert.That(await _ctx.GroupMappingDirectory.ListAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Remove_DeletesMapping()
    {
        var mapping = await _ctx.GroupMappingDirectory.CreateAsync("entra", "group-ops", BuiltInRoles.Operator);

        Assert.That(await _ctx.GroupMappingDirectory.RemoveAsync(mapping.Id), Is.True);
        Assert.That(await _ctx.GroupMappingDirectory.ListAsync(), Is.Empty);
    }

    [Test]
    public async Task Mutations_AreAudited()
    {
        var mapping = await _ctx.GroupMappingDirectory.CreateAsync("entra", "group-ops", BuiltInRoles.Operator);
        await _ctx.GroupMappingDirectory.RemoveAsync(mapping.Id);

        Assert.Multiple(async () =>
        {
            Assert.That(await _ctx.AuditCountAsync("group-mapping.created"), Is.EqualTo(1));
            Assert.That(await _ctx.AuditCountAsync("group-mapping.removed"), Is.EqualTo(1));
        });
    }
}
