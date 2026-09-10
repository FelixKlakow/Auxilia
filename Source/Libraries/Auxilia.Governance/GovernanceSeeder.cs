using Auxilia.PlatformData.Entities;
using Microsoft.Extensions.Logging;

namespace Auxilia.Governance;

/// <summary>
/// First-start bootstrap: creates the initial administrator from configuration when no
/// administrator exists yet. Never overwrites existing principals.
/// </summary>
public sealed class GovernanceSeeder(
    PrincipalDirectory directory,
    GovernanceSettings settings,
    ILogger<GovernanceSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await directory.AnyAdministratorExistsAsync(ct))
            return;

        if (string.IsNullOrWhiteSpace(settings.BootstrapAdminUsername) ||
            string.IsNullOrWhiteSpace(settings.BootstrapAdminPassword))
        {
            logger.LogWarning(
                "No administrator exists and no bootstrap credentials are configured " +
                "(Governance:BootstrapAdminUsername/Password) — administration is unavailable.");
            return;
        }

        PrincipalRecord admin;
        try
        {
            admin = await directory.CreateHumanAsync(
                "Bootstrap Administrator", settings.BootstrapAdminUsername, settings.BootstrapAdminPassword, ct);
        }
        catch (PrincipalConflictException ex)
        {
            // Never hijack an existing (non-administrator) login into the bootstrap admin.
            logger.LogError(ex,
                "Bootstrap administrator '{Username}' was not created — administration is unavailable.",
                settings.BootstrapAdminUsername);
            return;
        }
        await directory.AssignRoleAsync(admin.Id, BuiltInRoles.Administrator, ct);

        logger.LogWarning(
            "Bootstrap administrator '{Username}' created — rotate the password after first login.",
            settings.BootstrapAdminUsername);
    }
}
