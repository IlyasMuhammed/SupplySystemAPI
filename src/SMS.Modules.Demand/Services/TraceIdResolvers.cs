using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

internal sealed class PrTraceIdResolver : ITraceIdResolver
{
    public string InterfaceCode => "PR";

    private readonly DemandDbContext _db;
    public PrTraceIdResolver(DemandDbContext db) => _db = db;

    public async Task<Guid?> ResolveTraceIdAsync(Guid documentId) =>
        await _db.PurchaseRequisitions
            .Where(p => p.UUID == documentId)
            .Select(p => (Guid?)p.TraceId)
            .FirstOrDefaultAsync();
}

internal sealed class QuotationTraceIdResolver : ITraceIdResolver
{
    public string InterfaceCode => "QUOTATION";

    private readonly DemandDbContext _db;
    public QuotationTraceIdResolver(DemandDbContext db) => _db = db;

    public async Task<Guid?> ResolveTraceIdAsync(Guid documentId) =>
        await _db.Quotations
            .Where(q => q.UUID == documentId)
            .Select(q => (Guid?)q.TraceId)
            .FirstOrDefaultAsync();
}

internal sealed class PoTraceIdResolver : ITraceIdResolver
{
    public string InterfaceCode => "PO";

    private readonly DemandDbContext _db;
    public PoTraceIdResolver(DemandDbContext db) => _db = db;

    public async Task<Guid?> ResolveTraceIdAsync(Guid documentId) =>
        await _db.PurchaseOrders
            .Where(p => p.UUID == documentId)
            .Select(p => (Guid?)p.TraceId)
            .FirstOrDefaultAsync();
}

// A29-P3-06 — completes SaleOrder.TraceId's use of the existing DocumentTimeline resolver pattern,
// the same as its three siblings above.
internal sealed class SoTraceIdResolver : ITraceIdResolver
{
    public string InterfaceCode => "SO";

    private readonly DemandDbContext _db;
    public SoTraceIdResolver(DemandDbContext db) => _db = db;

    public async Task<Guid?> ResolveTraceIdAsync(Guid documentId) =>
        await _db.SaleOrders
            .Where(s => s.UUID == documentId)
            .Select(s => (Guid?)s.TraceId)
            .FirstOrDefaultAsync();
}
