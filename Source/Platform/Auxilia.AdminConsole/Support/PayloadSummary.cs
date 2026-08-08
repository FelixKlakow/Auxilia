using System.Text.Json;

namespace Auxilia.AdminConsole.Support;

/// <summary>
/// Turns an event payload into the few scalar fields worth showing in a table cell. A truncated
/// blob of raw JSON is unreadable and eats the widest column; the first handful of key/value pairs
/// says what the event was actually about. The full payload stays in the expanded row.
/// </summary>
public static class PayloadSummary
{
    private const int MaxFields = 3;
    private const int MaxValueLength = 48;

    public static IReadOnlyList<KeyValuePair<string, string>> Fields(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return [];

        JsonElement element;
        try
        {
            element = JsonSerializer.Deserialize<JsonElement>(payloadJson);
        }
        catch (JsonException)
        {
            return [new KeyValuePair<string, string>("", Clamp(payloadJson))];
        }

        if (element.ValueKind != JsonValueKind.Object)
            return [new KeyValuePair<string, string>("", Clamp(element.ToString()))];

        return element.EnumerateObject()
            .Where(p => p.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            .Take(MaxFields)
            .Select(p => new KeyValuePair<string, string>(p.Name, Clamp(Text(p.Value))))
            .ToList();
    }

    /// <summary>Indented JSON for the expanded row; unparseable payloads pass through verbatim.</summary>
    public static string Pretty(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string Text(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();

    private static string Clamp(string value)
        => value.Length <= MaxValueLength ? value : value[..MaxValueLength] + "…";
}
