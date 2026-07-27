using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Suppliers.Services;

internal sealed class SupplierContactLookupService : ISupplierContactLookupService
{
    private readonly SuppliersDbContext _db;

    public SupplierContactLookupService(SuppliersDbContext db) => _db = db;

    public async Task<SupplierContactInfo?> GetContactInfoAsync(Guid supplierId)
    {
        var supplier = await _db.Suppliers
            .FirstOrDefaultAsync(s => s.UUID == supplierId && !s.IsDelete);

        if (supplier is null) return null;

        var addressParts = new[]
        {
            supplier.AddressLine1,
            supplier.AddressLine2,
            supplier.City,
            supplier.ProvinceState,
            supplier.PostalCode,
            supplier.Country
        }.Where(p => !string.IsNullOrWhiteSpace(p));

        return new SupplierContactInfo(
            Phone: supplier.Phone ?? supplier.PrimaryContactPhone,
            Email: supplier.Email ?? supplier.PrimaryContactEmail,
            Address: addressParts.Any() ? string.Join(", ", addressParts) : null
        );
    }
}
