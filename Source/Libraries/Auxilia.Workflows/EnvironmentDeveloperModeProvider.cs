namespace Auxilia.Workflows;

public sealed class EnvironmentDeveloperModeProvider : IDeveloperModeProvider
{
    public bool IsActive { get; }

    public EnvironmentDeveloperModeProvider()
    {
        var value = global::System.Environment.GetEnvironmentVariable("AUXILIA_DEVELOPER_MODE") ?? string.Empty;
        IsActive = value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
