using Auxilia.Governance;
using Auxilia.Governance.IdentityImport;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.SystemTestSuite.IdentityImport;

/// <summary>
/// Identity import (#23) against a REAL OpenLDAP server: connection test, import with
/// default-role and group→role mapping, audit trail, and idempotent re-import.
/// </summary>
[TestFixture]
[Category("System")]
public class IdentityImportSystemTests
{
    private const string Actor = "system-test-admin";

    private static IdentityImportService Service
        => IdentityImportEnvironment.Services.GetRequiredService<IdentityImportService>();

    private static IdentitySourceDraft LdapDraft(string name)
    {
        var draft = new IdentitySourceDraft
        {
            Name = name,
            ConnectorType = "ldap",
            DefaultRole = BuiltInRoles.User
        };
        foreach (var (key, value) in IdentityImportEnvironment.LdapSettings())
            draft.Settings[key] = value;
        draft.GroupRoleMappings["reviewers"] = BuiltInRoles.Operator;
        return draft;
    }

    [Test]
    public async Task TestConnection_AgainstRealDirectory_ReportsReachableWithUserCount()
    {
        var source = await Service.SaveAsync(Actor, LdapDraft("ldap-connection-test"));

        var result = await Service.TestConnectionAsync(source.Id);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(result.Message, Does.Contain("3 user(s)"));
            Assert.That(result.Message, Does.Not.Contain(IdentityImportEnvironment.AdminPassword),
                "connection results must never leak the bind password");
        });
    }

    [Test]
    public async Task Import_CreatesPrincipalsWithMappedRoles_Audits_AndReimportsIdempotently()
    {
        var source = await Service.SaveAsync(Actor, LdapDraft("ldap-import"));
        var principals = IdentityImportEnvironment.Services.GetRequiredService<IDataAccess<PrincipalRecord>>();
        var roleAssignments = IdentityImportEnvironment.Services.GetRequiredService<IDataAccess<RoleAssignmentRecord>>();
        var auditRecords = IdentityImportEnvironment.Services.GetRequiredService<IDataAccess<AuditRecord>>();

        // ---- first import: the three seeded users arrive with deterministic IDs and roles.
        var first = await Service.ImportAsync(Actor, source.Id);
        Assert.Multiple(() =>
        {
            Assert.That(first.Created, Is.EqualTo(3), $"warnings: {string.Join("; ", first.Warnings)}");
            Assert.That(first.Updated, Is.EqualTo(0));
            Assert.That(first.Disabled, Is.EqualTo(0));
        });

        var ada = await principals.ReadAsync(IdentityImportService.PrincipalIdFor(
            source.Id, $"uid=ada,{IdentityImportEnvironment.PeopleBaseDn}"));
        Assert.That(ada, Is.Not.Null, "principal ID must derive deterministically from (source, entry DN)");
        Assert.Multiple(() =>
        {
            Assert.That(ada!.DisplayName, Is.EqualTo("Ada Lovelace"));
            Assert.That(ada.Kind, Is.EqualTo("Human"));
            Assert.That(ada.Status, Is.EqualTo("Active"));
            Assert.That(ada.ExternalSubject, Does.StartWith($"identity-source:{source.Id:D}:"));
        });

        var linusId = IdentityImportService.PrincipalIdFor(
            source.Id, $"uid=linus,{IdentityImportEnvironment.PeopleBaseDn}");
        var assignments = (await roleAssignments.ReadAsync()).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(RolesOf(assignments, ada!.Id),
                Is.EquivalentTo(new[] { BuiltInRoles.User, BuiltInRoles.Operator }),
                "ada is a reviewer → default role + mapped role");
            Assert.That(RolesOf(assignments, linusId),
                Is.EquivalentTo(new[] { BuiltInRoles.User }),
                "linus is in no mapped group → default role only");
        });

        var audits = (await auditRecords.ReadAsync()).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(audits.Count(a => a.Action == "identity-import.run" && a.Subject == "ldap-import"),
                Is.EqualTo(1));
            Assert.That(audits.Count(a => a.Action == "identity-import.applied" && a.Outcome == "created"),
                Is.GreaterThanOrEqualTo(3));
            Assert.That(audits.Any(a =>
                    ($"{a.Outcome}|{a.DetailJson}").Contains(IdentityImportEnvironment.AdminPassword)),
                Is.False, "no audit entry may carry the bind password");
        });

        // ---- re-import: idempotent — no duplicates, nothing created or updated.
        var second = await Service.ImportAsync(Actor, source.Id);
        Assert.Multiple(async () =>
        {
            Assert.That(second.Created, Is.EqualTo(0));
            Assert.That(second.Updated, Is.EqualTo(0));
            Assert.That(second.Disabled, Is.EqualTo(0));
            Assert.That(second.Skipped, Is.EqualTo(3), "unchanged users count as skipped");

            var all = (await principals.ReadAsync()).ToList();
            Assert.That(all.Count(p =>
                    p.ExternalSubject != null &&
                    p.ExternalSubject.StartsWith($"identity-source:{source.Id:D}:")),
                Is.EqualTo(3), "re-import must not create duplicates");
        });

        var view = (await Service.ListAsync()).Single(s => s.Name == "ldap-import");
        Assert.That(view.LastImport, Is.Not.Null);
        Assert.That(view.LastImport!.Skipped, Is.EqualTo(3), "the persisted summary reflects the last run");
    }

    private static IReadOnlyList<string> RolesOf(IReadOnlyList<RoleAssignmentRecord> assignments, Guid principalId)
        => assignments.Where(a => a.PrincipalId == principalId).Select(a => a.RoleName).ToList();
}
