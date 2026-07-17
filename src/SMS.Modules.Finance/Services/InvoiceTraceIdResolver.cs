using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

internal sealed class InvoiceTraceIdResolver : ITraceIdResolver
{
    public string InterfaceCode => "INVOICE";

    private readonly FinanceDbContext _db;
    public InvoiceTraceIdResolver(FinanceDbContext db) => _db = db;

    public async Task<Guid?> ResolveTraceIdAsync(Guid documentId) =>
        await _db.Invoices
            .Where(i => i.UUID == documentId)
            .Select(i => (Guid?)i.TraceId)
            .FirstOrDefaultAsync();
}
