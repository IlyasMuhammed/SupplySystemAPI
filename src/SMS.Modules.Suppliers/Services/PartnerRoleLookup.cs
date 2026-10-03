using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Suppliers.Services;

/// <summary>
/// A32 — lets Demand check that a sale inquiry's or quotation's partner is a customer (BR-C1-01/BR-C2-01)
/// without a project reference to Suppliers. Filtered explicitly on the caller's own organization: the tenant
/// query filter is bypassed for super admins, and another organization's partner must read as absent.
/// A soft-deleted partner is absent too; a deactivated one is returned with IsActive = false, for the caller to refuse.
/// </summary>
internal sealed class PartnerRoleLookup : IPartnerRoleLookup
{
    private readonly SuppliersDbContext _db;
    private readonly ITenantContext _tenant;

    public PartnerRoleLookup(SuppliersDbContext db, ITenantContext tenant)
    {
        _db     = db;
        _tenant = tenant;
    }

    public async Task<PartnerRoleInfo?> GetAsync(Guid partnerUuid, CancellationToken ct = default)
    {
        if (partnerUuid == Guid.Empty) return null;

        var orgId = _tenant.OrganizationId;
        return await _db.BusinessPartners.AsNoTracking()
            .Where(p => p.UUID == partnerUuid && p.OrganizationId == orgId && !p.IsDelete)
            .Select(p => new PartnerRoleInfo(p.UUID, p.SupplierName, p.IsCustomer, p.IsVendor, p.IsActive))
            .FirstOrDefaultAsync(ct);
    }
}
