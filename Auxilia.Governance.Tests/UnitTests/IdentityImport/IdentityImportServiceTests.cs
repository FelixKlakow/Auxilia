using System.Security.Cryptography;
using Auxilia.Governance.IdentityImport;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Governance.Tests.UnitTests.IdentityImport;

[TestFixture]
[Category("Unit")]
public class IdentityImportServiceTests
{
    private const string Actor = "test-admin";
    private const string SecretToken = "super-secret-token";

    private InMemoryDataAccess<IdentitySourceRecord> _sources = null!;
    private InMemoryDataAccess<PrincipalRecord> _principals = null!;
    private InMemoryDataAccess<RoleAssignmentRecord> _roleAssignments = null!;
    private InMemoryDataAccess<AuditRecord> _auditRecords = null!;
    private FakeIdentityImportConnector _connector = null!;
    private IdentityImportService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _sources = new InMemoryDataAccess<IdentitySourceRecord>();
        _principals = new InMemoryDataAccess<PrincipalRecord>();
        _roleAssignments = new InMemoryDataAccess<RoleAssignmentRecord>();
        _auditRecords = new InMemoryDataAccess<AuditRecord>();
        _connector = new FakeIdentityImportConnector();
        _service = new IdentityImportService(
            _sources, _principals, _roleAssignments, [_connector],
            new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32)),
            new AuditLog(_auditRecords, TimeProvider.System),
            TimeProvider.System);
    }

    [TearDown]
    public void TearDown()
    {
        _sources.Dispose();
        _principals.Dispose();
        _roleAssignments.Dispose();
        _auditRecords.Dispose();
    }

    private IdentitySourceDraft NewDraft(string name = "test-source")
    {
        var draft = new IdentitySourceDraft { Name = name, ConnectorType = FakeIdentityImportConnector.Type };
        draft.Settings["Endpoint"] = "https://idp.example.org";
        draft.Settings["Token"] = SecretToken;
        return draft;
    }

    private async Task<IdentitySourceView> NewSourceAsync(
        string defaultRole = "", Dictionary<string, string>? mappings = null, bool disableMissing = false)
    {
        var draft = NewDraft();
        draft.DefaultRole = defaultRole;
        draft.DisableMissing = disableMissing;
        foreach (var (group, role) in mappings ?? [])
            draft.GroupRoleMappings[group] = role;
        return await _service.SaveAsync(Actor, draft);
    }

    private static ExternalUser User(
        string externalId, string username = "user", string displayName = "User",
        bool enabled = true, params string[] groups)
        => new(externalId, displayName, username, enabled, groups);

    // ------------------------------------------------------------------ source administration

    [Test]
    public async Task Save_ProtectsSettings_AppliesDefaults_AndAuditsWithoutValues()
    {
        await NewSourceAsync();

        var record = (await _sources.ReadAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(record.Id, Is.EqualTo(IdentitySourceRecord.IdFor("test-source")), "deterministic source id");
            Assert.That(record.ProtectedSettingsJson, Does.StartWith("enc1:"), "settings must be protected at rest");
            Assert.That(record.ProtectedSettingsJson, Does.Not.Contain(SecretToken));
        });

        var saved = (await _auditRecords.ReadAsync()).Single(a => a.Action == "identity-source.saved");
        Assert.Multiple(() =>
        {
            Assert.That(saved.Outcome, Is.EqualTo("created"));
            Assert.That(saved.DetailJson, Does.Contain("PageSize"), "configured keys are audited");
            Assert.That(saved.DetailJson, Does.Not.Contain(SecretToken), "values are not");
        });

        // The connector's declared default fills unset settings.
        await _service.ImportAsync(Actor, record.Id);
        Assert.That(_connector.LastSettings!["PageSize"], Is.EqualTo("100"));
    }

    [Test]
    public async Task Save_Update_EmptySecretInput_KeepsStoredValue()
    {
        var source = await NewSourceAsync();

        var update = NewDraft();
        update.ExistingName = source.Name;
        update.Settings["Endpoint"] = "https://other.example.org";
        update.Settings["Token"] = ""; // write-only: empty keeps the stored secret

        var view = await _service.SaveAsync(Actor, update);
        Assert.Multiple(() =>
        {
            Assert.That(view.StoredSecretKeys, Does.Contain("Token"));
            Assert.That(view.Settings["Token"], Is.Empty, "secret values never leave the service");
            Assert.That(view.Settings["Endpoint"], Is.EqualTo("https://other.example.org"));
        });

        await _service.ImportAsync(Actor, source.Id);
        Assert.That(_connector.LastSettings!["Token"], Is.EqualTo(SecretToken));
    }

    [Test]
    public void Save_RejectsUnknownConnector_UnknownRoles_AndMissingRequiredSettings()
    {
        Assert.ThrowsAsync<ArgumentException>(() => _service.SaveAsync(Actor,
            new IdentitySourceDraft { Name = "x", ConnectorType = "nope" }));

        var badRole = NewDraft("bad-role");
        badRole.DefaultRole = "Sovereign";
        Assert.ThrowsAsync<ArgumentException>(() => _service.SaveAsync(Actor, badRole));

        var badMapping = NewDraft("bad-mapping");
        badMapping.GroupRoleMappings["devs"] = "Sovereign";
        Assert.ThrowsAsync<ArgumentException>(() => _service.SaveAsync(Actor, badMapping));

        var missing = new IdentitySourceDraft { Name = "missing", ConnectorType = FakeIdentityImportConnector.Type };
        var ex = Assert.ThrowsAsync<ArgumentException>(() => _service.SaveAsync(Actor, missing));
        Assert.That(ex!.Message, Does.Contain("Endpoint").And.Contain("Token"));
    }

    [Test]
    public async Task Delete_IsAudited_AndLeavesImportedPrincipalsUntouched()
    {
        var source = await NewSourceAsync();
        _connector.Users.Add(User("u-1", "ada", "Ada"));
        await _service.ImportAsync(Actor, source.Id);

        Assert.That(await _service.DeleteAsync(Actor, source.Id), Is.True);

        Assert.Multiple(async () =>
        {
            Assert.That((await _sources.ReadAsync()).ToList(), Is.Empty);
            Assert.That((await _principals.ReadAsync()).ToList(), Has.Count.EqualTo(1),
                "deleting a source never deletes principals");
            Assert.That((await _auditRecords.ReadAsync())
                .Count(a => a.Action == "identity-source.deleted"), Is.EqualTo(1));
        });
    }

    // ------------------------------------------------------------------ import semantics

    [Test]
    public async Task Import_CreatesExternalPrincipals_WithDeterministicIds_AndNoCredentials()
    {
        var source = await NewSourceAsync();
        _connector.Users.Add(User("u-1", "ada", "Ada Lovelace"));
        _connector.Users.Add(User("u-2", "off", "Off Line", enabled: false));

        var summary = await _service.ImportAsync(Actor, source.Id);

        Assert.Multiple(() =>
        {
            Assert.That(summary.Created, Is.EqualTo(2));
            Assert.That(summary.Updated, Is.EqualTo(0));
            Assert.That(summary.Disabled, Is.EqualTo(0));
            Assert.That(summary.Skipped, Is.EqualTo(0));
        });

        var ada = await _principals.ReadAsync(IdentityImportService.PrincipalIdFor(source.Id, "u-1"));
        Assert.That(ada, Is.Not.Null, "principal id derives deterministically from (source, external id)");
        Assert.Multiple(() =>
        {
            Assert.That(ada!.Kind, Is.EqualTo("Human"));
            Assert.That(ada.DisplayName, Is.EqualTo("Ada Lovelace"));
            Assert.That(ada.ExternalSubject, Is.EqualTo($"identity-source:{source.Id:D}:u-1"));
            Assert.That(ada.Status, Is.EqualTo("Active"));
        });
        var disabled = await _principals.ReadAsync(IdentityImportService.PrincipalIdFor(source.Id, "u-2"));
        Assert.That(disabled!.Status, Is.EqualTo("Disabled"), "source-disabled users arrive disabled");
    }

    [Test]
    public async Task Reimport_IsIdempotent_NoDuplicates_UnchangedUsersCountAsSkipped()
    {
        var source = await NewSourceAsync(defaultRole: BuiltInRoles.User);
        _connector.Users.Add(User("u-1", "ada", "Ada"));
        _connector.Users.Add(User("u-2", "grace", "Grace"));
        await _service.ImportAsync(Actor, source.Id);

        var second = await _service.ImportAsync(Actor, source.Id);

        Assert.Multiple(async () =>
        {
            Assert.That(second.Created, Is.EqualTo(0));
            Assert.That(second.Updated, Is.EqualTo(0));
            Assert.That(second.Skipped, Is.EqualTo(2));
            Assert.That((await _principals.ReadAsync()).ToList(), Has.Count.EqualTo(2), "no duplicates");
            Assert.That((await _roleAssignments.ReadAsync()).ToList(), Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task Import_UpdatesChangedDisplayNameAndStatus()
    {
        var source = await NewSourceAsync();
        _connector.Users.Add(User("u-1", "ada", "Ada"));
        await _service.ImportAsync(Actor, source.Id);

        _connector.Users.Clear();
        _connector.Users.Add(User("u-1", "ada", "Ada Lovelace", enabled: false));
        var summary = await _service.ImportAsync(Actor, source.Id);

        var principal = await _principals.ReadAsync(IdentityImportService.PrincipalIdFor(source.Id, "u-1"));
        Assert.Multiple(() =>
        {
            Assert.That(summary.Updated, Is.EqualTo(1));
            Assert.That(principal!.DisplayName, Is.EqualTo("Ada Lovelace"));
            Assert.That(principal.Status, Is.EqualTo("Disabled"));
        });
    }

    [Test]
    public async Task Import_MissingUsers_AreDisabledOnlyWithFlag_AndNeverDeleted()
    {
        // Without the flag: the vanished user keeps its status.
        var keep = await NewSourceAsync();
        _connector.Users.Add(User("u-1", "ada", "Ada"));
        await _service.ImportAsync(Actor, keep.Id);
        _connector.Users.Clear();
        var keepSummary = await _service.ImportAsync(Actor, keep.Id);

        var kept = await _principals.ReadAsync(IdentityImportService.PrincipalIdFor(keep.Id, "u-1"));
        Assert.Multiple(() =>
        {
            Assert.That(keepSummary.Disabled, Is.EqualTo(0));
            Assert.That(kept!.Status, Is.EqualTo("Active"));
        });

        // With the flag: disabled — but the record always survives.
        var draft = NewDraft();
        draft.ExistingName = keep.Name;
        draft.DisableMissing = true;
        await _service.SaveAsync(Actor, draft);

        var disableSummary = await _service.ImportAsync(Actor, keep.Id);
        var disabled = await _principals.ReadAsync(IdentityImportService.PrincipalIdFor(keep.Id, "u-1"));
        Assert.Multiple(() =>
        {
            Assert.That(disableSummary.Disabled, Is.EqualTo(1));
            Assert.That(disabled, Is.Not.Null, "imports must NEVER delete principals");
            Assert.That(disabled!.Status, Is.EqualTo("Disabled"));
        });
    }

    [Test]
    public async Task Import_AppliesDefaultRole_AndGroupMappings_WithoutDuplicateAssignments()
    {
        var source = await NewSourceAsync(
            defaultRole: BuiltInRoles.User,
            mappings: new Dictionary<string, string> { ["reviewers"] = BuiltInRoles.Operator });
        _connector.Users.Add(User("u-1", "ada", "Ada", enabled: true, "reviewers", "unmapped-group"));
        _connector.Users.Add(User("u-2", "linus", "Linus"));

        await _service.ImportAsync(Actor, source.Id);

        var adaRoles = RolesOf(IdentityImportService.PrincipalIdFor(source.Id, "u-1"));
        var linusRoles = RolesOf(IdentityImportService.PrincipalIdFor(source.Id, "u-2"));
        Assert.Multiple(() =>
        {
            Assert.That(adaRoles, Is.EquivalentTo(new[] { BuiltInRoles.User, BuiltInRoles.Operator }));
            Assert.That(linusRoles, Is.EquivalentTo(new[] { BuiltInRoles.User }));
        });
    }

    [Test]
    public async Task Import_NeverStripsManuallyAssignedRoles()
    {
        var source = await NewSourceAsync(defaultRole: BuiltInRoles.User);
        _connector.Users.Add(User("u-1", "ada", "Ada"));
        await _service.ImportAsync(Actor, source.Id);

        // An administrator promotes the imported user by hand.
        var principalId = IdentityImportService.PrincipalIdFor(source.Id, "u-1");
        await _roleAssignments.SaveAsync(new RoleAssignmentRecord
        {
            Id = RoleAssignmentRecord.IdFor(principalId, BuiltInRoles.Administrator),
            PrincipalId = principalId,
            RoleName = BuiltInRoles.Administrator,
            Source = "Direct"
        });

        await _service.ImportAsync(Actor, source.Id);

        var assignments = (await _roleAssignments.ReadAsync())
            .Where(a => a.PrincipalId == principalId).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(assignments.Select(a => a.RoleName),
                Is.EquivalentTo(new[] { BuiltInRoles.User, BuiltInRoles.Administrator }));
            Assert.That(assignments.Single(a => a.RoleName == BuiltInRoles.Administrator).Source,
                Is.EqualTo("Direct"), "the manual assignment stays untouched");
        });
    }

    [Test]
    public async Task Import_CountsConnectorSkippedEntries_AndDuplicateExternalIds()
    {
        var source = await NewSourceAsync();
        _connector.Users.Add(User("u-1", "ada", "Ada"));
        _connector.Users.Add(User("u-1", "imposter", "Imposter"));
        _connector.SkippedEntries.Add("line 7: broken row");

        var summary = await _service.ImportAsync(Actor, source.Id);

        Assert.Multiple(async () =>
        {
            Assert.That(summary.Created, Is.EqualTo(1));
            Assert.That(summary.Skipped, Is.EqualTo(2), "one bad source entry + one duplicate");
            Assert.That(summary.Warnings, Has.Count.EqualTo(2));
            Assert.That((await _principals.ReadAsync()).Single().DisplayName, Is.EqualTo("Ada"),
                "first occurrence wins");
        });
    }

    [Test]
    public async Task Import_PersistsSummaryOnSource_AndAuditsRunAndPerUserEntries()
    {
        var source = await NewSourceAsync();
        _connector.Users.Add(User("u-1", "ada", "Ada"));

        await _service.ImportAsync(Actor, source.Id);

        var view = (await _service.ListAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(view.LastImport, Is.Not.Null);
            Assert.That(view.LastImport!.Created, Is.EqualTo(1));
            Assert.That(view.LastImportUtc, Is.Not.Null);
        });

        var audits = (await _auditRecords.ReadAsync()).ToList();
        var run = audits.Single(a => a.Action == "identity-import.run");
        var applied = audits.Single(a => a.Action == "identity-import.applied");
        Assert.Multiple(() =>
        {
            Assert.That(run.Actor, Is.EqualTo(Actor));
            Assert.That(run.Subject, Is.EqualTo("test-source"));
            Assert.That(run.Outcome, Does.Contain("1 created"));
            Assert.That(applied.Outcome, Is.EqualTo("created"));
            Assert.That(applied.Subject,
                Is.EqualTo(IdentityImportService.PrincipalIdFor(source.Id, "u-1").ToString("D")));
        });
    }

    [Test]
    public async Task AuditTrail_NeverContainsSecretSettingValues()
    {
        var source = await NewSourceAsync(defaultRole: BuiltInRoles.User);
        _connector.Users.Add(User("u-1", "ada", "Ada"));
        await _service.ImportAsync(Actor, source.Id);
        await _service.TestConnectionAsync(source.Id);
        await _service.DeleteAsync(Actor, source.Id);

        foreach (var audit in (await _auditRecords.ReadAsync()).ToList())
        {
            var rendered = $"{audit.Actor}|{audit.Action}|{audit.Subject}|{audit.Outcome}|{audit.DetailJson}";
            Assert.That(rendered, Does.Not.Contain(SecretToken),
                $"audit entry '{audit.Action}' must not leak secrets");
        }
    }

    private IReadOnlyList<string> RolesOf(Guid principalId)
        => _roleAssignments.ReadAsync().GetAwaiter().GetResult()
            .Where(a => a.PrincipalId == principalId)
            .Select(a => a.RoleName)
            .ToList();
}
