namespace Auxilia.SteeringInstance.Workflows.Storage;

/// <summary>
/// POCO used to seed <see cref="SlotConfigurationStore"/> from <c>appsettings.json</c>
/// at startup.  Maps directly to the <c>SlotConfigurations</c> config section.
/// </summary>
public sealed class SlotConfigurationsSettings
{
    /// <summary>
    /// Key = workflow type name; Value = ordered list of slot configuration entries.
    /// </summary>
    public Dictionary<string, List<SlotConfigurationEntry>> Workflows { get; set; } = new();
}

/// <summary>One slot configuration entry as it appears in configuration.</summary>
public sealed class SlotConfigurationEntry
{
    public string SlotName { get; set; } = string.Empty;
    public string ProviderType { get; set; } = string.Empty;
    public Dictionary<string, string> Settings { get; set; } = new();
}

