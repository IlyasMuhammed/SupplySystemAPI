using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Reports.Services;

/// <summary>
/// R6 of A29 §15. Reads the sale-order deliveries from Logistics' context, and the sale order, customer
/// and warehouse behind each from Demand, Suppliers and Inventory.
/// <para>
/// A sale order is fulfilled through its deliveries, and one is <b>open</b> until it has reached the
/// customer: every status but DELIVERED, CLOSED, SHORT_CLOSED and CANCELLED. A delivery that is drafted,
/// on hold, picked, issued, in transit or only partly delivered is still work to do.
/// </para>
/// </summary>
internal sealed class FulfilmentReportService : IFulfilmentReportService
{
    /// <summary>The most deliveries one document carries.</summary>
    internal const int MaxRows = 10_000;

    /// <summary>The statuses of a delivery that has reached the customer or been given up on.</summary>
    private static readonly DeliveryStatus[] Finished =
        [DeliveryStatus.Delivered, DeliveryStatus.Closed, DeliveryStatus.ShortClosed, DeliveryStatus.Cancelled];

    private static readonly string[] FinishedCodes = [.. Finished.Select(LogisticsCode.Of)];

    /// <summary>Every open status, in the order a delivery moves through them.</summary>
    internal static readonly IReadOnlyList<string> OpenStatuses =
        [.. Enum.GetValues<DeliveryStatus>().Where(s => !Finished.Contains(s)).Select(LogisticsCode.Of)];

    private static readonly IReadOnlyList<string> Modes =
        [.. Enum.GetValues<DeliveryMode>().Select(LogisticsCode.Of)];

    private readonly LogisticsDbContext          _logistics;
    private readonly DemandDbContext             _demand;
    private readonly InventoryDbContext          _inventory;
    private readonly ISupplierNameLookupService  _names;
    private readonly IPoDocumentTemplateService  _templates;
    private readonly TimeProvider                _clock;

    public FulfilmentReportService(
        LogisticsDbContext logistics, DemandDbContext demand, InventoryDbContext inventory,
        ISupplierNameLookupService names, IPoDocumentTemplateService templates, TimeProvider? clock = null)
    {
        _logistics = logistics;
        _demand    = demand;
        _inventory = inventory;
        _names     = names;
        _templates = templates;
        _clock     = clock ?? TimeProvider.System;
    }

    public Task<FulfilmentStatusReport> GetFulfilmentStatusAsync(FulfilmentStatusFilter filter) =>
        BuildAsync(filter, forExport: false);

    public Task<FulfilmentStatusReport> GetFulfilmentStatusForExportAsync(FulfilmentStatusFilter filter) =>
        BuildAsync(filter, forExport: true);

    private async Task<FulfilmentStatusReport> BuildAsync(FulfilmentStatusFilter filter, bool forExport)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var status = Normalize(filter.Status, OpenStatuses, "an open delivery status");
        var mode   = Normalize(filter.DeliveryMode, Modes, "a delivery mode");

        // A warehouse nobody has heard of is a mistake to report, not an empty warehouse.
        string? warehouseName = null;
        if (filter.WarehouseId is { } asked)
            warehouseName = await _inventory.Warehouses.AsNoTracking().Where(w => w.Uuid == asked).Select(w => w.Name).FirstOrDefaultAsync()
                ?? throw new NotFoundException("Warehouse", asked);

        var saleOrder = LogisticsCode.Of(DeliverySourceType.SaleOrder);
        var open = _logistics.DeliveryOrders.AsNoTracking()
            .Where(d => !d.IsDelete && d.SourceType == saleOrder && !FinishedCodes.Contains(d.Status));

        if (status is not null)               open = open.Where(d => d.Status == status);
        if (filter.WarehouseId is { } from)   open = open.Where(d => d.ShipFromWarehouseUuid == from);
        if (mode is not null)                 open = open.Where(d => d.DeliveryMode == mode);

        var total = await open.CountAsync();
        if (forExport && total > MaxRows)
            throw new BadRequestException(
                $"{total} deliveries are open, more than the {MaxRows} one document carries. Filter by status, warehouse or delivery mode.");

        var (page, pageSize) = forExport
            ? (1, Math.Max(1, total))
            : PagedResults.Clamp(filter.Page, filter.PageSize);

        var byStatus = await open.GroupBy(d => d.Status)
            .Select(g => new { Key = g.Key, Count = g.Count() }).ToListAsync();
        var byWarehouse = await open.GroupBy(d => d.ShipFromWarehouseUuid)
            .Select(g => new { Key = g.Key, Count = g.Count() }).ToListAsync();
        var byMode = await open.GroupBy(d => d.DeliveryMode)
            .Select(g => new { Key = g.Key, Count = g.Count() }).ToListAsync();

        var rows = await open
            .OrderBy(d => d.CreatedDate).ThenBy(d => d.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new
            {
                d.UUID, d.DeliveryNumber, d.SaleOrderUuid, d.SourceNumber, d.Status, d.DeliveryMode, d.ShipFromWarehouseUuid,
                d.RequestedDate, d.PromisedDate, d.CreatedDate,
                LineCount = d.Lines.Count,
                Ordered   = d.Lines.Sum(l => l.QtyOrdered),
                Delivered = d.Lines.Sum(l => l.QtyDelivered)
            })
            .ToListAsync();

        // The sale order, and through it the customer; the warehouses' names.
        var orderIds = rows.Where(r => r.SaleOrderUuid is not null).Select(r => r.SaleOrderUuid!.Value).Distinct().ToList();
        var orders   = new Dictionary<Guid, (string Number, Guid PartnerId)>();
        if (orderIds.Count > 0)
            foreach (var o in await _demand.SaleOrders.AsNoTracking().Where(o => orderIds.Contains(o.UUID))
                                           .Select(o => new { o.UUID, o.SoNumber, o.PartnerId }).ToListAsync())
                orders[o.UUID] = (o.SoNumber, o.PartnerId);

        var partnerIds = orders.Values.Select(o => o.PartnerId).Distinct().ToList();
        var customers  = partnerIds.Count == 0
            ? new Dictionary<Guid, string>()
            : new Dictionary<Guid, string>(await _names.GetNamesAsync(partnerIds));

        var warehouseIds = rows.Select(r => r.ShipFromWarehouseUuid).Concat(byWarehouse.Select(w => w.Key))
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var warehouses = new Dictionary<Guid, string>();
        if (warehouseIds.Count > 0)
            foreach (var w in await _inventory.Warehouses.AsNoTracking().Where(w => warehouseIds.Contains(w.Uuid))
                                              .Select(w => new { w.Uuid, w.Name }).ToListAsync())
                warehouses[w.Uuid] = w.Name;

        var generatedAt = _clock.GetUtcNow().UtcDateTime;
        var today       = generatedAt.Date;
        var statusOrder = OpenStatuses.ToList();
        var modeOrder   = Modes.ToList();

        return new FulfilmentStatusReport
        {
            CompanyName = await CompanyNameAsync(),
            GeneratedAt = generatedAt,
            Criteria    = new FulfilmentStatusCriteria
            {
                Status = status, WarehouseUuid = filter.WarehouseId, WarehouseName = warehouseName, DeliveryMode = mode
            },
            ByStatus = [.. byStatus
                .OrderBy(s => statusOrder.IndexOf(s.Key))
                .Select(s => new FulfilmentStatusCount { Status = s.Key, Count = s.Count })],
            ByWarehouse = [.. byWarehouse
                .Select(w => new FulfilmentWarehouseCount
                {
                    WarehouseUuid = w.Key,
                    WarehouseName = w.Key is { } id ? warehouses.GetValueOrDefault(id) : null,
                    Count         = w.Count
                })
                .OrderByDescending(w => w.Count)
                .ThenBy(w => w.WarehouseUuid is null)
                .ThenBy(w => w.WarehouseName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(w => w.WarehouseUuid)],
            ByDeliveryMode = [.. byMode
                .OrderBy(m => m.Key is null ? int.MaxValue : modeOrder.IndexOf(m.Key))
                .Select(m => new FulfilmentModeCount { DeliveryMode = m.Key ?? string.Empty, Count = m.Count })],
            Items = [.. rows.Select(r =>
            {
                var order = r.SaleOrderUuid is { } id && orders.TryGetValue(id, out var found) ? found : default;

                return new FulfilmentStatusItem
                {
                    DeliveryUuid      = r.UUID,
                    DeliveryNumber    = r.DeliveryNumber,
                    SaleOrderUuid     = r.SaleOrderUuid,
                    SaleOrderNumber   = order.Number ?? r.SourceNumber,
                    PartnerId         = order.Number is null ? null : order.PartnerId,
                    CustomerName      = order.Number is null ? null : customers.GetValueOrDefault(order.PartnerId),
                    Status            = r.Status,
                    DeliveryMode      = r.DeliveryMode,
                    WarehouseUuid     = r.ShipFromWarehouseUuid,
                    WarehouseName     = r.ShipFromWarehouseUuid is { } wid ? warehouses.GetValueOrDefault(wid) : null,
                    RequestedDate     = r.RequestedDate,
                    PromisedDate      = r.PromisedDate,
                    CreatedDate       = r.CreatedDate,
                    DaysOpen          = Math.Max(0, (today - r.CreatedDate.Date).Days),
                    LineCount         = r.LineCount,
                    QuantityOrdered   = r.Ordered,
                    QuantityDelivered = r.Delivered
                };
            })],
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>A filter word in the deliveries' own upper case, or a refusal that lists what is valid.</summary>
    private static string? Normalize(string? value, IReadOnlyList<string> valid, string what)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized)) return null;

        return valid.Contains(normalized)
            ? normalized
            : throw new BadRequestException($"'{value}' is not {what}. Valid: {string.Join(", ", valid)}.");
    }

    private async Task<string?> CompanyNameAsync()
    {
        var template = await _templates.GetActiveAsync();
        return string.IsNullOrWhiteSpace(template?.CompanyName) ? null : template.CompanyName.Trim();
    }
}
