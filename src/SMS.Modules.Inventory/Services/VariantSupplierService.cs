using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Services;

// RC-001 (FSD Addendum 28) — Supplier Rate Card Management.
internal sealed class VariantSupplierService : IVariantSupplierService
{
    private readonly InventoryDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IOrganizationCurrencyService _orgCurrency;
    private readonly IPurchaseOrderPriceLookupService _poPriceLookup;
    private readonly ISupplierNameLookupService _supplierNames;
    private readonly ISupplierScoreLookupService _supplierScores;
    private readonly IUserQueryService _userQuery;
    // RC-007 — a rate not reviewed within this window shows the STALE badge; same threshold the
    // monthly StaleRateAlertJob detects against. Configurable via RateCard:StaleThresholdDays,
    // default 180 (replaces RC-002's original hardcoded 6-month constant).
    private readonly int _staleThresholdDays;

    // Rate changes beyond this threshold require an explicit ChangeReason.
    private const decimal ChangeReasonThreshold = 0.10m;

    public VariantSupplierService(
        InventoryDbContext db, ITenantContext tenantContext, IOrganizationCurrencyService orgCurrency,
        IPurchaseOrderPriceLookupService poPriceLookup, ISupplierNameLookupService supplierNames,
        ISupplierScoreLookupService supplierScores, IUserQueryService userQuery, IConfiguration configuration)
    {
        _db             = db;
        _tenantContext  = tenantContext;
        _supplierNames  = supplierNames;
        _supplierScores = supplierScores;
        _orgCurrency   = orgCurrency;
        _poPriceLookup = poPriceLookup;
        _userQuery     = userQuery;
        _staleThresholdDays = int.TryParse(configuration["RateCard:StaleThresholdDays"], out var d) ? d : 180;
    }

    public async Task<Guid> CreateAsync(CreateVariantSupplierRequest req, int createdBy)
    {
        var variant = await _db.ProductVariants.FirstOrDefaultAsync(v => v.Uuid == req.VariantUuid)
            ?? throw new NotFoundException("ProductVariant", req.VariantUuid);

        var tiers = req.DiscountTiers ?? [];
        DiscountTierValidator.Validate(tiers);

        var currencyId = req.CurrencyId
            ?? await _orgCurrency.GetBaseCurrencyIdAsync(_tenantContext.OrganizationId)
            ?? throw new BadRequestException(
                "Currency is required — this organization has no base currency configured, so it must be supplied explicitly.");

        var entity = new VariantSupplier
        {
            VariantId      = variant.Id,
            SupplierId     = req.SupplierUuid,
            VendorUnitCost = req.VendorUnitCost,
            LeadTimeDays   = req.LeadTimeDays,
            EffectiveFrom  = req.EffectiveFrom?.Date ?? DateTime.UtcNow.Date,
            EffectiveTo    = req.EffectiveTo,
            CurrencyId     = currencyId,
            MinOrderValue  = req.MinOrderValue,
            MinOrderQty    = req.MinOrderQty,
            DiscountTiers  = tiers.Count > 0 ? JsonSerializer.Serialize(tiers) : null,
            QuotationRef   = req.QuotationRef,
            Notes          = req.Notes,
            VendorPartNo   = req.VendorPartNo,
            CreatedBy      = createdBy,
            CreatedDate    = DateTime.UtcNow
        };

        _db.VariantSuppliers.Add(entity);
        await _db.SaveChangesAsync();
        return entity.Uuid;
    }

    public async Task<bool> UpdateAsync(Guid uuid, UpdateVariantSupplierRequest req, int modifiedBy)
    {
        var entity = await _db.VariantSuppliers.FirstOrDefaultAsync(x => x.Uuid == uuid);
        if (entity is null) return false;

        var tiers = req.DiscountTiers ?? [];
        DiscountTierValidator.Validate(tiers);
        var newDiscountTiersJson = tiers.Count > 0 ? JsonSerializer.Serialize(tiers) : null;

        // Change-reason enforcement — >10% move on VendorUnitCost requires an explicit reason.
        if (entity.VendorUnitCost != req.VendorUnitCost && entity.VendorUnitCost != 0)
        {
            var pctChange = Math.Abs(req.VendorUnitCost - entity.VendorUnitCost) / entity.VendorUnitCost;
            if (pctChange > ChangeReasonThreshold && string.IsNullOrWhiteSpace(req.ChangeReason))
                throw new BadRequestException(
                    "Change reason is required for rate changes greater than 10%.");
        }

        var now = DateTime.UtcNow;
        void CaptureChange(string field, string? oldValue, string? newValue)
        {
            if (oldValue == newValue) return;
            _db.SupplierRateHistories.Add(new SupplierRateHistory
            {
                VariantSupplierId = entity.Id,
                FieldChanged      = field,
                OldValue          = oldValue,
                NewValue          = newValue,
                ChangeReason      = req.ChangeReason,
                ChangedBy         = modifiedBy,
                ChangedAt         = now
            });
        }

        CaptureChange(nameof(VariantSupplier.VendorUnitCost), entity.VendorUnitCost.ToString(), req.VendorUnitCost.ToString());
        CaptureChange(nameof(VariantSupplier.LeadTimeDays), entity.LeadTimeDays?.ToString(), req.LeadTimeDays?.ToString());
        CaptureChange(nameof(VariantSupplier.EffectiveFrom), entity.EffectiveFrom.ToString("O"), req.EffectiveFrom.ToString("O"));
        CaptureChange(nameof(VariantSupplier.EffectiveTo), entity.EffectiveTo?.ToString("O"), req.EffectiveTo?.ToString("O"));
        CaptureChange(nameof(VariantSupplier.CurrencyId), entity.CurrencyId.ToString(), req.CurrencyId.ToString());
        CaptureChange(nameof(VariantSupplier.MinOrderValue), entity.MinOrderValue?.ToString(), req.MinOrderValue?.ToString());
        CaptureChange(nameof(VariantSupplier.MinOrderQty), entity.MinOrderQty?.ToString(), req.MinOrderQty?.ToString());
        CaptureChange(nameof(VariantSupplier.DiscountTiers), entity.DiscountTiers, newDiscountTiersJson);
        CaptureChange(nameof(VariantSupplier.QuotationRef), entity.QuotationRef, req.QuotationRef);
        CaptureChange(nameof(VariantSupplier.Notes), entity.Notes, req.Notes);
        CaptureChange(nameof(VariantSupplier.VendorPartNo), entity.VendorPartNo, req.VendorPartNo);
        CaptureChange(nameof(VariantSupplier.IsActive), entity.IsActive.ToString(), req.IsActive.ToString());
        CaptureChange(nameof(VariantSupplier.LastReviewedAt), entity.LastReviewedAt?.ToString("O"), req.LastReviewedAt?.ToString("O"));
        CaptureChange(nameof(VariantSupplier.LastReviewedBy), entity.LastReviewedBy?.ToString(), req.LastReviewedBy?.ToString());

        entity.VendorUnitCost = req.VendorUnitCost;
        entity.LeadTimeDays   = req.LeadTimeDays;
        entity.EffectiveFrom  = req.EffectiveFrom;
        entity.EffectiveTo    = req.EffectiveTo;
        entity.CurrencyId     = req.CurrencyId;
        entity.MinOrderValue  = req.MinOrderValue;
        entity.MinOrderQty    = req.MinOrderQty;
        entity.DiscountTiers  = newDiscountTiersJson;
        entity.QuotationRef   = req.QuotationRef;
        entity.Notes          = req.Notes;
        entity.VendorPartNo   = req.VendorPartNo;
        entity.IsActive       = req.IsActive;
        entity.LastReviewedAt = req.LastReviewedAt;
        entity.LastReviewedBy = req.LastReviewedBy;
        entity.ModifiedBy     = modifiedBy;
        entity.ModifiedDate   = now;

        // Single SaveChangesAsync — commits the rate update and every history row together.
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<VariantSupplierModel?> GetByIdAsync(Guid uuid)
    {
        // JsonSerializer.Deserialize can't be translated by EF into SQL — project the raw JSON
        // string first, then parse it after the query has executed.
        var row = await _db.VariantSuppliers
            .Where(x => x.Uuid == uuid)
            .Select(x => new
            {
                x.Id,
                x.Uuid,
                VariantUuid = x.Variant.Uuid,
                VariantSku  = x.Variant.Sku,
                ProductName = x.Variant.Product.Name,
                x.SupplierId,
                x.VendorUnitCost,
                x.LeadTimeDays,
                x.IsActive,
                x.EffectiveFrom,
                x.EffectiveTo,
                x.CurrencyId,
                x.MinOrderValue,
                x.MinOrderQty,
                x.QuotationRef,
                x.Notes,
                x.VendorPartNo,
                x.IsPreferred,
                x.LastReviewedAt,
                x.LastReviewedBy,
                x.CreatedDate,
                x.ModifiedDate,
                x.DiscountTiers
            })
            .FirstOrDefaultAsync();

        if (row is null) return null;

        // RC-004, Section 4 (Notes) — resolved from the latest history row for this field rather
        // than adding dedicated Notes-audit columns.
        DateTime? notesUpdatedAt = null;
        string?   notesUpdatedByName = null;
        var notesHistory = await _db.SupplierRateHistories
            .Where(h => h.VariantSupplierId == row.Id && h.FieldChanged == nameof(VariantSupplier.Notes))
            .OrderByDescending(h => h.ChangedAt)
            .Select(h => new { h.ChangedAt, h.ChangedBy })
            .FirstOrDefaultAsync();
        if (notesHistory is not null)
        {
            notesUpdatedAt = notesHistory.ChangedAt;
            var author = await _userQuery.GetUserAsync(notesHistory.ChangedBy);
            notesUpdatedByName = author?.DisplayName;
        }

        // RC-004, Section 6 (PO Reference).
        var poReference = await _poPriceLookup.GetPoReferenceSummaryAsync(row.VariantUuid, row.SupplierId);

        // RC-007 — same resolve-by-user-id pattern as NotesUpdatedByName above.
        string? lastReviewedByName = row.LastReviewedBy.HasValue
            ? (await _userQuery.GetUserAsync(row.LastReviewedBy.Value))?.DisplayName
            : null;

        return new VariantSupplierModel
        {
            Uuid           = row.Uuid,
            VariantUuid    = row.VariantUuid,
            VariantSku     = row.VariantSku,
            ProductName    = row.ProductName,
            SupplierId     = row.SupplierId,
            VendorUnitCost = row.VendorUnitCost,
            LeadTimeDays   = row.LeadTimeDays,
            IsActive       = row.IsActive,
            EffectiveFrom  = row.EffectiveFrom,
            EffectiveTo    = row.EffectiveTo,
            CurrencyId     = row.CurrencyId,
            MinOrderValue  = row.MinOrderValue,
            MinOrderQty    = row.MinOrderQty,
            QuotationRef   = row.QuotationRef,
            Notes          = row.Notes,
            NotesUpdatedAt = notesUpdatedAt,
            NotesUpdatedByName = notesUpdatedByName,
            VendorPartNo   = row.VendorPartNo,
            IsPreferred    = row.IsPreferred,
            LastReviewedAt = row.LastReviewedAt,
            LastReviewedBy = row.LastReviewedBy,
            LastReviewedByName = lastReviewedByName,
            CreatedDate    = row.CreatedDate,
            ModifiedDate   = row.ModifiedDate,
            DiscountTiers  = string.IsNullOrWhiteSpace(row.DiscountTiers)
                ? []
                : JsonSerializer.Deserialize<List<DiscountTierDto>>(row.DiscountTiers) ?? [],
            PoReference    = poReference is null ? null : new PoReferenceSummaryModel
            {
                LastPoUuid          = poReference.LastPoUuid,
                LastPoNumber        = poReference.LastPoNumber,
                LastPoDate          = poReference.LastPoDate,
                LastPoPrice         = poReference.LastPoPrice,
                PoCountLast12Months = poReference.PoCountLast12Months
            }
        };
    }

    // RC-007 — clears the stale flag without touching the rate itself. Mirrors SetPreferredAsync's
    // shape: single-field mutation, one audit history row, one SaveChangesAsync.
    public async Task<bool> MarkReviewedAsync(Guid uuid, int reviewedBy)
    {
        var entity = await _db.VariantSuppliers.FirstOrDefaultAsync(x => x.Uuid == uuid);
        if (entity is null) return false;

        var now = DateTime.UtcNow;
        var oldReviewedAt = entity.LastReviewedAt;

        entity.LastReviewedAt = now;
        entity.LastReviewedBy = reviewedBy;
        // ModifiedBy/ModifiedDate deliberately untouched — this isn't a rate edit.

        _db.SupplierRateHistories.Add(new SupplierRateHistory
        {
            VariantSupplierId = entity.Id,
            FieldChanged      = nameof(VariantSupplier.LastReviewedAt),
            OldValue          = oldReviewedAt?.ToString("O"),
            NewValue          = now.ToString("O"),
            ChangeReason      = "Marked as reviewed",
            ChangedBy         = reviewedBy,
            ChangedAt         = now
        });

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<PaginatedResponse<SupplierRateHistoryModel>> GetHistoryAsync(Guid uuid, RateHistoryFilter filter)
    {
        var variantSupplierId = await _db.VariantSuppliers
            .Where(x => x.Uuid == uuid)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync()
            ?? throw new NotFoundException("VariantSupplier", uuid);

        var query = _db.SupplierRateHistories
            .Where(h => h.VariantSupplierId == variantSupplierId)
            .OrderByDescending(h => h.ChangedAt);

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var data = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(h => new SupplierRateHistoryModel
            {
                Id           = h.Id,
                FieldChanged = h.FieldChanged,
                OldValue     = h.OldValue,
                NewValue     = h.NewValue,
                ChangeReason = h.ChangeReason,
                ChangedBy    = h.ChangedBy,
                ChangedAt    = h.ChangedAt
            })
            .ToListAsync();

        var userIds = data.Select(h => h.ChangedBy).Distinct().ToList();
        var users   = await _userQuery.GetUsersAsync(userIds);
        var namesByUserId = users.ToDictionary(u => u.UserId, u => u.DisplayName);
        foreach (var h in data)
            h.ChangedByName = namesByUserId.TryGetValue(h.ChangedBy, out var n) ? n : null;

        return new PaginatedResponse<SupplierRateHistoryModel>
        {
            Data         = data,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<PaginatedResponse<RateCardGridRowModel>> GetListAsync(RateCardListFilter filter)
    {
        var query = _db.VariantSuppliers
            .Where(x => x.SupplierId == filter.SupplierId)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLower();
            query = query.Where(x =>
                x.Variant.Product.Name.ToLower().Contains(term) ||
                x.Variant.VariantName.ToLower().Contains(term) ||
                x.Variant.Sku.ToLower().Contains(term) ||
                (x.VendorPartNo != null && x.VendorPartNo.ToLower().Contains(term)));
        }

        query = (filter.SortBy?.ToLowerInvariant(), filter.SortDir?.ToLowerInvariant() == "desc") switch
        {
            ("variantname", true)    => query.OrderByDescending(x => x.Variant.VariantName),
            ("variantname", false)   => query.OrderBy(x => x.Variant.VariantName),
            ("sku", true)            => query.OrderByDescending(x => x.Variant.Sku),
            ("sku", false)           => query.OrderBy(x => x.Variant.Sku),
            ("vendorunitcost", true) => query.OrderByDescending(x => x.VendorUnitCost),
            ("vendorunitcost", false)=> query.OrderBy(x => x.VendorUnitCost),
            ("leadtimedays", true)   => query.OrderByDescending(x => x.LeadTimeDays),
            ("leadtimedays", false)  => query.OrderBy(x => x.LeadTimeDays),
            ("minordervalue", true)  => query.OrderByDescending(x => x.MinOrderValue),
            ("minordervalue", false) => query.OrderBy(x => x.MinOrderValue),
            ("effectivefrom", true)  => query.OrderByDescending(x => x.EffectiveFrom),
            ("effectivefrom", false) => query.OrderBy(x => x.EffectiveFrom),
            ("effectiveto", true)    => query.OrderByDescending(x => x.EffectiveTo),
            ("effectiveto", false)   => query.OrderBy(x => x.EffectiveTo),
            (_, true)                => query.OrderByDescending(x => x.Variant.Product.Name),
            _                        => query.OrderBy(x => x.Variant.Product.Name)
        };

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var rows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Uuid,
                VariantUuid = x.Variant.Uuid,
                ProductName = x.Variant.Product.Name,
                VariantName = x.Variant.VariantName,
                Sku         = x.Variant.Sku,
                x.VendorPartNo,
                x.VendorUnitCost,
                x.CurrencyId,
                x.LeadTimeDays,
                x.MinOrderValue,
                x.MinOrderQty,
                x.IsPreferred,
                x.EffectiveFrom,
                x.EffectiveTo,
                x.LastReviewedAt,
                x.LastReviewedBy,
                x.DiscountTiers,
                x.QuotationRef,
                x.Notes,
                x.IsActive
            })
            .ToListAsync();

        var lastPrices = await _poPriceLookup.GetLastPricesAsync(
            rows.Select(r => (r.VariantUuid, filter.SupplierId)).Distinct().ToList());

        var today = DateTime.UtcNow.Date;
        var data = rows.Select(r => new RateCardGridRowModel
        {
            Uuid           = r.Uuid,
            VariantUuid    = r.VariantUuid,
            ProductName    = r.ProductName,
            VariantName    = r.VariantName,
            Sku            = r.Sku,
            VendorPartNo   = r.VendorPartNo,
            VendorUnitCost = r.VendorUnitCost,
            CurrencyId     = r.CurrencyId,
            LastPoPrice    = lastPrices.TryGetValue((r.VariantUuid, filter.SupplierId), out var p) ? p.UnitPrice : null,
            LeadTimeDays   = r.LeadTimeDays,
            MinOrderValue  = r.MinOrderValue,
            MinOrderQty    = r.MinOrderQty,
            IsPreferred    = r.IsPreferred,
            EffectiveFrom  = r.EffectiveFrom,
            EffectiveTo    = r.EffectiveTo,
            Status         = ComputeStatus(r.EffectiveFrom, r.EffectiveTo, r.LastReviewedAt, today),
            LastReviewedAt = r.LastReviewedAt,
            LastReviewedBy = r.LastReviewedBy,
            QuotationRef   = r.QuotationRef,
            Notes          = r.Notes,
            IsActive       = r.IsActive,
            DiscountTiers  = string.IsNullOrWhiteSpace(r.DiscountTiers)
                ? []
                : JsonSerializer.Deserialize<List<DiscountTierDto>>(r.DiscountTiers) ?? []
        }).ToList();

        return new PaginatedResponse<RateCardGridRowModel>
        {
            Data         = data,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    // Checked in urgency order: an expired-and-unreviewed row shows EXPIRED, not STALE. A row
    // that's never been reviewed at all is ACTIVE, not STALE — the ticket only defines "reviewed
    // too long ago" as stale, not "never reviewed." (RC-007's own stale-detection job is a separate
    // query that does treat never-reviewed as stale — see StaleRateAlertJob.)
    private string ComputeStatus(DateTime effectiveFrom, DateTime? effectiveTo, DateTime? lastReviewedAt, DateTime today)
    {
        if (effectiveTo.HasValue && effectiveTo.Value.Date < today) return "EXPIRED";
        if (effectiveFrom.Date > today) return "PENDING";
        if (lastReviewedAt.HasValue && lastReviewedAt.Value.Date < today.AddDays(-_staleThresholdDays)) return "STALE";
        return "ACTIVE";
    }

    public async Task<bool> SetPreferredAsync(Guid uuid, int modifiedBy)
    {
        var target = await _db.VariantSuppliers.FirstOrDefaultAsync(x => x.Uuid == uuid);
        if (target is null) return false;

        var now = DateTime.UtcNow;
        var others = await _db.VariantSuppliers
            .Where(x => x.VariantId == target.VariantId && x.Id != target.Id && x.IsPreferred)
            .ToListAsync();

        foreach (var other in others)
        {
            other.IsPreferred  = false;
            other.ModifiedBy   = modifiedBy;
            other.ModifiedDate = now;
        }

        target.IsPreferred  = true;
        target.ModifiedBy   = modifiedBy;
        target.ModifiedDate = now;

        // §3.3's DEFAULT_SUPPLIER auto-PO mode reads ProductVariant.DefaultSupplierId
        // (GetDefaultSupplierIdAsync, A29-P5-02) — this is the only place a variant's default
        // supplier is ever actually chosen, so it is kept in step with whichever rate is preferred
        // here rather than left as a column nothing writes.
        var variant = await _db.ProductVariants.FirstOrDefaultAsync(v => v.Id == target.VariantId);
        if (variant is not null)
            variant.DefaultSupplierId = target.SupplierId;

        // Single SaveChangesAsync — clears every other supplier's preferred flag for this variant,
        // sets this one, and updates the variant's default supplier, atomically.
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<List<RateComparisonRowModel>> GetComparisonAsync(Guid variantUuid)
    {
        var rows = await _db.VariantSuppliers
            .Where(x => x.Variant.Uuid == variantUuid && x.IsActive)
            .Select(x => new
            {
                x.Uuid,
                x.SupplierId,
                x.VendorPartNo,
                x.VendorUnitCost,
                x.CurrencyId,
                x.LeadTimeDays,
                x.MinOrderValue,
                x.MinOrderQty,
                x.IsPreferred
            })
            .ToListAsync();

        if (rows.Count == 0) return [];

        var supplierIds = rows.Select(r => r.SupplierId).Distinct().ToList();

        var names  = await _supplierNames.GetNamesAsync(supplierIds);
        var grades = await _supplierScores.GetLatestGradesAsync(supplierIds);
        var lastPo = await _poPriceLookup.GetLastPricesAsync(
            supplierIds.Select(s => (variantUuid, s)).ToList());

        return rows.Select(r => new RateComparisonRowModel
        {
            Uuid           = r.Uuid,
            SupplierId     = r.SupplierId,
            SupplierName   = names.TryGetValue(r.SupplierId, out var n) ? n : "(unknown supplier)",
            VendorPartNo   = r.VendorPartNo,
            VendorUnitCost = r.VendorUnitCost,
            CurrencyId     = r.CurrencyId,
            LeadTimeDays   = r.LeadTimeDays,
            MinOrderValue  = r.MinOrderValue,
            MinOrderQty    = r.MinOrderQty,
            LastPoDate     = lastPo.TryGetValue((variantUuid, r.SupplierId), out var po) ? po.PoDate : null,
            ScorecardGrade = grades.TryGetValue(r.SupplierId, out var g) ? g : null,
            IsPreferred    = r.IsPreferred
        })
        .OrderBy(r => r.VendorUnitCost)
        .ToList();
    }

    public async Task<Guid?> GetDefaultSupplierIdAsync(Guid variantUuid) =>
        await _db.ProductVariants.AsNoTracking()
            .Where(v => v.Uuid == variantUuid)
            .Select(v => v.DefaultSupplierId)
            .FirstOrDefaultAsync();

    // ── RC-005: Bulk Rate Adjustment ─────────────────────────────────────────

    private const int UndoWindowHours = 24;

    private static decimal ComputeNewRate(decimal current, string method, decimal value) =>
        method.ToUpperInvariant() switch
        {
            "PERCENTAGE" => current * (1 + value / 100m),
            "FIXED"      => current + value,
            _            => throw new BadRequestException(
                $"Unknown bulk-adjust method '{method}'. Must be PERCENTAGE or FIXED.")
        };

    public async Task<List<BulkAdjustPreviewRowModel>> PreviewBulkAdjustAsync(BulkAdjustRequest req)
    {
        if (req.VariantSupplierIds.Count == 0)
            throw new BadRequestException("No rate cards selected.");

        var rows = await _db.VariantSuppliers
            .Where(x => req.VariantSupplierIds.Contains(x.Uuid))
            .Select(x => new
            {
                x.Uuid,
                ProductName = x.Variant.Product.Name,
                VariantName = x.Variant.VariantName,
                x.VendorUnitCost
            })
            .ToListAsync();

        return rows.Select(r =>
        {
            var newRate = ComputeNewRate(r.VendorUnitCost, req.Method, req.Value);
            var diff    = newRate - r.VendorUnitCost;
            return new BulkAdjustPreviewRowModel
            {
                VariantSupplierId = r.Uuid,
                ProductName       = r.ProductName,
                VariantName       = r.VariantName,
                CurrentRate       = r.VendorUnitCost,
                NewRate           = newRate,
                Difference        = diff,
                DiffPct           = r.VendorUnitCost != 0 ? diff / r.VendorUnitCost * 100 : 0
            };
        }).ToList();
    }

    public async Task<BulkAdjustConfirmResult> ConfirmBulkAdjustAsync(BulkAdjustRequest req, int performedBy)
    {
        if (req.VariantSupplierIds.Count == 0)
            throw new BadRequestException("No rate cards selected.");
        if (string.IsNullOrWhiteSpace(req.ChangeReason))
            throw new BadRequestException("Change reason is required for bulk adjustments.");

        var entities = await _db.VariantSuppliers
            .Where(x => req.VariantSupplierIds.Contains(x.Uuid))
            .ToListAsync();

        var now = DateTime.UtcNow;
        var bulkOp = new BulkRateOperation
        {
            Method        = req.Method.ToUpperInvariant(),
            Value         = req.Value,
            AffectedCount = entities.Count,
            ChangeReason  = req.ChangeReason,
            PerformedBy   = performedBy,
            PerformedAt   = now
        };
        _db.BulkRateOperations.Add(bulkOp);

        decimal totalImpact = 0;
        foreach (var entity in entities)
        {
            var oldRate = entity.VendorUnitCost;
            var newRate = ComputeNewRate(oldRate, req.Method, req.Value);
            totalImpact += newRate - oldRate;

            entity.VendorUnitCost = newRate;
            entity.ModifiedBy     = performedBy;
            entity.ModifiedDate   = now;

            _db.SupplierRateHistories.Add(new SupplierRateHistory
            {
                VariantSupplierId = entity.Id,
                FieldChanged      = nameof(VariantSupplier.VendorUnitCost),
                OldValue          = oldRate.ToString(),
                NewValue          = newRate.ToString(),
                ChangeReason      = $"{req.ChangeReason} (bulk adjustment)",
                ChangedBy         = performedBy,
                ChangedAt         = now,
                // Navigation, not the scalar — lets EF fix up bulkOp's generated Id onto this row
                // within the same SaveChangesAsync() below.
                BulkOperation     = bulkOp
            });
        }

        bulkOp.TotalImpactAmount = totalImpact;

        // Single SaveChangesAsync — the operation log row, every rate update, and every history
        // row commit together.
        await _db.SaveChangesAsync();

        return new BulkAdjustConfirmResult
        {
            BulkOperationId   = bulkOp.Uuid,
            AffectedCount     = entities.Count,
            TotalImpactAmount = totalImpact
        };
    }

    public async Task UndoBulkAdjustAsync(Guid bulkOperationId, int performedBy)
    {
        var bulkOp = await _db.BulkRateOperations.FirstOrDefaultAsync(b => b.Uuid == bulkOperationId)
            ?? throw new NotFoundException("BulkRateOperation", bulkOperationId);

        if (bulkOp.IsUndone)
            throw new BadRequestException("Operation already reversed.");
        if (bulkOp.PerformedAt < DateTime.UtcNow.AddHours(-UndoWindowHours))
            throw new BadRequestException("Undo window expired.");

        var historyRows = await _db.SupplierRateHistories
            .Where(h => h.BulkOperationId == bulkOp.Id)
            .ToListAsync();

        var variantSupplierIds = historyRows.Select(h => h.VariantSupplierId).Distinct().ToList();
        var entities = await _db.VariantSuppliers
            .Where(x => variantSupplierIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);

        var now = DateTime.UtcNow;
        foreach (var h in historyRows)
        {
            if (!entities.TryGetValue(h.VariantSupplierId, out var entity)) continue;
            if (!decimal.TryParse(h.OldValue, out var restoredRate)) continue;

            var currentRate = entity.VendorUnitCost;
            entity.VendorUnitCost = restoredRate;
            entity.ModifiedBy     = performedBy;
            entity.ModifiedDate   = now;

            _db.SupplierRateHistories.Add(new SupplierRateHistory
            {
                VariantSupplierId = entity.Id,
                FieldChanged      = nameof(VariantSupplier.VendorUnitCost),
                OldValue          = currentRate.ToString(),
                NewValue          = restoredRate.ToString(),
                ChangeReason      = $"Undo of bulk operation {bulkOp.Uuid}",
                ChangedBy         = performedBy,
                ChangedAt         = now
                // BulkOperationId deliberately left null — this reversal is a one-way action, not
                // itself something a future undo should be able to find and re-reverse.
            });
        }

        bulkOp.IsUndone = true;

        await _db.SaveChangesAsync();
    }

    public async Task<List<BulkRateOperationModel>> GetRecentBulkOperationsAsync()
    {
        var cutoff = DateTime.UtcNow.AddHours(-UndoWindowHours);

        var ops = await _db.BulkRateOperations
            .Where(b => b.PerformedAt >= cutoff)
            .OrderByDescending(b => b.PerformedAt)
            .Select(b => new
            {
                b.Uuid,
                b.Method,
                b.Value,
                b.AffectedCount,
                b.TotalImpactAmount,
                b.ChangeReason,
                b.PerformedBy,
                b.PerformedAt,
                b.IsUndone
            })
            .ToListAsync();

        var userIds = ops.Select(o => o.PerformedBy).Distinct().ToList();
        var users   = await _userQuery.GetUsersAsync(userIds);
        var namesByUserId = users.ToDictionary(u => u.UserId, u => u.DisplayName);

        return ops.Select(o => new BulkRateOperationModel
        {
            Uuid              = o.Uuid,
            Method            = o.Method,
            Value             = o.Value,
            AffectedCount     = o.AffectedCount,
            TotalImpactAmount = o.TotalImpactAmount,
            ChangeReason      = o.ChangeReason,
            PerformedBy       = o.PerformedBy,
            PerformedByName   = namesByUserId.TryGetValue(o.PerformedBy, out var n) ? n : null,
            PerformedAt       = o.PerformedAt,
            IsUndone          = o.IsUndone
        }).ToList();
    }

    // ── RC-006: Excel Import/Export ──────────────────────────────────────────

    public async Task<List<RateCardExportRowModel>> GetExportRowsAsync(Guid supplierId)
    {
        return await _db.VariantSuppliers
            .Where(x => x.SupplierId == supplierId && x.IsActive)
            .OrderBy(x => x.Variant.Product.Name).ThenBy(x => x.Variant.VariantName)
            .Select(x => new RateCardExportRowModel
            {
                ProductCode   = x.Variant.Product.Sku,
                ProductName   = x.Variant.Product.Name,
                VariantSku    = x.Variant.Sku,
                VariantName   = x.Variant.VariantName,
                VendorPartNo  = x.VendorPartNo,
                CurrentRate   = x.VendorUnitCost,
                LeadDays      = x.LeadTimeDays,
                MinQty        = x.MinOrderQty,
                EffectiveFrom = x.EffectiveFrom,
                EffectiveTo   = x.EffectiveTo,
                Notes         = x.Notes
            })
            .ToListAsync();
    }

    private sealed record ParsedImportRow(
        int RowNumber, string Sku, decimal? Rate, int? LeadDays, int? MinQty,
        DateTime? EffectiveFrom, DateTime? EffectiveTo, string? Notes, string? Error);

    // Reads by header name (not fixed column index) so the parser stays resilient to reordered
    // columns; only Variant SKU / Current Rate / Lead Days / Min Qty / Effective From / Effective
    // To / Notes are read back — Product Code, Product Name, Variant Name and Vendor Part No are
    // exported for human reference only, matching is by SKU.
    private static List<ParsedImportRow> ParseImportWorkbook(Stream file)
    {
        using var workbook = new XLWorkbook(file);
        var ws = workbook.Worksheets.First();

        var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        for (var c = 1; c <= lastCol; c++)
        {
            var h = ws.Cell(1, c).GetString().Trim();
            if (!string.IsNullOrEmpty(h)) headerMap[h] = c;
        }

        int Col(string name) => headerMap.TryGetValue(name, out var c) ? c : -1;
        var skuCol     = Col("Variant SKU");
        var rateCol    = Col("Current Rate");
        var leadCol    = Col("Lead Days");
        var minQtyCol  = Col("Min Qty");
        var effFromCol = Col("Effective From");
        var effToCol   = Col("Effective To");
        var notesCol   = Col("Notes");

        var results = new List<ParsedImportRow>();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (var r = 2; r <= lastRow; r++)
        {
            var row = ws.Row(r);
            var sku = skuCol > 0 ? row.Cell(skuCol).GetString().Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(sku)) continue;

            string? error = null;

            decimal? rate = null;
            var rateCell = rateCol > 0 ? row.Cell(rateCol) : null;
            if (rateCell is not null && !rateCell.IsEmpty())
            {
                if (rateCell.TryGetValue(out decimal parsedRate)) rate = parsedRate;
                else error ??= "Rate must be a valid number.";
            }
            else
            {
                error ??= "Rate is required.";
            }
            if (rate is not null && rate <= 0)
                error ??= "Rate must be a positive number.";

            int? leadDays = null;
            if (leadCol > 0 && !row.Cell(leadCol).IsEmpty() && row.Cell(leadCol).TryGetValue(out int parsedLead))
                leadDays = parsedLead;

            int? minQty = null;
            if (minQtyCol > 0 && !row.Cell(minQtyCol).IsEmpty() && row.Cell(minQtyCol).TryGetValue(out int parsedMinQty))
                minQty = parsedMinQty;

            DateTime? effFrom = null;
            if (effFromCol > 0 && !row.Cell(effFromCol).IsEmpty())
            {
                if (row.Cell(effFromCol).TryGetValue(out DateTime parsedFrom)) effFrom = parsedFrom;
                else error ??= "Effective From must be a valid date.";
            }

            DateTime? effTo = null;
            if (effToCol > 0 && !row.Cell(effToCol).IsEmpty())
            {
                if (row.Cell(effToCol).TryGetValue(out DateTime parsedTo)) effTo = parsedTo;
                else error ??= "Effective To must be a valid date.";
            }

            if (effFrom.HasValue && effTo.HasValue && effTo.Value < effFrom.Value)
                error ??= "Effective To must not be before Effective From.";

            var notes = notesCol > 0 ? row.Cell(notesCol).GetString() : null;

            results.Add(new ParsedImportRow(r, sku, rate, leadDays, minQty, effFrom, effTo,
                string.IsNullOrWhiteSpace(notes) ? null : notes, error));
        }

        return results;
    }

    // A new rate card needs a currency and the import file has no column for one, so it comes from
    // the import dialog, then the organization's base currency. Before this, only the base currency
    // was consulted — and with none configured (no screen sets it), every row for a product the
    // supplier had no rate card for was rejected at confirm, after the preview had shown it as a
    // valid "New Link". For a supplier with no rate cards yet, that meant an import created nothing.
    private const string NoCurrencyForNewRow =
        "Choose a currency in the import dialog — this organization has no base currency set, and this row creates a new rate card.";

    private async Task<Guid?> ResolveImportCurrencyAsync(Guid? requested) =>
        requested ?? await _orgCurrency.GetBaseCurrencyIdAsync(_tenantContext.OrganizationId);

    public async Task<List<ImportPreviewRowModel>> PreviewImportAsync(Stream file, Guid supplierId, Guid? currencyId = null)
    {
        var parsedRows = ParseImportWorkbook(file);
        if (parsedRows.Count == 0)
            throw new BadRequestException("The uploaded file has no data rows.");

        var newRowCurrency = await ResolveImportCurrencyAsync(currencyId);

        var skus = parsedRows.Select(r => r.Sku).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var variants = await _db.ProductVariants
            .Where(v => skus.Contains(v.Sku))
            .Select(v => new { v.Id, v.Sku, ProductName = v.Product.Name })
            .ToListAsync();
        var variantsBySku = variants.ToDictionary(v => v.Sku, v => v, StringComparer.OrdinalIgnoreCase);

        var variantIds = variants.Select(v => v.Id).ToList();
        var existing = await _db.VariantSuppliers
            .Where(x => x.SupplierId == supplierId && variantIds.Contains(x.VariantId))
            .Select(x => new { x.VariantId, x.VendorUnitCost })
            .ToListAsync();
        var existingByVariantId = existing.ToDictionary(x => x.VariantId, x => x.VendorUnitCost);

        var results = new List<ImportPreviewRowModel>();
        foreach (var row in parsedRows)
        {
            if (row.Error is not null)
            {
                results.Add(new ImportPreviewRowModel { Row = row.RowNumber, Sku = row.Sku, Error = row.Error });
                continue;
            }

            if (!variantsBySku.TryGetValue(row.Sku, out var variant))
            {
                results.Add(new ImportPreviewRowModel
                {
                    Row = row.RowNumber, Sku = row.Sku, Error = $"SKU '{row.Sku}' not found in the system."
                });
                continue;
            }

            var hasExisting = existingByVariantId.TryGetValue(variant.Id, out var currentRate);

            // The same rule confirm applies, shown now rather than discovered after confirming.
            if (!hasExisting && newRowCurrency is null)
            {
                results.Add(new ImportPreviewRowModel
                {
                    Row = row.RowNumber, Sku = row.Sku, ProductName = variant.ProductName,
                    ImportedRate = row.Rate, NewRecord = true, Error = NoCurrencyForNewRow
                });
                continue;
            }

            results.Add(new ImportPreviewRowModel
            {
                Row          = row.RowNumber,
                Sku          = row.Sku,
                ProductName  = variant.ProductName,
                CurrentRate  = hasExisting ? currentRate : null,
                ImportedRate = row.Rate,
                RateChanged  = hasExisting && currentRate != row.Rate,
                NewRecord    = !hasExisting
            });
        }

        return results;
    }

    public async Task<ImportConfirmResult> ConfirmImportAsync(Stream file, Guid supplierId, int performedBy, Guid? currencyId = null)
    {
        var parsedRows = ParseImportWorkbook(file);
        if (parsedRows.Count == 0)
            throw new BadRequestException("The uploaded file has no data rows.");

        var result = new ImportConfirmResult();

        var skus = parsedRows.Select(r => r.Sku).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var variants = await _db.ProductVariants.Where(v => skus.Contains(v.Sku)).ToListAsync();
        var variantsBySku = variants.ToDictionary(v => v.Sku, v => v, StringComparer.OrdinalIgnoreCase);

        var variantIds = variants.Select(v => v.Id).ToList();
        var existingEntities = await _db.VariantSuppliers
            .Where(x => x.SupplierId == supplierId && variantIds.Contains(x.VariantId))
            .ToListAsync();
        var existingByVariantId = existingEntities.ToDictionary(x => x.VariantId);

        var newRowCurrency = await ResolveImportCurrencyAsync(currencyId);
        var now = DateTime.UtcNow;
        const string importReason = "Excel import";

        foreach (var row in parsedRows)
        {
            if (row.Error is not null)
            {
                result.Errors.Add(new ImportRowError { Row = row.RowNumber, Sku = row.Sku, Message = row.Error });
                continue;
            }

            if (!variantsBySku.TryGetValue(row.Sku, out var variant))
            {
                result.Errors.Add(new ImportRowError
                {
                    Row = row.RowNumber, Sku = row.Sku, Message = $"SKU '{row.Sku}' not found in the system."
                });
                continue;
            }

            if (existingByVariantId.TryGetValue(variant.Id, out var entity))
            {
                // Blanks in the sheet mean "no change" — never silently null out a field a
                // re-import didn't actually carry a value for.
                var newRate    = row.Rate!.Value;
                var newLead    = row.LeadDays ?? entity.LeadTimeDays;
                var newMinQty  = row.MinQty ?? entity.MinOrderQty;
                var newEffFrom = row.EffectiveFrom ?? entity.EffectiveFrom;
                var newEffTo   = row.EffectiveTo ?? entity.EffectiveTo;
                var newNotes   = row.Notes ?? entity.Notes;

                CaptureImportFieldChange(entity.Id, nameof(VariantSupplier.VendorUnitCost), entity.VendorUnitCost.ToString(), newRate.ToString(), importReason, performedBy, now);
                CaptureImportFieldChange(entity.Id, nameof(VariantSupplier.LeadTimeDays), entity.LeadTimeDays?.ToString(), newLead?.ToString(), importReason, performedBy, now);
                CaptureImportFieldChange(entity.Id, nameof(VariantSupplier.MinOrderQty), entity.MinOrderQty?.ToString(), newMinQty?.ToString(), importReason, performedBy, now);
                CaptureImportFieldChange(entity.Id, nameof(VariantSupplier.EffectiveFrom), entity.EffectiveFrom.ToString("O"), newEffFrom.ToString("O"), importReason, performedBy, now);
                CaptureImportFieldChange(entity.Id, nameof(VariantSupplier.EffectiveTo), entity.EffectiveTo?.ToString("O"), newEffTo?.ToString("O"), importReason, performedBy, now);
                CaptureImportFieldChange(entity.Id, nameof(VariantSupplier.Notes), entity.Notes, newNotes, importReason, performedBy, now);

                entity.VendorUnitCost = newRate;
                entity.LeadTimeDays   = newLead;
                entity.MinOrderQty    = newMinQty;
                entity.EffectiveFrom  = newEffFrom;
                entity.EffectiveTo    = newEffTo;
                entity.Notes          = newNotes;
                entity.ModifiedBy     = performedBy;
                entity.ModifiedDate   = now;

                result.UpdatedCount++;
            }
            else
            {
                if (newRowCurrency is null)
                {
                    result.Errors.Add(new ImportRowError
                    {
                        Row = row.RowNumber, Sku = row.Sku, Message = NoCurrencyForNewRow
                    });
                    continue;
                }

                _db.VariantSuppliers.Add(new VariantSupplier
                {
                    VariantId      = variant.Id,
                    SupplierId     = supplierId,
                    VendorUnitCost = row.Rate!.Value,
                    LeadTimeDays   = row.LeadDays,
                    MinOrderQty    = row.MinQty,
                    EffectiveFrom  = row.EffectiveFrom?.Date ?? now.Date,
                    EffectiveTo    = row.EffectiveTo,
                    CurrencyId     = newRowCurrency.Value,
                    Notes          = row.Notes,
                    CreatedBy      = performedBy,
                    CreatedDate    = now
                });
                result.CreatedCount++;
            }
        }

        await _db.SaveChangesAsync();
        return result;
    }

    private void CaptureImportFieldChange(
        int variantSupplierId, string field, string? oldValue, string? newValue,
        string changeReason, int changedBy, DateTime changedAt)
    {
        if (oldValue == newValue) return;
        _db.SupplierRateHistories.Add(new SupplierRateHistory
        {
            VariantSupplierId = variantSupplierId,
            FieldChanged      = field,
            OldValue          = oldValue,
            NewValue          = newValue,
            ChangeReason      = changeReason,
            ChangedBy         = changedBy,
            ChangedAt         = changedAt
        });
    }

    // ── RC-006: Copy Rates Between Suppliers ─────────────────────────────────

    private async Task<List<CopyPreviewRowModel>> BuildCopyRowsAsync(CopyRatesRequest req)
    {
        if (req.SourceSupplierId == req.TargetSupplierId)
            throw new BadRequestException("Source and target supplier must be different.");

        var sourceRows = await _db.VariantSuppliers
            .Where(x => x.SupplierId == req.SourceSupplierId && x.IsActive)
            .Select(x => new
            {
                x.Uuid,
                x.VariantId,
                ProductName = x.Variant.Product.Name,
                VariantName = x.Variant.VariantName,
                Sku         = x.Variant.Sku,
                x.VendorUnitCost
            })
            .ToListAsync();

        if (sourceRows.Count == 0) return [];

        var variantIds = sourceRows.Select(r => r.VariantId).Distinct().ToList();
        var targetLinkedVariantIds = await _db.VariantSuppliers
            .Where(x => x.SupplierId == req.TargetSupplierId && variantIds.Contains(x.VariantId))
            .Select(x => x.VariantId)
            .ToListAsync();
        var targetLinkedSet = targetLinkedVariantIds.ToHashSet();

        return sourceRows.Select(r =>
        {
            var willSkip = targetLinkedSet.Contains(r.VariantId);
            return new CopyPreviewRowModel
            {
                VariantSupplierId = r.Uuid,
                ProductName       = r.ProductName,
                VariantName       = r.VariantName,
                Sku               = r.Sku,
                SourceRate        = r.VendorUnitCost,
                AdjustedRate      = Math.Round(r.VendorUnitCost * (1 + req.AdjustmentPct / 100m), 2),
                WillSkip          = willSkip,
                SkipReason        = willSkip ? "Target supplier already has a rate link for this variant." : null
            };
        }).ToList();
    }

    public Task<List<CopyPreviewRowModel>> PreviewCopyAsync(CopyRatesRequest req) => BuildCopyRowsAsync(req);

    public async Task<CopyConfirmResult> ConfirmCopyAsync(CopyRatesRequest req, int performedBy)
    {
        var rows = await BuildCopyRowsAsync(req);
        var toCreate = rows.Where(r => !r.WillSkip).ToList();
        var skippedCount = rows.Count(r => r.WillSkip);

        if (toCreate.Count == 0)
            return new CopyConfirmResult { CreatedCount = 0, SkippedCount = skippedCount };

        var sourceIds = toCreate.Select(r => r.VariantSupplierId).ToList();
        var sourceEntities = await _db.VariantSuppliers
            .Where(x => sourceIds.Contains(x.Uuid))
            .ToDictionaryAsync(x => x.Uuid);

        var now = DateTime.UtcNow;
        foreach (var row in toCreate)
        {
            if (!sourceEntities.TryGetValue(row.VariantSupplierId, out var source)) continue;

            _db.VariantSuppliers.Add(new VariantSupplier
            {
                VariantId      = source.VariantId,
                SupplierId     = req.TargetSupplierId,
                VendorUnitCost = row.AdjustedRate,
                LeadTimeDays   = source.LeadTimeDays,
                MinOrderQty    = source.MinOrderQty,
                MinOrderValue  = source.MinOrderValue,
                DiscountTiers  = source.DiscountTiers,
                CurrencyId     = source.CurrencyId,
                EffectiveFrom  = now.Date,
                CreatedBy      = performedBy,
                CreatedDate    = now
            });
        }

        await _db.SaveChangesAsync();
        return new CopyConfirmResult { CreatedCount = toCreate.Count, SkippedCount = skippedCount };
    }
}
