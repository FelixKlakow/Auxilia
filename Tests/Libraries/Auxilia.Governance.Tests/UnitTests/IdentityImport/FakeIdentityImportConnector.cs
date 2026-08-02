using Auxilia.Governance.IdentityImport;

namespace Auxilia.Governance.Tests.UnitTests.IdentityImport;

/// <summary>Scriptable connector for import-semantics tests; records the settings it was called with.</summary>
internal sealed class FakeIdentityImportConnector : IIdentityImportConnector
{
    public const string Type = "fake";

    public string ConnectorType => Type;

    public string DisplayName => "Fake connector";

    public string Description => "Test double";

    public IReadOnlyList<ConnectorSettingDescriptor> SettingDescriptors { get; } =
    [
        new("Endpoint", "Endpoint", ConnectorSettingKind.Text, Required: true),
        new("Token", "Token", ConnectorSettingKind.Secret, Required: true),
        new("PageSize", "Page size", ConnectorSettingKind.Number, DefaultValue: "100")
    ];

    public List<ExternalUser> Users { get; } = [];

    public List<string> SkippedEntries { get; } = [];

    public IReadOnlyDictionary<string, string>? LastSettings { get; private set; }

    public Task<ConnectorTestResult> TestConnectionAsync(
        IReadOnlyDictionary<string, string> settings, CancellationToken ct = default)
    {
        LastSettings = settings;
        return Task.FromResult(new ConnectorTestResult(true, $"{Users.Count} user(s)"));
    }

    public Task<IdentityImportFetch> FetchUsersAsync(
        IReadOnlyDictionary<string, string> settings, CancellationToken ct = default)
    {
        LastSettings = settings;
        return Task.FromResult(new IdentityImportFetch(Users.ToList(), SkippedEntries.ToList()));
    }
}
