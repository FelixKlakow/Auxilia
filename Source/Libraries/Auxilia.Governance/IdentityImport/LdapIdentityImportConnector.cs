using System.DirectoryServices.Protocols;
using System.Net;

namespace Auxilia.Governance.IdentityImport;

/// <summary>
/// Imports users from an LDAP directory (OpenLDAP, Active Directory, ...) via
/// System.DirectoryServices.Protocols using paged searches. Entries map to
/// <see cref="ExternalUser"/>: the DN is the stable external ID; AD's
/// userAccountControl disable flag is honoured when present.
/// </summary>
public sealed class LdapIdentityImportConnector : IIdentityImportConnector
{
    public const string Type = "ldap";

    private const int PageSize = 500;
    private const string UserAccountControlAttribute = "userAccountControl";
    private const int AccountDisableFlag = 0x2;

    public string ConnectorType => Type;

    public string DisplayName => "LDAP / Active Directory";

    public string Description => "Connect to a directory server and import the users a search filter matches.";

    public IReadOnlyList<ConnectorSettingDescriptor> SettingDescriptors { get; } =
    [
        new("Host", "Host", ConnectorSettingKind.Text, Required: true,
            HelpText: "DNS name or IP address of the LDAP / Active Directory server."),
        new("Port", "Port", ConnectorSettingKind.Number, DefaultValue: "389",
            HelpText: "389 for plain LDAP, 636 for LDAP over SSL."),
        new("UseSsl", "Use SSL", ConnectorSettingKind.Boolean, DefaultValue: "false"),
        new("BindDn", "Bind DN", ConnectorSettingKind.Text, Required: true,
            HelpText: "Service account used for the search, e.g. cn=admin,dc=example,dc=org."),
        new("BindPassword", "Bind password", ConnectorSettingKind.Secret, Required: true),
        new("BaseDn", "Base DN", ConnectorSettingKind.Text, Required: true,
            HelpText: "Subtree to search for users, e.g. ou=people,dc=example,dc=org."),
        new("UserFilter", "User filter", ConnectorSettingKind.Text, DefaultValue: "(objectClass=person)",
            HelpText: "LDAP search filter selecting the user entries to import."),
        new("UsernameAttribute", "Username attribute", ConnectorSettingKind.Text, DefaultValue: "uid",
            HelpText: "uid on most LDAP servers; use sAMAccountName for Active Directory."),
        new("DisplayNameAttribute", "Display name attribute", ConnectorSettingKind.Text, DefaultValue: "cn"),
        new("GroupAttribute", "Group attribute", ConnectorSettingKind.Text, DefaultValue: "memberOf",
            HelpText: "User attribute holding group memberships (memberOf on Active Directory); " +
                      "leave empty to import without groups.")
    ];

    public async Task<ConnectorTestResult> TestConnectionAsync(
        IReadOnlyDictionary<string, string> settings, CancellationToken ct = default)
    {
        try
        {
            var fetch = await FetchUsersAsync(settings, ct);
            var message = $"Directory reachable — {fetch.Users.Count} user(s) match the filter" +
                          (fetch.SkippedEntries.Count > 0
                              ? $", {fetch.SkippedEntries.Count} entr(ies) skipped"
                              : "");
            return new ConnectorTestResult(true, message);
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException
                                       or ArgumentException or InvalidOperationException)
        {
            // Exception messages never contain setting values — safe to surface.
            return new ConnectorTestResult(false, $"Connection failed: {ex.Message}");
        }
    }

    public Task<IdentityImportFetch> FetchUsersAsync(
        IReadOnlyDictionary<string, string> settings, CancellationToken ct = default)
        // S.DS.P is a synchronous protocol API — run it off the caller's thread.
        => Task.Run(() => FetchUsers(LdapImportSettings.Parse(settings)), ct);

    private static IdentityImportFetch FetchUsers(LdapImportSettings settings)
    {
        using var connection = Connect(settings);

        var users = new List<ExternalUser>();
        var skipped = new List<string>();
        var pageControl = new PageResultRequestControl(PageSize);

        while (true)
        {
            var request = new SearchRequest(
                settings.BaseDn, settings.UserFilter, SearchScope.Subtree, settings.RequestedAttributes);
            request.Controls.Add(pageControl);

            var response = (SearchResponse)connection.SendRequest(request);
            foreach (SearchResultEntry entry in response.Entries)
            {
                var user = MapEntry(entry, settings);
                if (user is null)
                    skipped.Add($"{entry.DistinguishedName}: no '{settings.UsernameAttribute}' attribute");
                else
                    users.Add(user);
            }

            var pageResponse = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
            if (pageResponse is null || pageResponse.Cookie.Length == 0)
                break;
            pageControl.Cookie = pageResponse.Cookie;
        }

        return new IdentityImportFetch(users, skipped);
    }

    private static LdapConnection Connect(LdapImportSettings settings)
    {
        var connection = new LdapConnection(
            new LdapDirectoryIdentifier(settings.Host, settings.Port),
            new NetworkCredential(settings.BindDn, settings.BindPassword),
            AuthType.Basic);
        try
        {
            connection.SessionOptions.ProtocolVersion = 3;
            if (settings.UseSsl)
                connection.SessionOptions.SecureSocketLayer = true;
            connection.Timeout = TimeSpan.FromSeconds(30);
            connection.AutoBind = false;
            connection.Bind();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static ExternalUser? MapEntry(SearchResultEntry entry, LdapImportSettings settings)
    {
        var username = FirstValue(entry, settings.UsernameAttribute);
        if (username is null)
            return null;

        var displayName = FirstValue(entry, settings.DisplayNameAttribute) ?? username;

        var enabled = true;
        if (int.TryParse(FirstValue(entry, UserAccountControlAttribute), out var accountControl))
            enabled = (accountControl & AccountDisableFlag) == 0;

        var groups = settings.GroupAttribute.Length == 0
            ? []
            : AllValues(entry, settings.GroupAttribute).Select(GroupNameOf).ToList();

        return new ExternalUser(entry.DistinguishedName, displayName, username, enabled, groups);
    }

    /// <summary>"cn=Reviewers,ou=groups,dc=example,dc=org" → "Reviewers"; plain values stay as-is.</summary>
    internal static string GroupNameOf(string value)
    {
        var firstComponent = value.Split(',')[0];
        var separator = firstComponent.IndexOf('=');
        return separator < 0 ? value.Trim() : firstComponent[(separator + 1)..].Trim();
    }

    private static string? FirstValue(SearchResultEntry entry, string attribute)
    {
        var values = entry.Attributes[attribute];
        return values is { Count: > 0 } ? values[0]?.ToString() : null;
    }

    private static IEnumerable<string> AllValues(SearchResultEntry entry, string attribute)
    {
        var values = entry.Attributes[attribute];
        if (values is null)
            yield break;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i]?.ToString() is { Length: > 0 } value)
                yield return value;
        }
    }
}

/// <summary>Validated LDAP settings with the connector's documented defaults applied.</summary>
internal sealed record LdapImportSettings(
    string Host,
    int Port,
    bool UseSsl,
    string BindDn,
    string BindPassword,
    string BaseDn,
    string UserFilter,
    string UsernameAttribute,
    string DisplayNameAttribute,
    string GroupAttribute)
{
    public string[] RequestedAttributes =>
        GroupAttribute.Length == 0
            ? [UsernameAttribute, DisplayNameAttribute, "userAccountControl"]
            : [UsernameAttribute, DisplayNameAttribute, "userAccountControl", GroupAttribute];

    public static LdapImportSettings Parse(IReadOnlyDictionary<string, string> settings)
    {
        var host = Required(settings, "Host");
        var bindDn = Required(settings, "BindDn");
        var bindPassword = Required(settings, "BindPassword");
        var baseDn = Required(settings, "BaseDn");

        var portText = ValueOrDefault(settings, "Port", "389");
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            throw new ArgumentException($"Setting 'Port' must be a port number, got '{portText}'.");

        return new LdapImportSettings(
            host,
            port,
            string.Equals(ValueOrDefault(settings, "UseSsl", "false"), "true", StringComparison.OrdinalIgnoreCase),
            bindDn,
            bindPassword,
            baseDn,
            ValueOrDefault(settings, "UserFilter", "(objectClass=person)"),
            ValueOrDefault(settings, "UsernameAttribute", "uid"),
            ValueOrDefault(settings, "DisplayNameAttribute", "cn"),
            settings.GetValueOrDefault("GroupAttribute", "memberOf").Trim());
    }

    private static string Required(IReadOnlyDictionary<string, string> settings, string key)
        => settings.GetValueOrDefault(key)?.Trim() is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"Setting '{key}' is required.");

    private static string ValueOrDefault(IReadOnlyDictionary<string, string> settings, string key, string fallback)
        => settings.GetValueOrDefault(key)?.Trim() is { Length: > 0 } value ? value : fallback;
}
