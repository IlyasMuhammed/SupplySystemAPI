using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Services;
using SMS.Modules.Suppliers.Data;

namespace SMS.Modules.Suppliers.Services;

/// <summary>
/// A38 (FIN) — a customer's payment terms for Finance's POS credit sales (<see cref="ICustomerPaymentTermsLookup"/>):
/// BusinessPartner.PaymentTermsDays of the given organization's partner; null when the partner is not that organization's.
/// </summary>
internal sealed class CustomerPaymentTermsLookup : ICustomerPaymentTermsLookup
{
    private readonly SuppliersDbContext _db;

    public CustomerPaymentTermsLookup(SuppliersDbContext db) => _db = db;

    public async Task<int?> GetPaymentTermsDaysAsync(Guid organizationId, Guid partnerId, CancellationToken ct = default) =>
        await _db.BusinessPartners.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && p.UUID == partnerId)
            .Select(p => (int?)p.PaymentTermsDays)
            .FirstOrDefaultAsync(ct);
}
