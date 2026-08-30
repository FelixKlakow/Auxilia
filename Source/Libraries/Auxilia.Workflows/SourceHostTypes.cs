namespace Auxilia.Workflows;

/// <summary>
/// Well-known source-host type strings — an OPEN vocabulary, not a closed set: providers may
/// introduce new host types without an SDK change, so consumers must tolerate unknown values
/// and compare with <see cref="StringComparison.OrdinalIgnoreCase"/>.
/// </summary>
public static class SourceHostTypes
{
    public const string GitHub = "github";
    public const string GitLab = "gitlab";
    public const string AzureDevOps = "azure-devops";
}
