namespace SMS.Shared.Common;

/// <summary>
/// A32 — something a module must set up for every new organization (e.g. Demand's ten seeded rejection reasons).
/// Tenancy calls every registered handler right after an organization is created and committed. A handler must be
/// idempotent and stamp the organization it is given, never the caller's tenant (a super admin creates the org).
/// A handler that fails does not undo the organization: each module also backfills every organization at startup
/// (see <see cref="IOrganizationDirectory"/>), which is what makes a missed call harmless.
/// </summary>
public interface IOrganizationProvisionedHandler
{
    Task OnOrganizationProvisionedAsync(Guid organizationId, CancellationToken ct = default);
}

/// <summary>
/// A32 — every organization's id, for a module's startup backfill of per-organization data. Implemented in
/// Tenancy (which owns the organizations) and resolved through DI so callers need no reference to it.
/// </summary>
public interface IOrganizationDirectory
{
    /// <summary>All organizations, active or not (an inactive one can be reactivated and must find its data in place).</summary>
    Task<IReadOnlyList<Guid>> GetOrganizationIdsAsync(CancellationToken ct = default);
}
