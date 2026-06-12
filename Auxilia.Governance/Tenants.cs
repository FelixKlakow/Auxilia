namespace Auxilia.Governance;

/// <summary>
/// v1 is single-tenant: every record carries this tenant ID so multi-tenancy is a data
/// migration, not a schema break (ARCHITECTURE.md §16).
/// </summary>
public static class Tenants
{
    public static readonly Guid DefaultTenantId = new("d3fa017e-0000-4000-8000-000000000001");
}
