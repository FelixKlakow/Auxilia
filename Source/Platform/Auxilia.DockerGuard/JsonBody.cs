using System.Text.Json;

namespace Auxilia.DockerGuard;

/// <summary>
/// Reads request bodies the way the daemon decodes them. The daemon matches JSON field names
/// case-insensitively (with Unicode case folding), so a guard reading names case-sensitively
/// could be walked around (<c>"privileged": true</c>). A body is therefore refused unless every
/// property name is ASCII and no two names of one object differ only by case — after that a
/// case-insensitive lookup sees exactly the field the daemon will see.
/// </summary>
internal static class JsonBody
{
    /// <summary>Why <paramref name="root"/> is ambiguous to read, or null when it is not.</summary>
    public static string? Ambiguity(JsonElement root)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Name.Any(c => c > 0x7F))
                        return $"property name '{property.Name}' is not ASCII";
                    if (!seen.Add(property.Name))
                        return $"property '{property.Name}' appears more than once";
                    if (Ambiguity(property.Value) is { } nested)
                        return nested;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in root.EnumerateArray())
                    if (Ambiguity(item) is { } nested)
                        return nested;
                return null;
            default:
                return null;
        }
    }

    public static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    /// <summary>Whether the property is present with a value other than null.</summary>
    public static bool IsSet(JsonElement element, string name)
        => TryGet(element, name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public static bool IsTrue(JsonElement element, string name)
        => TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.True;

    public static string? String(JsonElement element, string name)
        => TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static IEnumerable<string> Strings(JsonElement element, string name)
        => TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)
            : [];

    /// <summary>
    /// A Docker string-slice field (<c>CapAdd</c>, <c>CapDrop</c>, <c>Cmd</c>, <c>Entrypoint</c>)
    /// as the daemon reads it: a single string is a one-element list. Absent or null is empty;
    /// anything else (a list with a non-string) is null — unreadable, so refused.
    /// </summary>
    public static IReadOnlyList<string>? StringSlice(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value) || value.ValueKind is JsonValueKind.Null)
            return [];
        return value.ValueKind switch
        {
            JsonValueKind.String => [value.GetString()!],
            JsonValueKind.Array when value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String)
                => value.EnumerateArray().Select(v => v.GetString()!).ToList(),
            _ => null,
        };
    }

    /// <summary>Whether the property holds a non-empty array, object or string.</summary>
    public static bool IsNonEmpty(JsonElement element, string name)
        => TryGet(element, name, out var value) && value.ValueKind switch
        {
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().Any(),
            JsonValueKind.String => value.GetString()!.Length > 0,
            JsonValueKind.True => true,
            _ => false,
        };

    /// <summary>A string-valued object (labels) as a dictionary.</summary>
    public static IReadOnlyDictionary<string, string>? StringMap(JsonElement element, string name)
        => TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
            : null;
}
