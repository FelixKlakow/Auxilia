using System.Text.Json;
using Auxilia.PlatformData.Protection;

namespace Auxilia.BackendService.Dashboard;

/// <summary>
/// Exposes only the key names of a protected settings payload — values stay opaque so the
/// UI can never render decrypted slot settings.
/// </summary>
public static class ProtectedSettingsInspector
{
    public static IReadOnlyList<string> SettingKeys(ISettingsProtector protector, string protectedSettingsJson)
    {
        try
        {
            var dictionary = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                protector.Unprotect(protectedSettingsJson));
            return dictionary?.Keys.Order().ToList() ?? [];
        }
        catch
        {
            // Wrong key, foreign format, non-object payload — the viewer shows nothing
            // rather than risking exposure or crashing the page.
            return [];
        }
    }
}
