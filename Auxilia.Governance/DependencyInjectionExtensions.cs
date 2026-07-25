using Auxilia.Governance.Identity;
using Auxilia.Governance.IdentityImport;
using Auxilia.Governance.Policy;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Governance;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Registers governance services and their entity storage. Requires
    /// <see cref="Auxilia.PlatformData.DependencyInjectionExtensions.AddSettingsProtection"/>,
    /// an <see cref="AuditLog"/>, and a <see cref="TimeProvider"/> to be registered by the host.
    /// </summary>
    public static IServiceCollection AddGovernance(
        this IServiceCollection services, PlatformDataSettings dataSettings, GovernanceSettings settings)
    {
        services.AddSingleton(settings);
        services.AddPlatformEntity<PrincipalRecord>(dataSettings);
        services.AddPlatformEntity<RoleAssignmentRecord>(dataSettings);
        services.AddPlatformEntity<GroupMappingRecord>(dataSettings);
        services.AddPlatformEntity<WorkflowTypeAccessRecord>(dataSettings);
        services.AddPlatformEntity<CredentialRecord>(dataSettings);
        services.AddPlatformEntity<IdentitySourceRecord>(dataSettings);
        services.AddPlatformEntity<GroupRecord>(dataSettings);
        services.AddPlatformEntity<GroupMembershipRecord>(dataSettings);
        services.AddPlatformEntity<GroupRoleRecord>(dataSettings);

        services.AddSingleton<IIdentityImportConnector, LdapIdentityImportConnector>();
        services.AddSingleton<IIdentityImportConnector, CsvIdentityImportConnector>();
        services.AddSingleton<IdentityImportService>();
        services.AddSingleton<PrincipalDirectory>();
        services.AddSingleton<WorkflowTypeAccessStore>();
        services.AddSingleton<GroupMappingResolver>();
        services.AddSingleton<GroupRoleResolver>();
        services.AddSingleton<GroupDirectory>();
        services.AddSingleton<IIdentityProvider, LocalIdentityProvider>();
        services.AddSingleton<ExternalIdentityProvisioner>();
        services.AddSingleton<IPolicyEngine, PolicyEngine>();
        services.AddSingleton<GovernanceSeeder>();
        return services;
    }
}
