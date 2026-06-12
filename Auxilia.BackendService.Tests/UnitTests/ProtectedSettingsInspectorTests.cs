using System.Security.Cryptography;
using Auxilia.BackendService.Dashboard;
using Auxilia.PlatformData.Protection;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class ProtectedSettingsInspectorTests
{
    [Test]
    public void SettingKeys_ProtectedPayload_ReturnsSortedKeysOnly()
    {
        var protector = new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32));
        var payload = protector.Protect("""{"token":"secret-value","baseUrl":"https://example.com"}""");

        var keys = ProtectedSettingsInspector.SettingKeys(protector, payload);

        Assert.That(keys, Is.EqualTo(new[] { "baseUrl", "token" }));
    }

    [Test]
    public void SettingKeys_WrongKey_ReturnsEmptyInsteadOfThrowing()
    {
        var payload = new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32))
            .Protect("""{"token":"secret-value"}""");
        var otherProtector = new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32));

        Assert.That(ProtectedSettingsInspector.SettingKeys(otherProtector, payload), Is.Empty);
    }

    [Test]
    public void SettingKeys_NonObjectPayload_ReturnsEmpty()
    {
        var protector = new NullSettingsProtector();

        Assert.Multiple(() =>
        {
            Assert.That(ProtectedSettingsInspector.SettingKeys(protector, "not json"), Is.Empty);
            Assert.That(ProtectedSettingsInspector.SettingKeys(protector, "[1,2,3]"), Is.Empty);
        });
    }
}
