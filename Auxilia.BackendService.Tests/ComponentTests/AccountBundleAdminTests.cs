using Auxilia.Governance;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
/// Account-bundle administration through the real DI container: secrets are write-only,
/// encrypted at rest via the configured protector, and read back masked on the admin page.
/// </summary>
[TestFixture]
[Category("Component")]
public class AccountBundleAdminTests : DashboardComponentTestBase
{
    private const string SecretValue = "super-secret-pat-value";

    private AccountBundleStore Bundles => Factory.Services.GetRequiredService<AccountBundleStore>();

    [Test]
    public async Task CreateAndUpdate_SecretsAreProtectedAtRest_AndReadBackMasked()
    {
        var created = await Bundles.CreateAsync("test-admin", "masked-bundle", "SourceControl",
            Guid.NewGuid(), new Dictionary<string, string> { ["pat"] = SecretValue });

        // At rest: AES-GCM protected, plaintext never stored.
        var record = (await Factory.Services.GetRequiredService<IDataAccess<AccountBundleRecord>>()
            .ReadAsync(created.Id))!;
        Assert.Multiple(() =>
        {
            Assert.That(record.ProtectedSecretsJson, Does.StartWith("enc1:"));
            Assert.That(record.ProtectedSecretsJson, Does.Not.Contain(SecretValue));
        });

        // Update is write-only too: a new key is added, values still never come back.
        var updated = await Bundles.UpdateSecretsAsync("test-admin", created.Id,
            new Dictionary<string, string> { ["username"] = "bot-user" }, []);
        Assert.That(updated.SecretKeys, Is.EqualTo(new[] { "pat", "username" }));

        // The admin page shows key names with masked values only.
        using var client = CreateClient();
        var (cookie, _) = await LoginAsync(client, AdminUsername, AdminPassword);
        var html = await GetHtmlAsync(client, "/admin/bundles", cookie);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("masked-bundle"));
            Assert.That(html, Does.Contain("pat = •••"));
            Assert.That(html, Does.Not.Contain(SecretValue));
        });

        await Bundles.DeleteAsync("test-admin", created.Id);
    }

    [Test]
    public async Task Mutations_AreAudited()
    {
        var created = await Bundles.CreateAsync("test-admin", "audited-bundle", "Ai",
            Guid.NewGuid(), new Dictionary<string, string> { ["apiKey"] = SecretValue });
        await Bundles.UpdateSecretsAsync("test-admin", created.Id,
            new Dictionary<string, string> { ["endpoint"] = "https://example.com" }, []);
        await Bundles.DeleteAsync("test-admin", created.Id);

        var audit = (await Factory.Services.GetRequiredService<IDataAccess<AuditRecord>>().ReadAsync())
            .Where(a => a.Subject == created.Id.ToString())
            .ToList();
        Assert.Multiple(() =>
        {
            Assert.That(audit.Select(a => a.Action),
                Is.SupersetOf(new[] { "bundle.created", "bundle.updated", "bundle.deleted" }));
            Assert.That(audit.All(a => a.DetailJson?.Contains(SecretValue) != true), Is.True,
                "audit entries must never contain secret material");
        });
    }
}
