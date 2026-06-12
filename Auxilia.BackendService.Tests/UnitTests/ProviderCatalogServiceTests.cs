using System.Text.Json;
using Auxilia.BackendService.Dashboard;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class ProviderCatalogServiceTests
{
    private IDataAccess<SlotProviderRecord> _providers = null!;
    private IDataAccess<ProviderCatalogRecord> _catalog = null!;
    private IDataAccess<AuditRecord> _auditRecords = null!;
    private ProviderCatalogService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _providers = new InMemoryDataAccess<SlotProviderRecord>();
        _catalog = new InMemoryDataAccess<ProviderCatalogRecord>();
        _auditRecords = new InMemoryDataAccess<AuditRecord>();
        _sut = new ProviderCatalogService(_providers, _catalog,
            new AuditLog(_auditRecords, TimeProvider.System));
    }

    [TearDown]
    public void TearDown()
    {
        (_providers as IDisposable)?.Dispose();
        (_catalog as IDisposable)?.Dispose();
        (_auditRecords as IDisposable)?.Dispose();
    }

    private static readonly SettingDescriptor[] EmailDescriptors =
    [
        new("ImapHost", "IMAP host", SettingKind.Text, Required: true, HelpText: "Server host."),
        new("Password", "Password", SettingKind.Secret, Required: true),
        new("Folder", "Mail folder", SettingKind.Text, DefaultValue: "INBOX")
    ];

    private Task SeedProviderAsync(
        string providerType = "email-work-items",
        string dllPath = "/plugins/email.slothandler.dll",
        IReadOnlyList<SettingDescriptor>? descriptors = null)
        => _providers.SaveAsync(new SlotProviderRecord
        {
            Id = SlotProviderRecord.IdFor(providerType),
            ProviderType = providerType,
            DllPath = dllPath,
            SettingDescriptorsJson = descriptors is null ? null : JsonSerializer.Serialize(descriptors)
        });

    // ------------------------------------------------------------------ listing

    [Test]
    public async Task List_ProviderWithoutCuration_DefaultsToUnavailableUncategorized()
    {
        await SeedProviderAsync(descriptors: EmailDescriptors);

        var entries = await _sut.ListAsync();

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Available, Is.False, "availability must be deny-by-default");
            Assert.That(entries[0].Category, Is.Empty);
            Assert.That(entries[0].Descriptors.Select(d => d.Key),
                Is.EqualTo(new[] { "ImapHost", "Password", "Folder" }));
            Assert.That(entries[0].Overrides, Is.Empty);
        });
    }

    [Test]
    public async Task List_LegacyProviderWithoutDescriptors_YieldsEmptyDescriptorList()
    {
        await SeedProviderAsync("legacy-provider", "/plugins/legacy.slothandler.dll");

        var entries = await _sut.ListAsync();

        Assert.That(entries.Single().Descriptors, Is.Empty);
    }

    [Test]
    public async Task List_DependencyLibraryRegistrations_AreNotPartOfTheCatalog()
    {
        await SeedProviderAsync("e2e-dep-0", "/plugins/MailKit.dll");
        await SeedProviderAsync("email-work-items", "/plugins/email.slothandler.dll");

        var entries = await _sut.ListAsync();

        Assert.That(entries.Select(e => e.ProviderType), Is.EqualTo(new[] { "email-work-items" }));
    }

    // ------------------------------------------------------------------ availability

    [Test]
    public void SetAvailability_UnknownProvider_IsRejected()
        => Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.SetAvailabilityAsync("admin-1", "ghost", available: true));

    [Test]
    public async Task SetAvailability_Toggle_PersistsAndAudits()
    {
        await SeedProviderAsync();

        var entry = await _sut.SetAvailabilityAsync("admin-1", "email-work-items", available: true);

        Assert.That(entry.Available, Is.True);
        Assert.That((await _sut.ListAsync()).Single().Available, Is.True);
        var audit = (await _auditRecords.ReadAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(audit.Actor, Is.EqualTo("admin-1"));
            Assert.That(audit.Action, Is.EqualTo("provider-catalog.availability-changed"));
            Assert.That(audit.Subject, Is.EqualTo("email-work-items"));
            Assert.That(audit.Outcome, Is.EqualTo("available"));
        });
    }

    [Test]
    public async Task SetAvailability_DoesNotWipeCategoryOrOverrides()
    {
        await SeedProviderAsync(descriptors: EmailDescriptors);
        await _sut.SetCategoryAsync("admin-1", "email-work-items", "task-source");
        await _sut.SetOverridesAsync("admin-1", "email-work-items",
            [new SettingDescriptorOverride("Folder", DefaultValue: "Tickets")]);

        var entry = await _sut.SetAvailabilityAsync("admin-1", "email-work-items", available: true);

        Assert.Multiple(() =>
        {
            Assert.That(entry.Category, Is.EqualTo("task-source"));
            Assert.That(entry.Overrides, Has.Count.EqualTo(1));
        });
    }

    // ------------------------------------------------------------------ category

    [Test]
    public async Task SetCategory_TrimsAndAudits()
    {
        await SeedProviderAsync();

        var entry = await _sut.SetCategoryAsync("admin-1", "email-work-items", "  task-source ");

        Assert.That(entry.Category, Is.EqualTo("task-source"));
        var audit = (await _auditRecords.ReadAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(audit.Action, Is.EqualTo("provider-catalog.category-changed"));
            Assert.That(audit.Outcome, Is.EqualTo("task-source"));
        });
    }

    [Test]
    public void SetCategory_UnknownProvider_IsRejected()
        => Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.SetCategoryAsync("admin-1", "ghost", "task-source"));

    // ------------------------------------------------------------------ overrides

    [Test]
    public async Task SetOverrides_UnknownSettingKey_IsRejected()
    {
        await SeedProviderAsync(descriptors: EmailDescriptors);

        Assert.ThrowsAsync<ArgumentException>(() => _sut.SetOverridesAsync(
            "admin-1", "email-work-items", [new SettingDescriptorOverride("NoSuchKey", Label: "X")]));
    }

    [Test]
    public async Task SetOverrides_DuplicateKey_IsRejected()
    {
        await SeedProviderAsync(descriptors: EmailDescriptors);

        Assert.ThrowsAsync<ArgumentException>(() => _sut.SetOverridesAsync(
            "admin-1", "email-work-items",
            [new SettingDescriptorOverride("Folder"), new SettingDescriptorOverride("Folder")]));
    }

    [Test]
    public async Task SetOverrides_EmptyKey_IsRejected()
    {
        await SeedProviderAsync(descriptors: EmailDescriptors);

        Assert.ThrowsAsync<ArgumentException>(() => _sut.SetOverridesAsync(
            "admin-1", "email-work-items", [new SettingDescriptorOverride("  ")]));
    }

    [Test]
    public async Task SetOverrides_Valid_PersistsAndAuditsKeyNamesOnly()
    {
        await SeedProviderAsync(descriptors: EmailDescriptors);

        await _sut.SetOverridesAsync("admin-1", "email-work-items",
            [new SettingDescriptorOverride("Folder", Label: "Ticket folder", DefaultValue: "Tickets")]);

        var audit = (await _auditRecords.ReadAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(audit.Action, Is.EqualTo("provider-catalog.overrides-changed"));
            Assert.That(audit.DetailJson, Does.Contain("Folder"));
            Assert.That(audit.DetailJson, Does.Not.Contain("Tickets"),
                "audit must record which keys changed, not the values");
        });
    }

    // ------------------------------------------------------------------ merge

    [Test]
    public void Merge_OverrideReplacesLabelHelpAndDefault_ButNeverKindKeyRequiredChoices()
    {
        var descriptors = new[]
        {
            new SettingDescriptor("Folder", "Mail folder", SettingKind.Choice, Required: true,
                HelpText: "Polled folder.", DefaultValue: "INBOX", Choices: ["INBOX", "Archive"])
        };

        var merged = ProviderCatalogService.Merge(descriptors,
            [new SettingDescriptorOverride("Folder", "Ticket folder", "Use the shared inbox.", "Archive")]);

        Assert.Multiple(() =>
        {
            Assert.That(merged.Single().Label, Is.EqualTo("Ticket folder"));
            Assert.That(merged.Single().HelpText, Is.EqualTo("Use the shared inbox."));
            Assert.That(merged.Single().DefaultValue, Is.EqualTo("Archive"));
            Assert.That(merged.Single().Key, Is.EqualTo("Folder"));
            Assert.That(merged.Single().Kind, Is.EqualTo(SettingKind.Choice));
            Assert.That(merged.Single().Required, Is.True);
            Assert.That(merged.Single().Choices, Is.EqualTo(new[] { "INBOX", "Archive" }));
        });
    }

    [Test]
    public void Merge_PartialOverride_KeepsManifestValuesForUntouchedAspects()
    {
        var descriptors = new[]
        {
            new SettingDescriptor("ImapPort", "IMAP port", SettingKind.Number,
                HelpText: "Server port.", DefaultValue: "3143")
        };

        var merged = ProviderCatalogService.Merge(descriptors,
            [new SettingDescriptorOverride("ImapPort", DefaultValue: "993")]);

        Assert.Multiple(() =>
        {
            Assert.That(merged.Single().Label, Is.EqualTo("IMAP port"));
            Assert.That(merged.Single().HelpText, Is.EqualTo("Server port."));
            Assert.That(merged.Single().DefaultValue, Is.EqualTo("993"));
        });
    }

    [Test]
    public void Merge_NoOverrides_ReturnsDescriptorsUnchanged()
    {
        var merged = ProviderCatalogService.Merge(EmailDescriptors, []);

        Assert.That(merged, Is.EqualTo(EmailDescriptors));
    }

    // ------------------------------------------------------------------ read path for #20

    [Test]
    public async Task ListAvailableByCategory_GroupsAvailableProviders_WithMergedDescriptors()
    {
        await SeedProviderAsync("email-work-items", "/plugins/email.slothandler.dll", EmailDescriptors);
        await SeedProviderAsync("github-repo", "/plugins/github.slothandler.dll");
        await SeedProviderAsync("hidden-provider", "/plugins/hidden.slothandler.dll");

        await _sut.SetCategoryAsync("admin-1", "email-work-items", "task-source");
        await _sut.SetAvailabilityAsync("admin-1", "email-work-items", true);
        await _sut.SetCategoryAsync("admin-1", "github-repo", "repository");
        await _sut.SetAvailabilityAsync("admin-1", "github-repo", true);
        await _sut.SetOverridesAsync("admin-1", "email-work-items",
            [new SettingDescriptorOverride("Folder", DefaultValue: "Tickets")]);

        var byCategory = await _sut.ListAvailableByCategoryAsync();

        Assert.Multiple(() =>
        {
            Assert.That(byCategory.Keys, Is.EquivalentTo(new[] { "task-source", "repository" }));
            Assert.That(byCategory["repository"].Single().ProviderType, Is.EqualTo("github-repo"));
            var email = byCategory["task-source"].Single();
            Assert.That(email.ProviderType, Is.EqualTo("email-work-items"));
            Assert.That(email.Descriptors.Single(d => d.Key == "Folder").DefaultValue,
                Is.EqualTo("Tickets"), "the read path must serve MERGED descriptors");
        });
    }

    [Test]
    public async Task ListAvailableByCategory_HiddenProviders_AreNotServed()
    {
        await SeedProviderAsync("hidden-provider", "/plugins/hidden.slothandler.dll");

        var byCategory = await _sut.ListAvailableByCategoryAsync();

        Assert.That(byCategory, Is.Empty);
    }
}
