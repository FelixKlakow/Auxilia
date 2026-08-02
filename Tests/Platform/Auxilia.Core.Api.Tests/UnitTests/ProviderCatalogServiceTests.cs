using System.Text.Json;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// The Core-owned slot-provider catalog: deny-by-default availability, presentation/setting curation
/// kept separate from the plugin registration, and merge rules that keep keys/kinds manifest-owned.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class ProviderCatalogServiceTests
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
        _sut = new ProviderCatalogService(_providers, _catalog, new AuditLog(_auditRecords, TimeProvider.System));
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
        string? category = null,
        IReadOnlyList<SettingDescriptor>? descriptors = null)
        => _providers.SaveAsync(new SlotProviderRecord
        {
            Id = SlotProviderRecord.IdFor(providerType),
            ProviderType = providerType,
            DllPath = dllPath,
            Category = category,
            SettingDescriptorsJson = descriptors is null ? null : JsonSerializer.Serialize(descriptors)
        });

    private async Task<IReadOnlyList<ProviderCatalogEntry>> ListAsync(bool? available = null)
        => (await _sut.QueryAsync(new ProviderCatalogQuery(available, Take: 100), CancellationToken.None)).Items;

    [Test]
    public async Task Query_ProviderWithoutCuration_DefaultsToUnavailableUncategorized()
    {
        await SeedProviderAsync(descriptors: EmailDescriptors);

        var entries = await ListAsync();

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Available, Is.False, "availability must be deny-by-default");
            Assert.That(entries[0].Category, Is.Empty);
            Assert.That(entries[0].Descriptors.Select(d => d.Key),
                Is.EqualTo(new[] { "ImapHost", "Password", "Folder" }));
            Assert.That(entries[0].Descriptors.All(d => !d.Disabled), Is.True);
        });
    }

    [Test]
    public async Task Query_DependencyLibraryRegistrations_AreNotPartOfTheCatalog()
    {
        await SeedProviderAsync("e2e-dep-0", "/plugins/MailKit.dll");
        await SeedProviderAsync("email-work-items", "/plugins/email.slothandler.dll");

        Assert.That((await ListAsync()).Select(e => e.ProviderType), Is.EqualTo(new[] { "email-work-items" }));
    }

    [Test]
    public async Task Query_AvailableFilter_ReturnsOnlyMatchingProviders()
    {
        await SeedProviderAsync("email-work-items", "/plugins/email.slothandler.dll");
        await SeedProviderAsync("github-repo", "/plugins/github.slothandler.dll");
        await _sut.SetAvailabilityAsync("admin-1", "github-repo", available: true, CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That((await ListAsync(available: true)).Select(e => e.ProviderType),
                Is.EqualTo(new[] { "github-repo" }));
            Assert.That((await ListAsync(available: false)).Select(e => e.ProviderType),
                Is.EqualTo(new[] { "email-work-items" }));
        });
    }

    [Test]
    public void SetAvailability_UnknownProvider_IsRejected()
        => Assert.ThrowsAsync<KeyNotFoundException>(
            () => _sut.SetAvailabilityAsync("admin-1", "ghost", available: true, CancellationToken.None));

    [Test]
    public async Task SetAvailability_Toggle_PersistsAndAudits()
    {
        await SeedProviderAsync();

        var entry = await _sut.SetAvailabilityAsync("admin-1", "email-work-items", available: true, CancellationToken.None);

        Assert.That(entry.Available, Is.True);
        Assert.That((await ListAsync()).Single().Available, Is.True);
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
    public async Task SetAvailability_DoesNotWipeStoredCuration()
    {
        await SeedProviderAsync(descriptors: EmailDescriptors);
        await _catalog.SaveAsync(new ProviderCatalogRecord
        {
            Id = ProviderCatalogRecord.IdFor("email-work-items"),
            ProviderType = "email-work-items",
            Category = "task-source",
            DescriptorOverridesJson = JsonSerializer.Serialize(
                new[] { new { Key = "Folder", DefaultValue = "Tickets" } })
        });

        var entry = await _sut.SetAvailabilityAsync("admin-1", "email-work-items", available: true, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(entry.Category, Is.EqualTo("task-source"));
            Assert.That(entry.Descriptors.Single(d => d.Key == "Folder").DefaultValue, Is.EqualTo("Tickets"));
        });
    }

    [Test]
    public async Task SetSettingDisabled_MarksItDisabled_KeepsFullSet_AndAudits()
    {
        await SeedProviderAsync(descriptors: EmailDescriptors);

        var entry = await _sut.SetSettingDisabledAsync("admin-1", "email-work-items", "Password", disabled: true, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(entry.Descriptors.Single(d => d.Key == "Password").Disabled, Is.True);
            Assert.That(entry.Descriptors.Select(d => d.Key),
                Does.Contain("Password"), "the full manifest set stays intact for secret handling");
        });
        var audit = (await _auditRecords.ReadAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(audit.Action, Is.EqualTo("provider-catalog.setting-changed"));
            Assert.That(audit.Outcome, Is.EqualTo("Password: disabled"));
        });
    }

    [Test]
    public async Task SetSettingDisabled_UnknownSettingKey_IsRejected()
    {
        await SeedProviderAsync(descriptors: EmailDescriptors);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.SetSettingDisabledAsync("admin-1", "email-work-items", "NoSuchKey", disabled: true, CancellationToken.None));
    }

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
}
