namespace Auxilia.Core.Api;

/// <summary>Host configuration for the Core API (bound from the <c>CoreApi</c> section).</summary>
public sealed class CoreApiSettings
{
    /// <summary>Queue the runner pool consumes run commands from (competing consumers).</summary>
    public string RunCommandQueue { get; set; } = "workflow.run-commands";

    /// <summary>
    /// Configurations seeded at startup — the "statically configured" path. Idempotent by name:
    /// a configuration whose name already exists is left untouched.
    /// </summary>
    public List<StaticRunConfiguration> StaticConfigurations { get; set; } = new();
}

/// <summary>A run configuration provided through host settings (appsettings / environment).</summary>
public sealed class StaticRunConfiguration
{
    public string Name { get; set; } = "";
    public string WorkflowType { get; set; } = "";
    public string PackageUri { get; set; } = "";
    public Dictionary<string, string> Context { get; set; } = new();
}
