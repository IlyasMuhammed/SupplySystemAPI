using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Services;

/// <summary>A36 — the read models of API-CONTRACT §3 (list, detail, materials, ledger, dashboard).</summary>
internal sealed partial class ServiceOrderService
{
    private sealed record MaterialName(Guid ProductUuid, string DisplayName, string ProductName, string? VariantName, string Sku, string ProductType);

    public async Task<PaginatedResponse<ServiceOrderListItemModel>> GetListAsync(ServiceOrderListFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = _db.ServiceOrders.AsNoTracking().AsQueryable();

        var statuses = (filter.Status ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant()).ToList();
        if (statuses.Count > 0)                    query = query.Where(o => statuses.Contains(o.Status));
        if (filter.CustomerUuid is { } customer)   query = query.Where(o => o.CustomerUuid == customer);
        if (filter.AssignedUserId is { } assignee) query = query.Where(o => o.AssignedUserId == assignee);
        if (filter.FromDate is { } from)           query = query.Where(o => o.ScheduledDate >= from.Date);
        if (filter.ToDate is { } to)               query = query.Where(o => o.ScheduledDate <= to.Date);
        if (filter.Priority is { } priority)       query = query.Where(o => o.Priority == priority);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLower();
            var products = await _inv.Products.AsNoTracking()
                .Where(p => p.Name.ToLower().Contains(term) || p.Sku.ToLower().Contains(term))
                .Select(p => p.Uuid).ToListAsync(ct);
            query = query.Where(o => o.ServiceNumber.ToLower().Contains(term) ||
                                     (o.SourceReference != null && o.SourceReference.ToLower().Contains(term)) ||
                                     products.Contains(o.ServiceProductUuid));
        }

        var total    = await query.CountAsync(ct);
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);
        var rows = await query
            .OrderBy(o => o.ScheduledDate == null).ThenBy(o => o.ScheduledDate).ThenByDescending(o => o.Priority).ThenBy(o => o.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return new PaginatedResponse<ServiceOrderListItemModel>
        {
            Data         = await ListItemsAsync(rows, ct),
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    public async Task<ServiceOrderDetailModel?> GetAsync(Guid uuid, CancellationToken ct = default)
    {
        var order = await _db.ServiceOrders.AsNoTracking().Include(o => o.Materials).Include(o => o.Bom)
            .FirstOrDefaultAsync(o => o.UUID == uuid, ct);
        if (order is null) return null;

        var d = Fill(new ServiceOrderDetailModel(), order, await HeaderNamesAsync([order], ct));
        d.ServiceProductUuid = order.ServiceProductUuid;
        d.ServiceVariantUuid = order.ServiceVariantUuid;
        d.WarehouseUuid      = order.WarehouseUuid;
        d.WarehouseName      = await _inv.Warehouses.IgnoreQueryFilters().AsNoTracking()
                                   .Where(w => w.Uuid == order.WarehouseUuid).Select(w => w.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        d.AssignedRoleId     = order.AssignedRoleId;
        d.AssignedRoleName   = order.AssignedRoleId is { } role && _services.GetService<IRoleNameLookup>() is { } roles
                                   ? (await roles.GetNamesAsync([role], ct)).GetValueOrDefault(role) : null;
        d.BomId              = order.BomId;
        d.BomNumber          = order.Bom?.BomNumber;
        d.BomVersion         = order.BomVersion;
        d.EstimatedHours     = order.EstimatedHours;
        d.ActualHours        = order.ActualHours;
        d.ActualStartDate    = order.ActualStartDate;
        d.ActualEndDate      = order.ActualEndDate;
        d.SourceType         = order.SourceType;
        d.SourceUuid         = order.SourceUuid;
        d.SourceLineUuid     = order.SourceLineUuid;
        d.SourceReference    = order.SourceReference;
        d.InvoicingPolicy    = order.InvoicingPolicy;
        d.BillingModel       = order.BillingModel;
        d.CompletionNotes    = order.CompletionNotes;
        d.CustomerSignature  = order.CustomerSignature;
        d.Notes              = order.Notes;
        d.TraceId            = order.TraceId;
        d.RowVersion         = Convert.ToBase64String(order.RowVersion ?? []);
        d.CreatedAt          = order.CreatedAt;
        d.UpdatedAt          = order.UpdatedAt;
        d.Materials          = await MaterialModelsAsync(order, ct);
        d.Ledger             = await LedgerEntriesAsync(order.Id, ct);
        d.AllowedActions     = AllowedActions(order.Status);
        return d;
    }

    public async Task<IReadOnlyList<ServiceMaterialModel>> GetMaterialsAsync(Guid uuid, CancellationToken ct = default)
    {
        var order = await _db.ServiceOrders.AsNoTracking().Include(o => o.Materials).FirstOrDefaultAsync(o => o.UUID == uuid, ct)
            ?? throw new NotFoundException("Service order", uuid);
        return await MaterialModelsAsync(order, ct);
    }

    public async Task<ServiceLedgerModel> GetLedgerAsync(Guid uuid, CancellationToken ct = default)
    {
        var id = await _db.ServiceOrders.AsNoTracking().Where(o => o.UUID == uuid).Select(o => (int?)o.Id).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Service order", uuid);
        var entries = await LedgerEntriesAsync(id, ct);
        return new ServiceLedgerModel
        {
            Entries = entries,
            NetByProduct = entries.GroupBy(e => e.VariantUuid).Select(g => new ServiceLedgerNetModel
            {
                VariantUuid = g.Key, ProductName = g.First().ProductName, Uom = g.First().Uom, NetQuantity = g.Sum(e => e.Quantity)
            }).ToList()
        };
    }

    /// <summary>FSD §15.6 — today's jobs, those waiting for materials, mine, and this week's completion rate (Monday–Sunday, UTC).</summary>
    public async Task<ServiceDashboardModel> GetDashboardAsync(int userId, CancellationToken ct = default)
    {
        const int Cap = 50;
        var today     = DateTime.UtcNow.Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var weekEnd   = weekStart.AddDays(6);
        var orders    = _db.ServiceOrders.AsNoTracking();

        var todays = await orders.Where(o => o.ScheduledDate == today && o.Status != ServiceOrderStatus.Cancelled)
            .OrderBy(o => o.ScheduledTime == null).ThenBy(o => o.ScheduledTime).ThenByDescending(o => o.Priority).Take(Cap).ToListAsync(ct);
        var waiting = await orders.Where(o => o.Status == ServiceOrderStatus.MaterialPending || o.Status == ServiceOrderStatus.Waiting)
            .OrderByDescending(o => o.Priority).ThenBy(o => o.ScheduledDate == null).ThenBy(o => o.ScheduledDate).Take(Cap).ToListAsync(ct);
        var mine = await orders.Where(o => o.AssignedUserId == userId && o.Status != ServiceOrderStatus.Completed &&
                                           o.Status != ServiceOrderStatus.Closed && o.Status != ServiceOrderStatus.Cancelled)
            .OrderBy(o => o.ScheduledDate == null).ThenBy(o => o.ScheduledDate).ThenByDescending(o => o.Priority).Take(Cap).ToListAsync(ct);
        var week = await orders.Where(o => o.ScheduledDate >= weekStart && o.ScheduledDate <= weekEnd && o.Status != ServiceOrderStatus.Cancelled)
            .Select(o => o.Status).ToListAsync(ct);

        var items = (await ListItemsAsync([.. todays, .. waiting, .. mine], ct)).ToDictionary(i => i.Uuid);
        var completed = week.Count(s => s is ServiceOrderStatus.Completed or ServiceOrderStatus.Closed);
        return new ServiceDashboardModel
        {
            Today               = todays.Select(o => items[o.UUID]).ToList(),
            WaitingForMaterials = waiting.Select(o => items[o.UUID]).ToList(),
            Mine                = mine.Select(o => items[o.UUID]).ToList(),
            CompletionRate      = new ServiceCompletionRateModel
            {
                CompletedThisWeek = completed,
                ScheduledThisWeek = week.Count,
                Percent           = week.Count == 0 ? 0m : decimal.Round(100m * completed / week.Count, 1)
            }
        };
    }

    // ── Pieces ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Contract note 6 — status-based; the UI also checks permissions.</summary>
    internal static List<string> AllowedActions(string status)
    {
        var actions = new List<string>();
        if (status != ServiceOrderStatus.Cancelled)          actions.Add("EDIT");
        if (status == ServiceOrderStatus.Draft)              actions.Add("PLAN");
        if (status == ServiceOrderStatus.Ready)              actions.Add("START");
        if (ServiceOrderStatus.IsRunning(status))            actions.Add("ADD_MATERIAL");
        if (status == ServiceOrderStatus.InProgress)         actions.Add("COMPLETE");
        if (status == ServiceOrderStatus.Completed)          actions.Add("CLOSE");
        if (ServiceOrderStatus.CanCancel(status))            actions.Add("CANCEL");
        return actions;
    }

    private sealed record HeaderNames(
        IReadOnlyDictionary<Guid, MaterialName> Variants, IReadOnlyDictionary<Guid, string> Products,
        IReadOnlyDictionary<Guid, string> Customers, IReadOnlyDictionary<int, string> Users);

    private async Task<List<ServiceOrderListItemModel>> ListItemsAsync(IReadOnlyList<ServiceOrder> orders, CancellationToken ct)
    {
        var names = await HeaderNamesAsync(orders, ct);
        return orders.DistinctBy(o => o.Id).Select(o => Fill(new ServiceOrderListItemModel(), o, names)).ToList();
    }

    private async Task<HeaderNames> HeaderNamesAsync(IReadOnlyList<ServiceOrder> orders, CancellationToken ct)
    {
        var variants = new Dictionary<Guid, MaterialName>();
        foreach (var org in orders.GroupBy(o => o.OrganizationId))
            foreach (var kv in await MaterialNamesAsync(org.Key, org.Select(o => o.ServiceVariantUuid).ToList(), ct))
                variants[kv.Key] = kv.Value;

        var productUuids = orders.Select(o => o.ServiceProductUuid).Distinct().ToList();
        var products = productUuids.Count == 0 ? new Dictionary<Guid, string>()
            : await _inv.Products.IgnoreQueryFilters().AsNoTracking().Where(p => productUuids.Contains(p.Uuid))
                .ToDictionaryAsync(p => p.Uuid, p => p.Name, ct);

        var customerIds = orders.Select(o => o.CustomerUuid).Distinct().ToList();
        IReadOnlyDictionary<Guid, string> customers = customerIds.Count > 0 && _services.GetService<ISupplierNameLookupService>() is { } partners
            ? await partners.GetNamesAsync(customerIds) : new Dictionary<Guid, string>();

        var userIds = orders.Where(o => o.AssignedUserId.HasValue).Select(o => o.AssignedUserId!.Value).Distinct().ToList();
        var users = userIds.Count > 0 && _services.GetService<IUserQueryService>() is { } userQuery
            ? (await userQuery.GetUsersAsync(userIds)).ToDictionary(u => u.UserId, u => u.DisplayName)
            : new Dictionary<int, string>();

        return new HeaderNames(variants, products, customers, users);
    }

    private static T Fill<T>(T model, ServiceOrder o, HeaderNames names) where T : ServiceOrderListItemModel
    {
        var variant = names.Variants.GetValueOrDefault(o.ServiceVariantUuid);
        model.Uuid               = o.UUID;
        model.ServiceNumber      = o.ServiceNumber;
        model.ServiceProductName = names.Products.GetValueOrDefault(o.ServiceProductUuid) ?? string.Empty;
        model.ServiceVariantName = variant?.VariantName;
        model.CustomerUuid       = o.CustomerUuid;
        model.CustomerName       = names.Customers.GetValueOrDefault(o.CustomerUuid) ?? string.Empty;
        model.ScheduledDate      = o.ScheduledDate?.ToString("yyyy-MM-dd");
        model.ScheduledTime      = o.ScheduledTime?.ToString(@"hh\:mm");
        model.AssignedUserId     = o.AssignedUserId;
        model.AssignedUserName   = o.AssignedUserId is { } u ? names.Users.GetValueOrDefault(u) : null;
        model.Status             = o.Status;
        model.Priority           = o.Priority;
        model.MaterialReadiness  = o.MaterialReadiness;
        model.Quantity           = o.Quantity;
        return model;
    }

    /// <summary>Variant display names, organization explicit: "Product – Variant", just the product for its default variant.</summary>
    private async Task<Dictionary<Guid, MaterialName>> MaterialNamesAsync(Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct)
    {
        var uuids = variantUuids.Distinct().ToList();
        if (uuids.Count == 0) return [];
        var rows = await _inv.ProductVariants.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.OrganizationId == organizationId && uuids.Contains(v.Uuid))
            .Select(v => new { v.Uuid, v.VariantName, v.IsDefault, v.Sku, ProductUuid = v.Product.Uuid, v.Product.Name, v.Product.ProductType })
            .ToListAsync(ct);
        return rows.ToDictionary(v => v.Uuid, v =>
        {
            var plain = v.IsDefault || string.IsNullOrWhiteSpace(v.VariantName) || v.VariantName == "Default";
            return new MaterialName(v.ProductUuid, plain ? v.Name : $"{v.Name} – {v.VariantName}", v.Name,
                plain ? null : v.VariantName, v.Sku, v.ProductType);
        });
    }

    private async Task<List<ServiceMaterialModel>> MaterialModelsAsync(ServiceOrder order, CancellationToken ct)
    {
        var smrs = order.Materials.OrderBy(m => m.Sequence).ThenBy(m => m.Id).ToList();
        if (smrs.Count == 0) return [];

        var names = await MaterialNamesAsync(order.OrganizationId, smrs.Select(m => m.MaterialVariantUuid).ToList(), ct);

        // Free stock now, in the order's warehouse (on hand less held).
        var variantUuids = smrs.Where(m => m.IsStock).Select(m => m.MaterialVariantUuid).Distinct().ToList();
        var available = variantUuids.Count == 0 ? new Dictionary<Guid, decimal>()
            : (await _inv.InventoryItems.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.Warehouse.Uuid == order.WarehouseUuid && variantUuids.Contains(i.Variant.Uuid))
                .Select(i => new { VariantUuid = i.Variant.Uuid, i.QtyOnHand, i.QtyReserved })
                .ToListAsync(ct))
              .GroupBy(i => i.VariantUuid).ToDictionary(g => g.Key, g => Math.Max(0m, g.Sum(i => i.QtyOnHand - i.QtyReserved)));

        var smrUuids = smrs.Select(m => m.UUID).ToList();
        var supply = (await _db.SupplyRequirements.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.OrganizationId == order.OrganizationId && s.DemandSourceType == SupplyDemandSourceType.ServiceOrder &&
                            smrUuids.Contains(s.DemandSourceUuid))
                .OrderByDescending(s => s.Id).Select(s => new { s.DemandSourceUuid, s.SupplyNumber, s.Status }).ToListAsync(ct))
            .GroupBy(s => s.DemandSourceUuid)
            .ToDictionary(g => g.Key, g => g.FirstOrDefault(s => s.Status != SupplyRequirementStatus.Cancelled) ?? g.First());

        var addedBy = smrs.Where(m => m.AddedBy.HasValue).Select(m => m.AddedBy!.Value).Distinct().ToList();
        var users = addedBy.Count > 0 && _services.GetService<IUserQueryService>() is { } userQuery
            ? (await userQuery.GetUsersAsync(addedBy)).ToDictionary(u => u.UserId, u => u.DisplayName)
            : new Dictionary<int, string>();

        var removable = ServiceOrderStatus.IsRunning(order.Status);
        return smrs.Select(m =>
        {
            var name = names.GetValueOrDefault(m.MaterialVariantUuid);
            var sr   = supply.GetValueOrDefault(m.UUID);
            return new ServiceMaterialModel
            {
                Uuid                    = m.UUID,
                ProductUuid             = m.MaterialProductUuid,
                VariantUuid             = m.MaterialVariantUuid,
                ProductName             = name?.ProductName ?? string.Empty,
                VariantName             = name?.VariantName,
                Sku                     = name?.Sku,
                SourceType              = m.SourceType,
                RequiredQuantity        = m.RequiredQuantity,
                NetQuantity             = m.NetQuantity,
                ScrapAllowance          = m.ScrapAllowance,
                ReservedQuantity        = m.ReservedQuantity,
                IssuedQuantity          = m.IssuedQuantity,
                ConsumedQuantity        = m.ConsumedQuantity,
                ReturnedQuantity        = m.ReturnedQuantity,
                ShortageQuantity        = m.ShortageQuantity,
                AvailableQuantity       = m.IsStock ? available.GetValueOrDefault(m.MaterialVariantUuid) : 0m,
                Uom                     = m.Uom,
                IsCritical              = m.IsCritical,
                IsAdhoc                 = m.IsAdhoc,
                Status                  = m.Status,
                RequiredDate            = m.RequiredDate.ToString("yyyy-MM-dd"),
                AddedByName             = m.AddedBy is { } a ? users.GetValueOrDefault(a) : null,
                Notes                   = m.Notes,
                SupplyRequirementNumber = sr?.SupplyNumber,
                SupplyRequirementStatus = sr?.Status,
                CanRemove               = removable && m.IsAdhoc && m.IssuedQuantity == 0 && m.Status != SmrStatus.Cancelled
            };
        }).ToList();
    }

    private async Task<List<ServiceLedgerEntryModel>> LedgerEntriesAsync(int serviceOrderId, CancellationToken ct) =>
        await _db.ServiceLedgerEntries.AsNoTracking()
            .Where(e => e.ServiceOrderId == serviceOrderId)
            .OrderBy(e => e.TransactionDate).ThenBy(e => e.Id)
            .Select(e => new ServiceLedgerEntryModel
            {
                Id                   = e.Id,
                EntryType            = e.EntryType,
                ProductUuid          = e.ProductUuid,
                VariantUuid          = e.VariantUuid,
                ProductName          = e.ProductName,
                Quantity             = e.Quantity,
                Uom                  = e.Uom,
                WarehouseName        = e.WarehouseName,
                SourceDocumentType   = e.SourceDocumentType,
                SourceDocumentNumber = e.SourceDocumentNumber,
                MovementType         = e.MovementType,
                TransactionDate      = e.TransactionDate,
                Notes                = e.Notes
            })
            .ToListAsync(ct);
}
