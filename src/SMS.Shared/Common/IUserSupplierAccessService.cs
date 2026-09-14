namespace SMS.Shared.Common;

// Resolves which suppliers the current request's user may see (REQ-2.x). Deliberately NOT baked
// into the JWT like permissions are — a user's mapped-supplier set can be arbitrarily large and is
// edited far more often than a role's permission catalog, so it's resolved fresh per request from
// the DB instead of risking staleness until the next token refresh (see TokenService/ITenantContext
// for the contrasting "small, stable, claim-baked" case this deliberately avoids).
//
// Only ever restrictive for a SupplierType="EXTERNAL" UserAccount that has at least one mapping —
// internal users, super admins, and external users with zero mappings all see everything
// (fail-open: the restriction only activates once an admin explicitly assigns a supplier).
public interface IUserSupplierAccessService
{
    Task<bool> IsRestrictedAsync();

    // Only meaningful when IsRestrictedAsync() is true.
    Task<IReadOnlySet<Guid>> GetAllowedSupplierIdsAsync();

    Task<bool> CanAccessSupplierAsync(Guid supplierUuid);
}
