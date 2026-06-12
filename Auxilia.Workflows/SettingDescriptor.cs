using System.Text.Json.Serialization;

namespace Auxilia.Workflows;

/// <summary>How a provider setting is edited and rendered by configuration UIs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SettingKind
{
    Text,
    Secret,
    Number,
    Boolean,
    Choice
}

/// <summary>
/// Machine-readable description of one slot-provider setting, shipped in the plugin manifest
/// so configuration editors can render real forms instead of raw JSON.
/// </summary>
public sealed record SettingDescriptor(
    string Key,
    string Label,
    SettingKind Kind,
    bool Required = false,
    string? HelpText = null,
    string? DefaultValue = null,
    IReadOnlyList<string>? Choices = null);
