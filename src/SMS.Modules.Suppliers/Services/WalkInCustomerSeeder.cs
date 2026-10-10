using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Suppliers.Services;

/// <summary>
/// A37 CUST-01 — every organization has one walk-in customer: code C-WALKIN, ACTIVE, IsSystem, WALK_IN, credit 0.
/// Created at API start for every organization (<see cref="SuppliersModuleExtensions.MigrateSuppliersSchema"/>), when
/// Tenancy provisions a new one (<see cref="IOrganizationProvisionedHandler"/>), and lazily by the customer API.
/// Idempotent and explicit about the organization (no caller at startup; a super admin bypasses the tenant filter).
/// A partner some user already coded C-WALKIN is left alone (never taken over), and no second row is created.
/// </summary>
internal sealed class WalkInCustomerSeeder : IOrganizationProvisionedHandler
{
    /// <summary>Organizations known to have their walk-in row in this process — spares the customer API a query per call.</summary>
    private static readonly ConcurrentDictionary<Guid, bool> Seeded = new();

    private readonly SuppliersDbContext _db;
    public WalkInCustomerSeeder(SuppliersDbContext db) => _db = db;

    public Task OnOrganizationProvisionedAsync(Guid organizationId, CancellationToken ct = default) =>
        EnsureAsync(organizationId, ct);

    public async Task EnsureForAllAsync(IEnumerable<Guid> organizationIds, CancellationToken ct = default)
    {
        foreach (var organizationId in organizationIds) await EnsureAsync(organizationId, ct);
    }

    public async Task EnsureAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty || Seeded.ContainsKey(organizationId)) return;

        var exists = await _db.BusinessPartners.IgnoreQueryFilters().AnyAsync(p => p.OrganizationId == organizationId
            && ((p.IsSystem && p.CustomerType == CustomerTypes.WalkIn) || p.SupplierCode == CustomerTypes.WalkInCode), ct);
        if (!exists)
        {
            var walkIn = new BusinessPartner
            {
                UUID = Guid.NewGuid(), OrganizationId = organizationId,
                SupplierCode = CustomerTypes.WalkInCode, SupplierName = CustomerTypes.WalkInName,
                PartnerType = PartnerCode.Of(PartnerType.Customer), IsVendor = false, IsCustomer = true,
                CustomerType = CustomerTypes.WalkIn, IsSystem = true, CreditLimit = 0m, PaymentTermsDays = 0,
                Status = "ACTIVE", IsActive = true, CreatedBy = 0, CreatedDate = DateTime.UtcNow,
                Notes = "Created by the system for counter sales without a named customer."
            };
            _db.BusinessPartners.Add(walkIn);
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // Another instance seeded it first (unique OrganizationId + code): that row is the walk-in.
                _db.Entry(walkIn).State = EntityState.Detached;
            }
        }
        Seeded[organizationId] = true;
    }

    /// <summary>Tests only — forget what this process has seeded.</summary>
    internal static void ResetCache() => Seeded.Clear();
}
