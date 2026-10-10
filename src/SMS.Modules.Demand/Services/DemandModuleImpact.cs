using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A37 D-18 — what is still in progress when MODULE_DEMAND is about to be switched off: purchase orders not closed,
/// cancelled or rejected, and sale orders not invoiced, closed or cancelled (drafts included — both are work someone
/// has started). The organization is taken explicitly (R-11).
/// </summary>
internal sealed class DemandModuleImpact : IModuleImpactProvider
{
    internal const string OpenPurchaseOrders = "Open purchase orders";
    internal const string OpenSaleOrders     = "Open sale orders";

    private static readonly string[] ClosedPurchaseOrders = ["CLOSED", "CANCELLED", "REJECTED"];

    private static readonly string[] ClosedSaleOrders =
    [
        EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Invoiced),
        EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Closed),
        EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Cancelled)
    ];

    private readonly DemandDbContext _db;
    public DemandModuleImpact(DemandDbContext db) => _db = db;

    public string ModuleCode => ModuleCodes.Demand;

    public async Task<IReadOnlyList<ModuleImpactItem>> GetInProgressAsync(Guid organizationId, CancellationToken ct = default)
    {
        var purchaseOrders = await _db.PurchaseOrders.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(p => p.OrganizationId == organizationId && !p.IsDelete && !ClosedPurchaseOrders.Contains(p.Status), ct);
        var saleOrders = await _db.SaleOrders.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(o => o.OrganizationId == organizationId && !o.IsDeleted && !ClosedSaleOrders.Contains(o.Status), ct);
        return [new(OpenPurchaseOrders, purchaseOrders), new(OpenSaleOrders, saleOrders)];
    }
}
