using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Services;

internal sealed class SaleOrderConfigService : ISaleOrderConfigService
{
    private readonly DemandDbContext _db;
    private readonly IUserQueryService _userQuery;

    public SaleOrderConfigService(DemandDbContext db, IUserQueryService userQuery)
    {
        _db        = db;
        _userQuery = userQuery;
    }

    public async Task<SaleOrderConfigModel> GetConfigAsync()
    {
        var entity = await GetOrCreateAsync();
        return ToModel(entity);
    }

    public async Task<SaleOrderConfigModel> UpdateConfigAsync(UpdateSaleOrderConfigRequest req, int updatedBy)
    {
        var entity = await GetOrCreateAsync();
        var now = DateTime.UtcNow;

        void CaptureChange(string field, string? oldValue, string? newValue)
        {
            if (oldValue == newValue) return;
            _db.SaleOrderConfigAudits.Add(new SaleOrderConfigAudit
            {
                SaleOrderConfigId = entity.Id,
                FieldChanged      = field,
                OldValue          = oldValue,
                NewValue          = newValue,
                ChangedBy         = updatedBy,
                ChangedAt         = now
            });
        }

        CaptureChange(nameof(SaleOrderConfig.AutoPoEnabled), entity.AutoPoEnabled.ToString(), req.AutoPoEnabled.ToString());
        CaptureChange(nameof(SaleOrderConfig.SupplierSelectionMode), entity.SupplierSelectionMode, req.SupplierSelectionMode);
        CaptureChange(nameof(SaleOrderConfig.AutoPoApprovalMode), entity.AutoPoApprovalMode, req.AutoPoApprovalMode);
        CaptureChange(nameof(SaleOrderConfig.DropShipEnabled), entity.DropShipEnabled.ToString(), req.DropShipEnabled.ToString());
        CaptureChange(nameof(SaleOrderConfig.SelfPickupEnabled), entity.SelfPickupEnabled.ToString(), req.SelfPickupEnabled.ToString());
        CaptureChange(nameof(SaleOrderConfig.DefaultFulfillmentMode), entity.DefaultFulfillmentMode, req.DefaultFulfillmentMode);
        CaptureChange(nameof(SaleOrderConfig.ReservationTtlHours), entity.ReservationTtlHours.ToString(), req.ReservationTtlHours.ToString());
        CaptureChange(nameof(SaleOrderConfig.PartialFulfillmentAllowed), entity.PartialFulfillmentAllowed.ToString(), req.PartialFulfillmentAllowed.ToString());
        CaptureChange(nameof(SaleOrderConfig.EmailIntimationEnabled), entity.EmailIntimationEnabled.ToString(), req.EmailIntimationEnabled.ToString());
        CaptureChange(nameof(SaleOrderConfig.IntimationDepartmentId), entity.IntimationDepartmentId?.ToString(), req.IntimationDepartmentId?.ToString());
        CaptureChange(nameof(SaleOrderConfig.IntimationCcEmails), entity.IntimationCcEmails, req.IntimationCcEmails);
        CaptureChange(nameof(SaleOrderConfig.ShipmentRequiredDefault), entity.ShipmentRequiredDefault.ToString(), req.ShipmentRequiredDefault.ToString());

        entity.AutoPoEnabled             = req.AutoPoEnabled;
        entity.SupplierSelectionMode     = req.SupplierSelectionMode;
        entity.AutoPoApprovalMode        = req.AutoPoApprovalMode;
        entity.DropShipEnabled           = req.DropShipEnabled;
        entity.SelfPickupEnabled         = req.SelfPickupEnabled;
        entity.DefaultFulfillmentMode    = req.DefaultFulfillmentMode;
        entity.ReservationTtlHours       = req.ReservationTtlHours;
        entity.PartialFulfillmentAllowed = req.PartialFulfillmentAllowed;
        entity.EmailIntimationEnabled    = req.EmailIntimationEnabled;
        entity.IntimationDepartmentId    = req.IntimationDepartmentId;
        entity.IntimationCcEmails        = req.IntimationCcEmails;
        entity.ShipmentRequiredDefault   = req.ShipmentRequiredDefault;
        entity.UpdatedBy                 = updatedBy;
        entity.UpdatedAt                 = now;

        // Single SaveChangesAsync — commits the config update and every audit row together.
        await _db.SaveChangesAsync();
        return ToModel(entity);
    }

    public async Task<PaginatedResponse<SaleOrderConfigAuditModel>> GetAuditAsync(SaleOrderConfigAuditFilter filter)
    {
        var configId = await _db.SaleOrderConfigs.Select(x => (int?)x.Id).FirstOrDefaultAsync();

        var query = configId is null
            ? _db.SaleOrderConfigAudits.Where(a => false) // no config row yet — nothing has ever changed
            : _db.SaleOrderConfigAudits.Where(a => a.SaleOrderConfigId == configId).AsQueryable();

        query = query.OrderByDescending(a => a.ChangedAt);

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);

        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new SaleOrderConfigAuditModel
            {
                Id           = a.Id,
                FieldChanged = a.FieldChanged,
                OldValue     = a.OldValue,
                NewValue     = a.NewValue,
                ChangedBy    = a.ChangedBy,
                ChangedAt    = a.ChangedAt
            })
            .ToListAsync();

        var names = await _userQuery.GetUsersAsync(rows.Select(r => r.ChangedBy).Distinct().ToList());
        var nameById = names.ToDictionary(n => n.UserId, n => n.DisplayName);
        foreach (var row in rows)
            row.ChangedByName = nameById.GetValueOrDefault(row.ChangedBy);

        return new PaginatedResponse<SaleOrderConfigAuditModel>
        {
            Data         = rows,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    // §3.2 is "one row per org" but only defines GET/PUT — no POST — so GET has to be able to
    // self-initialize a config on first read rather than 404 for every org until somebody who
    // knows a create endpoint doesn't exist first calls one.
    private async Task<SaleOrderConfig> GetOrCreateAsync()
    {
        var entity = await _db.SaleOrderConfigs.FirstOrDefaultAsync();
        if (entity is not null) return entity;

        entity = new SaleOrderConfig();
        _db.SaleOrderConfigs.Add(entity);
        await _db.SaveChangesAsync();
        return entity;
    }

    private static SaleOrderConfigModel ToModel(SaleOrderConfig x) => new()
    {
        Uuid                      = x.Uuid,
        AutoPoEnabled             = x.AutoPoEnabled,
        SupplierSelectionMode     = x.SupplierSelectionMode,
        AutoPoApprovalMode        = x.AutoPoApprovalMode,
        DropShipEnabled           = x.DropShipEnabled,
        SelfPickupEnabled         = x.SelfPickupEnabled,
        DefaultFulfillmentMode    = x.DefaultFulfillmentMode,
        ReservationTtlHours       = x.ReservationTtlHours,
        PartialFulfillmentAllowed = x.PartialFulfillmentAllowed,
        EmailIntimationEnabled    = x.EmailIntimationEnabled,
        IntimationDepartmentId    = x.IntimationDepartmentId,
        IntimationCcEmails        = x.IntimationCcEmails,
        ShipmentRequiredDefault   = x.ShipmentRequiredDefault,
        UpdatedBy                 = x.UpdatedBy,
        UpdatedAt                 = x.UpdatedAt
    };
}
