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
    Choice,

    /// <summary>A reference to a configured slot instance, picked from the accessible ones.</summary>
    SlotInstance
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
    IReadOnlyList<string>? Choices = null)
{
    /// <summary>For <see cref="SettingKind.SlotInstance"/>: only instances of providers in this catalog category are offered; null = any.</summary>
    public string? InstanceCategory { get; init; }

    /// <summary>
    /// Key of a dashboard-registered connect flow (e.g. an OAuth sign-in) that can fill this
    /// setting's value — editors then offer "Connect…" besides manual entry.
    /// </summary>
    public string? ConnectFlow { get; init; }
}
