using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Services;

internal sealed class PricingRuleService : IPricingRuleService
{
    private readonly InventoryDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IOrganizationCurrencyService _orgCurrency;

    private static readonly HashSet<string> ValidPriceTypes =
    [
        PricingRuleType.Selling, PricingRuleType.Cost, PricingRuleType.Promotional, PricingRuleType.Contract
    ];

    public PricingRuleService(InventoryDbContext db, ITenantContext tenantContext, IOrganizationCurrencyService orgCurrency)
    {
        _db            = db;
        _tenantContext = tenantContext;
        _orgCurrency   = orgCurrency;
    }

    public async Task<Guid> CreateAsync(CreatePricingRuleRequest req, int createdBy)
    {
        var variant = await _db.ProductVariants.FirstOrDefaultAsync(v => v.Uuid == req.VariantUuid)
            ?? throw new NotFoundException("ProductVariant", req.VariantUuid);

        ValidatePriceType(req.PriceType);
        // A CONTRACT rate that names no partner could never resolve (IPricingService skips tier 1
        // entirely when the caller passes no partner) — refuse it here rather than accept a row
        // that can never apply.
        if (req.PriceType == PricingRuleType.Contract && req.PartnerUuid is null)
            throw new BadRequestException("A CONTRACT price requires a partner.");
        ValidateQtyAndDateRange(req.MinQty, req.MaxQty, req.EffectiveFrom, req.EffectiveTo);

        var currencyId = req.CurrencyId
            ?? await _orgCurrency.GetBaseCurrencyIdAsync(_tenantContext.OrganizationId)
            ?? throw new BadRequestException(
                "Currency is required — this organization has no base currency configured, so it must be supplied explicitly.");

        var entity = new PricingRule
        {
            VariantId     = variant.Id,
            PartnerId     = req.PartnerUuid,
            PriceType     = req.PriceType,
            MinQty        = req.MinQty,
            MaxQty        = req.MaxQty,
            UnitPrice     = req.UnitPrice,
            CurrencyId    = currencyId,
            EffectiveFrom = req.EffectiveFrom?.Date ?? DateTime.UtcNow.Date,
            EffectiveTo   = req.EffectiveTo,
            IsActive      = true,
            CreatedBy     = createdBy,
            CreatedDate   = DateTime.UtcNow
        };

        _db.PricingRules.Add(entity);
        await _db.SaveChangesAsync();
        return entity.Uuid;
    }

    public async Task<bool> UpdateAsync(Guid uuid, UpdatePricingRuleRequest req)
    {
        var entity = await _db.PricingRules.FirstOrDefaultAsync(x => x.Uuid == uuid);
        if (entity is null) return false;

        ValidatePriceType(req.PriceType);
        if (req.PriceType == PricingRuleType.Contract && entity.PartnerId is null)
            throw new BadRequestException("A CONTRACT price requires a partner.");
        ValidateQtyAndDateRange(req.MinQty, req.MaxQty, req.EffectiveFrom, req.EffectiveTo);

        entity.PriceType     = req.PriceType;
        entity.MinQty        = req.MinQty;
        entity.MaxQty        = req.MaxQty;
        entity.UnitPrice     = req.UnitPrice;
        entity.CurrencyId    = req.CurrencyId;
        entity.EffectiveFrom = req.EffectiveFrom.Date;
        entity.EffectiveTo   = req.EffectiveTo;
        entity.IsActive      = req.IsActive;

        await _db.SaveChangesAsync();
        return true;
    }

    // Soft delete, matching ProductsController.DeleteProduct — a rule can be referenced by past
    // price-resolution audit trails, so the row stays; it just stops matching.
    public async Task<bool> DeleteAsync(Guid uuid)
    {
        var entity = await _db.PricingRules.FirstOrDefaultAsync(x => x.Uuid == uuid);
        if (entity is null) return false;

        entity.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<PricingRuleModel?> GetByIdAsync(Guid uuid) =>
        await ProjectedQuery().FirstOrDefaultAsync(x => x.Uuid == uuid);

    public async Task<PaginatedResponse<PricingRuleModel>> GetListAsync(PricingRuleListFilter filter)
    {
        var query = ProjectedQuery();

        if (filter.VariantUuid is { } variantUuid)
            query = query.Where(x => x.VariantUuid == variantUuid);
        if (filter.PartnerUuid is { } partnerUuid)
            query = query.Where(x => x.PartnerUuid == partnerUuid);
        if (!string.IsNullOrWhiteSpace(filter.PriceType))
            query = query.Where(x => x.PriceType == filter.PriceType);
        if (filter.IsActive is { } isActive)
            query = query.Where(x => x.IsActive == isActive);

        query = query.OrderByDescending(x => x.CreatedDate);

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var data = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return new PaginatedResponse<PricingRuleModel>
        {
            Data         = data,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    private IQueryable<PricingRuleModel> ProjectedQuery() =>
        _db.PricingRules.AsNoTracking().Select(x => new PricingRuleModel
        {
            Uuid          = x.Uuid,
            VariantUuid   = x.Variant.Uuid,
            VariantSku    = x.Variant.Sku,
            ProductName   = x.Variant.Product.Name,
            PartnerUuid   = x.PartnerId,
            PriceType     = x.PriceType,
            MinQty        = x.MinQty,
            MaxQty        = x.MaxQty,
            UnitPrice     = x.UnitPrice,
            CurrencyId    = x.CurrencyId,
            EffectiveFrom = x.EffectiveFrom,
            EffectiveTo   = x.EffectiveTo,
            IsActive      = x.IsActive,
            CreatedDate   = x.CreatedDate
        });

    private static void ValidatePriceType(string priceType)
    {
        if (!ValidPriceTypes.Contains(priceType))
            throw new BadRequestException(
                $"'{priceType}' is not a valid price type. Expected one of: {string.Join(", ", ValidPriceTypes)}.");
    }

    private static void ValidateQtyAndDateRange(decimal? minQty, decimal? maxQty, DateTime? from, DateTime? to)
    {
        if (minQty is { } min && maxQty is { } max && min > max)
            throw new BadRequestException("Minimum quantity cannot be greater than maximum quantity.");
        if (from is { } f && to is { } t && f > t)
            throw new BadRequestException("Effective-from date cannot be after effective-to date.");
    }
}
