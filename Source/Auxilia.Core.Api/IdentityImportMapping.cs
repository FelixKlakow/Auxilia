using Auxilia.Core.Contracts;
using Auxilia.Governance.IdentityImport;

namespace Auxilia.Core.Api;

/// <summary>Maps between the Governance identity-import model and the Core wire contracts.</summary>
internal static class IdentityImportMapping
{
    public static IdentitySourceDraft ToDraft(this SaveIdentitySourceRequest request)
    {
        var draft = new IdentitySourceDraft
        {
            ExistingName = request.ExistingName,
            Name = request.Name,
            ConnectorType = request.ConnectorType,
            DefaultRole = request.DefaultRole,
            DisableMissing = request.DisableMissing
        };
        foreach (var (key, value) in request.Settings)
            draft.Settings[key] = value;
        foreach (var (group, role) in request.GroupRoleMappings)
            draft.GroupRoleMappings[group] = role;
        return draft;
    }

    public static IdentitySourceDto ToDto(this IdentitySourceView view)
        => new(
            view.Id, view.Name, view.ConnectorType, view.DisableMissing, view.DefaultRole,
            new Dictionary<string, string>(view.GroupRoleMappings),
            new Dictionary<string, string>(view.Settings),
            view.StoredSecretKeys.ToList(),
            view.LastImport?.ToDto(),
            view.LastImportUtc);

    public static IdentityImportSummaryDto ToDto(this IdentityImportSummary summary)
        => new(summary.Created, summary.Updated, summary.Disabled, summary.Skipped, summary.Warnings);

    public static IdentityConnectorTestResult ToDto(this ConnectorTestResult result)
        => new(result.Success, result.Message);

    public static IdentityConnectorDescriptorDto ToDescriptorDto(this IIdentityImportConnector connector)
        => new(
            connector.ConnectorType, connector.DisplayName, connector.Description,
            connector.SettingDescriptors
                .Select(d => new IdentityConnectorSettingDto(
                    d.Key, d.Label, d.Kind.ToString(), d.Required, d.HelpText, d.DefaultValue, d.Multiline))
                .ToList());
}
