using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Suppliers.Services;

/// <summary>
/// A38 — the customer master as the POS module reads it (<see cref="IPosCustomerSource"/>): business partners with
/// IsCustomer. The organization is always explicit and the tenant filter is ignored (it is off for super admins and in
/// jobs); another organization's customer is absent. Same conventions as the A37 customer sync (CustomerService.SyncAsync):
/// changes strictly after <c>since</c>, oldest first, deactivated and deleted customers included as inactive so an
/// offline copy drops them, and a page never ends inside a run of equal ModifiedAt values. Balances are not here — they
/// are Finance's (ICustomerBalanceLookup).
/// </summary>
internal sealed class PosCustomerSource : IPosCustomerSource
{
    private const int MaxLimit = 1000;

    private readonly SuppliersDbContext _db;
    private readonly WalkInCustomerSeeder _walkIn;

    public PosCustomerSource(SuppliersDbContext db, WalkInCustomerSeeder walkIn)
    {
        _db = db;
        _walkIn = walkIn;
    }

    private IQueryable<BusinessPartner> AllCustomers(Guid organizationId) =>
        _db.BusinessPartners.IgnoreQueryFilters().AsNoTracking().Where(p => p.OrganizationId == organizationId && p.IsCustomer);

    public async Task<IReadOnlyList<PosCustomer>> GetChangedAsync(Guid organizationId, DateTime? since, int limit, CancellationToken ct = default)
    {
        await _walkIn.EnsureAsync(organizationId, ct);
        limit = Math.Clamp(limit, 1, MaxLimit);
        var query = AllCustomers(organizationId);
        if (since is { } s)
        {
            var utc = s.Kind == DateTimeKind.Local ? s.ToUniversalTime() : DateTime.SpecifyKind(s, DateTimeKind.Utc);
            query = query.Where(p => p.ModifiedAt > utc);
        }

        var rows = await query.OrderBy(p => p.ModifiedAt).ThenBy(p => p.Id).Take(limit + 1).ToListAsync(ct);
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            rows.AddRange(await query.Where(p => p.ModifiedAt == last.ModifiedAt && p.Id > last.Id).OrderBy(p => p.Id).ToListAsync(ct));
        }
        return rows.Select(Map).ToList();
    }

    /// <summary>The customer (inactive included, so POS can say why it refuses); null when deleted, not a customer or another org's.</summary>
    public async Task<PosCustomer?> GetAsync(Guid organizationId, Guid customerUuid, CancellationToken ct = default)
    {
        var p = await AllCustomers(organizationId).FirstOrDefaultAsync(x => x.UUID == customerUuid && !x.IsDelete, ct);
        return p is null ? null : Map(p);
    }

    public async Task<IReadOnlyList<PosCustomer>> GetManyAsync(Guid organizationId, IReadOnlyCollection<Guid> customerUuids, CancellationToken ct = default)
    {
        if (customerUuids.Count == 0) return [];
        var ids = customerUuids.Distinct().ToList();
        var rows = await AllCustomers(organizationId).Where(p => ids.Contains(p.UUID) && !p.IsDelete).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<PosCustomer> GetWalkInAsync(Guid organizationId, CancellationToken ct = default)
    {
        await _walkIn.EnsureAsync(organizationId, ct);
        // The system row; failing that, the partner already coded C-WALKIN that the A37 seeder deliberately left in place.
        var p = await AllCustomers(organizationId).Where(x => !x.IsDelete)
                    .Where(x => x.IsSystem && x.CustomerType == CustomerTypes.WalkIn).OrderBy(x => x.Id).FirstOrDefaultAsync(ct)
                ?? await AllCustomers(organizationId).Where(x => !x.IsDelete && x.SupplierCode == CustomerTypes.WalkInCode)
                    .OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        return p is null
            ? throw new BadRequestException("This organization has no walk-in customer.")
            : Map(p);
    }

    private static PosCustomer Map(BusinessPartner p) => new(
        p.UUID,
        p.SupplierCode,
        p.SupplierName,
        p.CustomerType ?? CustomerTypes.Company,
        p.Phone,
        p.Mobile,
        p.Email,
        p.CreditLimit ?? 0m,
        p.IsActive && !p.IsDelete,
        p.IsSystem,
        DateTime.SpecifyKind(p.ModifiedAt, DateTimeKind.Utc));
}
