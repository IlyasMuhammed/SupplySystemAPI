using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

internal sealed class SupplierPaymentTraceIdResolver : ITraceIdResolver
{
    public string InterfaceCode => "SUPPLIER_PAYMENT";

    private readonly FinanceDbContext _db;
    public SupplierPaymentTraceIdResolver(FinanceDbContext db) => _db = db;

    public async Task<Guid?> ResolveTraceIdAsync(Guid documentId) =>
        await _db.SupplierPayments
            .Where(p => p.UUID == documentId)
            .Select(p => (Guid?)p.TraceId)
            .FirstOrDefaultAsync();
}
