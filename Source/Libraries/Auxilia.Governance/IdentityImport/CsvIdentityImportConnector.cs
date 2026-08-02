using System.Text;

namespace Auxilia.Governance.IdentityImport;

/// <summary>
/// Imports users from pasted CSV content (v1 simplicity: the whole file is one multiline
/// setting). Columns: externalId,username,displayName,enabled,groups — quoted fields are
/// supported, bad rows are skipped and reported, groups inside one field are separated by ';'.
/// </summary>
public sealed class CsvIdentityImportConnector : IIdentityImportConnector
{
    public const string Type = "csv";

    internal const string CsvSettingKey = "Csv";

    public string ConnectorType => Type;

    public string DisplayName => "CSV upload";

    public string Description => "Paste user rows exported from any system — no live connection needed.";

    public IReadOnlyList<ConnectorSettingDescriptor> SettingDescriptors { get; } =
    [
        new(CsvSettingKey, "CSV content", ConnectorSettingKind.Text, Required: true,
            HelpText: "Columns: externalId,username,displayName,enabled,groups. " +
                      "An optional header row is ignored; separate multiple groups with ';'.",
            Multiline: true)
    ];

    public Task<ConnectorTestResult> TestConnectionAsync(
        IReadOnlyDictionary<string, string> settings, CancellationToken ct = default)
    {
        var fetch = Parse(settings.GetValueOrDefault(CsvSettingKey, ""));
        var message = $"{fetch.Users.Count} user(s) parsed" +
                      (fetch.SkippedEntries.Count > 0 ? $", {fetch.SkippedEntries.Count} row(s) skipped" : "");
        return Task.FromResult(new ConnectorTestResult(fetch.Users.Count > 0, message));
    }

    public Task<IdentityImportFetch> FetchUsersAsync(
        IReadOnlyDictionary<string, string> settings, CancellationToken ct = default)
        => Task.FromResult(Parse(settings.GetValueOrDefault(CsvSettingKey, "")));

    internal static IdentityImportFetch Parse(string csv)
    {
        var users = new List<ExternalUser>();
        var skipped = new List<string>();

        var lines = csv.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            IReadOnlyList<string> fields;
            try
            {
                fields = ParseLine(line);
            }
            catch (FormatException ex)
            {
                skipped.Add($"line {index + 1}: {ex.Message}");
                continue;
            }

            // An optional header row is recognised by its first column name.
            if (index == 0 && fields.Count > 0 &&
                string.Equals(fields[0].Trim(), "externalId", StringComparison.OrdinalIgnoreCase))
                continue;

            if (fields.Count is < 2 or > 5)
            {
                skipped.Add($"line {index + 1}: expected 2-5 columns, found {fields.Count}");
                continue;
            }

            var externalId = fields[0].Trim();
            var username = fields[1].Trim();
            if (externalId.Length == 0 || username.Length == 0)
            {
                skipped.Add($"line {index + 1}: externalId and username must be non-empty");
                continue;
            }

            var displayName = fields.Count > 2 && fields[2].Trim().Length > 0 ? fields[2].Trim() : username;

            var enabled = true;
            if (fields.Count > 3 && fields[3].Trim().Length > 0)
            {
                var parsed = ParseEnabled(fields[3].Trim());
                if (parsed is null)
                {
                    skipped.Add($"line {index + 1}: unrecognised enabled value '{fields[3].Trim()}'");
                    continue;
                }
                enabled = parsed.Value;
            }

            var groups = fields.Count > 4
                ? fields[4].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];

            users.Add(new ExternalUser(externalId, displayName, username, enabled, groups));
        }

        return new IdentityImportFetch(users, skipped);
    }

    private static bool? ParseEnabled(string value) => value.ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "y" => true,
        "false" or "0" or "no" or "n" => false,
        _ => null
    };

    /// <summary>RFC-4180-style field split: quoted fields may contain commas and doubled quotes.</summary>
    internal static IReadOnlyList<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        if (inQuotes)
            throw new FormatException("unterminated quoted field");

        fields.Add(field.ToString());
        return fields;
    }
}
