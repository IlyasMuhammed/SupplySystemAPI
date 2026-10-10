using Microsoft.EntityFrameworkCore;
using SMS.Modules.Material.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Material.Services;

// A30-P5-07 — completes the manufacturing chain's use of the existing DocumentTimeline resolver
// pattern (SMS.Modules.Demand.Services.TraceIdResolvers), one per interface code this module owns.

internal sealed class BomTraceIdResolver : ITraceIdResolver
{
    public string InterfaceCode => ManufacturingInterfaceCodes.Bom;

    private readonly MaterialDbContext _db;
    public BomTraceIdResolver(MaterialDbContext db) => _db = db;

    public async Task<Guid?> ResolveTraceIdAsync(Guid documentId) =>
        await _db.BillsOfMaterials
            .Where(b => b.UUID == documentId)
            .Select(b => (Guid?)b.TraceId)
            .FirstOrDefaultAsync();
}

internal sealed class ProdTraceIdResolver : ITraceIdResolver
{
    public string InterfaceCode => ManufacturingInterfaceCodes.ProductionOrder;

    private readonly MaterialDbContext _db;
    public ProdTraceIdResolver(MaterialDbContext db) => _db = db;

    public async Task<Guid?> ResolveTraceIdAsync(Guid documentId) =>
        await _db.ProductionOrders
            .Where(p => p.UUID == documentId)
            .Select(p => (Guid?)p.TraceId)
            .FirstOrDefaultAsync();
}

/// <summary>A36 D-14 — a service order's timeline (interface code SERVICE_ORDER, documentId = the order's UUID).</summary>
internal sealed class ServiceOrderTraceIdResolver : ITraceIdResolver
{
    public string InterfaceCode => ServiceTimelineEventTypes.InterfaceCode;

    private readonly MaterialDbContext _db;
    public ServiceOrderTraceIdResolver(MaterialDbContext db) => _db = db;

    public async Task<Guid?> ResolveTraceIdAsync(Guid documentId) =>
        await _db.ServiceOrders
            .Where(s => s.UUID == documentId)
            .Select(s => (Guid?)s.TraceId)
            .FirstOrDefaultAsync();
}

internal sealed class SrTraceIdResolver : ITraceIdResolver
{
    public string InterfaceCode => ManufacturingInterfaceCodes.SupplyRequirement;

    private readonly MaterialDbContext _db;
    public SrTraceIdResolver(MaterialDbContext db) => _db = db;

    public async Task<Guid?> ResolveTraceIdAsync(Guid documentId) =>
        await _db.SupplyRequirements
            .Where(s => s.UUID == documentId)
            .Select(s => (Guid?)s.TraceId)
            .FirstOrDefaultAsync();
}
