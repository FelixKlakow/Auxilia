using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.PlatformData.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class DependencyInjectionExtensionsTests
{
    [Test]
    public void AddPlatformEntity_InMemory_ResolvesDataAccess()
    {
        var services = new ServiceCollection();
        services.AddPlatformEntity<SlotProviderRecord>(new PlatformDataSettings
        {
            Backend = PlatformDataBackend.InMemory
        });

        using var provider = services.BuildServiceProvider();
        Assert.That(provider.GetService<IDataAccess<SlotProviderRecord>>(), Is.Not.Null);
    }

    [Test]
    public async Task AddPlatformEntity_Json_PersistsAcrossContainers()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"auxilia-pd-{Guid.NewGuid():N}");
        try
        {
            var settings = new PlatformDataSettings { Backend = PlatformDataBackend.Json, JsonDirectory = dir };
            var record = new SlotProviderRecord
            {
                Id = SlotProviderRecord.IdFor("prov"),
                ProviderType = "prov",
                DllPath = "/plugins/prov.slothandler.dll"
            };

            // First container writes…
            {
                var services = new ServiceCollection().AddPlatformEntity<SlotProviderRecord>(settings);
                await using var provider = services.BuildServiceProvider();
                await provider.GetRequiredService<IDataAccess<SlotProviderRecord>>().SaveAsync(record);
            }

            // …a fresh container (fresh process equivalent) reads it back.
            {
                var services = new ServiceCollection().AddPlatformEntity<SlotProviderRecord>(settings);
                await using var provider = services.BuildServiceProvider();
                var loaded = await provider.GetRequiredService<IDataAccess<SlotProviderRecord>>()
                    .ReadAsync(record.Id);
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded!.DllPath, Is.EqualTo("/plugins/prov.slothandler.dll"));
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void AddSettingsProtection_WithoutKey_RegistersNullProtector()
    {
        var services = new ServiceCollection().AddSettingsProtection(new PlatformDataSettings());
        using var provider = services.BuildServiceProvider();
        Assert.That(provider.GetRequiredService<ISettingsProtector>(), Is.InstanceOf<NullSettingsProtector>());
    }

    [Test]
    public void AddSettingsProtection_WithKey_RegistersAesGcmProtector()
    {
        var settings = new PlatformDataSettings
        {
            ProtectionKeyBase64 = Convert.ToBase64String(new byte[32])
        };
        var services = new ServiceCollection().AddSettingsProtection(settings);
        using var provider = services.BuildServiceProvider();
        Assert.That(provider.GetRequiredService<ISettingsProtector>(), Is.InstanceOf<AesGcmSettingsProtector>());
    }
}
