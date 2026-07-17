using Microsoft.EntityFrameworkCore;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Warehouse.Services;

// Both GRN and GRN_QC interface codes resolve to the same underlying Grn row/trace_id.
internal sealed class GrnTraceIdResolver   : GrnTraceIdResolverBase { public override string InterfaceCode => "GRN"; public GrnTraceIdResolver(WarehouseDbContext db) : base(db) { } }
internal sealed class GrnQcTraceIdResolver : GrnTraceIdResolverBase { public override string InterfaceCode => "GRN_QC"; public GrnQcTraceIdResolver(WarehouseDbContext db) : base(db) { } }

internal abstract class GrnTraceIdResolverBase : ITraceIdResolver
{
    public abstract string InterfaceCode { get; }

    private readonly WarehouseDbContext _db;
    protected GrnTraceIdResolverBase(WarehouseDbContext db) => _db = db;

    public async Task<Guid?> ResolveTraceIdAsync(Guid documentId) =>
        await _db.Grns
            .Where(g => g.UUID == documentId)
            .Select(g => (Guid?)g.TraceId)
            .FirstOrDefaultAsync();
}
