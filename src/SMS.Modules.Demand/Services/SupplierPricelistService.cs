using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Services;

/// <summary>A saved pricelist row plus the field-level changes the controller writes to the audit log.</summary>
public sealed record PricelistSaveOutcome(PricelistSaveResult Result, IReadOnlyList<PoFieldChange> Changes);

/// <summary>A45 C1/C4/C6 — CRUD of the organization's supplier pricelists (docs/pricelist/ADDENDUM-45-ANALYSIS.md Part B).</summary>
public interface ISupplierPricelistService
{
    Task<PaginatedResponse<PricelistItem>> GetListAsync(SupplierPricelistFilter filter, CancellationToken ct = default);
    Task<PricelistItem?> GetByIdAsync(Guid uuid, CancellationToken ct = default);
    Task<PricelistSaveOutcome> CreateAsync(CreateSupplierPricelistRequest req, int userId, CancellationToken ct = default);
    Task<PricelistSaveOutcome> UpdateAsync(Guid uuid, UpdateSupplierPricelistRequest req, int userId, CancellationToken ct = default);
    /// <summary>Soft delete (BR-16). Null when there is no active row with this uuid (unknown, another organization's, or already inactive).</summary>
    Task<PricelistItem?> DeactivateAsync(Guid uuid, int userId, CancellationToken ct = default);
}

/// <summary>
/// Every query filters explicitly on the caller's own organization (the EF tenant filter is off for super admins, and another
/// organization's row must read as absent to them too — same rule as the rejection reasons). Cross-module checks (product,
/// variant, vendor, currency) use the optional services Demand already uses elsewhere and are skipped when a service is absent.
/// </summary>
internal sealed class SupplierPricelistService : ISupplierPricelistService
{
    internal const int MaxPageSize = 200;
    private const decimal MaxMoney = 9_999_999_999_999m;

    private readonly DemandDbContext _db;
    private readonly InventoryDbContext? _inv;
    private readonly IPartnerRoleLookup? _partners;
    private readonly ISupplierNameLookupService? _supplierNames;
    private readonly ICurrencyCodeLookup? _codes;
    private readonly IOrgCurrencyLookup? _orgCurrencies;
    private readonly IOrganizationCurrencyService? _orgCurrency;

    public SupplierPricelistService(
        DemandDbContext db, InventoryDbContext? inv = null, IPartnerRoleLookup? partners = null,
        ISupplierNameLookupService? supplierNames = null, ICurrencyCodeLookup? codes = null,
        IOrgCurrencyLookup? orgCurrencies = null, IOrganizationCurrencyService? orgCurrency = null)
    {
        _db            = db;
        _inv           = inv;
        _partners      = partners;
        _supplierNames = supplierNames;
        _codes         = codes;
        _orgCurrencies = orgCurrencies;
        _orgCurrency   = orgCurrency;
    }

    private Guid Org => _db.TenantContext.OrganizationId;

    private IQueryable<SupplierPricelist> Own() =>
        _db.SupplierPricelists.IgnoreQueryFilters().Where(r => r.OrganizationId == Org);

    // ── Reads ────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<PricelistItem>> GetListAsync(SupplierPricelistFilter filter, CancellationToken ct = default)
    {
        var query = Own().AsNoTracking();
        if (filter.ActiveOnly)               query = query.Where(r => r.IsActive);
        if (filter.ProductUuid is { } p)     query = query.Where(r => r.ProductUuid == p);
        if (filter.VariantUuid is { } v)     query = query.Where(r => r.VariantUuid == v);
        if (filter.SupplierUuid is { } s)    query = query.Where(r => r.SupplierUuid == s);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            var lower = term.ToLowerInvariant();

            // Product / variant names live in Inventory and supplier names in Suppliers, so resolve what the term matches there
            // first and then filter this table by the resulting UUIDs.
            var productUuids = new List<Guid>();
            var variantUuids = new List<Guid>();
            if (_inv is not null)
            {
                productUuids = await _inv.Products.AsNoTracking()
                    .Where(x => x.Name.ToLower().Contains(lower) || x.Sku.ToLower().Contains(lower))
                    .Select(x => x.Uuid).Take(500).ToListAsync(ct);
                variantUuids = await _inv.ProductVariants.AsNoTracking()
                    .Where(x => x.Sku.ToLower().Contains(lower) || x.VariantName.ToLower().Contains(lower))
                    .Select(x => x.Uuid).Take(500).ToListAsync(ct);
            }

            var supplierUuids = new List<Guid>();
            if (_supplierNames is not null)
            {
                var distinct = await Own().Select(r => r.SupplierUuid).Distinct().ToListAsync(ct);
                if (distinct.Count > 0)
                {
                    var names = await _supplierNames.GetNamesAsync(distinct);
                    supplierUuids = names.Where(kv => kv.Value.Contains(term, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
                }
            }

            query = query.Where(r =>
                (r.VendorProductCode != null && r.VendorProductCode.ToLower().Contains(lower))
                || (r.VendorProductName != null && r.VendorProductName.ToLower().Contains(lower))
                || (r.Notes != null && r.Notes.ToLower().Contains(lower))
                || productUuids.Contains(r.ProductUuid)
                || (r.VariantUuid != null && variantUuids.Contains(r.VariantUuid.Value))
                || supplierUuids.Contains(r.SupplierUuid));
        }

        var total    = await query.CountAsync(ct);
        var pageSize = Math.Clamp(filter.PageSize, 1, MaxPageSize);
        var page     = Math.Max(1, filter.Page);

        var rows = await query
            .OrderBy(r => r.Priority).ThenByDescending(r => r.ValidFrom).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        return new PaginatedResponse<PricelistItem>
        {
            Data         = await ToItemsAsync(rows, ct),
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<PricelistItem?> GetByIdAsync(Guid uuid, CancellationToken ct = default)
    {
        var row = await Own().AsNoTracking().FirstOrDefaultAsync(r => r.UUID == uuid, ct);
        return row is null ? null : (await ToItemsAsync([row], ct))[0];
    }

    // ── Writes ───────────────────────────────────────────────────────────────

    public async Task<PricelistSaveOutcome> CreateAsync(CreateSupplierPricelistRequest req, int userId, CancellationToken ct = default)
    {
        await ValidateAsync(req, ct);
        var from = req.ValidFrom.Date;
        var to   = req.ValidTo.Date;

        var active = req.IsActive ?? true;
        if (active) await RefuseDuplicateAsync(null, req, from, to, ct);

        var row = new SupplierPricelist
        {
            UUID              = Guid.NewGuid(),
            OrganizationId    = Org,
            CreatedBy         = userId,
            CreatedDate       = DateTime.UtcNow,
            IsActive          = active
        };
        Apply(row, req, from, to);
        _db.SupplierPricelists.Add(row);

        var warning = active ? await OverlapWarningAsync(row, ct) : null;
        await SaveAsync(ct);
        return new PricelistSaveOutcome(await ResultAsync(row, warning, ct), []);
    }

    public async Task<PricelistSaveOutcome> UpdateAsync(Guid uuid, UpdateSupplierPricelistRequest req, int userId, CancellationToken ct = default)
    {
        var row = await Own().FirstOrDefaultAsync(r => r.UUID == uuid, ct)
            ?? throw new NotFoundException("SupplierPricelist", uuid);

        var supplied = ParseRowVersion(req.RowVersion);
        if (!row.RowVersion.AsSpan().SequenceEqual(supplied))
            throw new ConflictException(StaleMessage);

        await ValidateAsync(req, ct);
        var from = req.ValidFrom.Date;
        var to   = req.ValidTo.Date;
        var nextActive = req.IsActive ?? row.IsActive;

        // Only an active row can collide with another active row.
        if (nextActive) await RefuseDuplicateAsync(row.Id, req, from, to, ct);

        var before = Snapshot(row);
        _db.Entry(row).Property(r => r.RowVersion).OriginalValue = supplied;
        Apply(row, req, from, to);
        row.IsActive     = nextActive;
        row.ModifiedBy   = userId;
        row.ModifiedDate = DateTime.UtcNow;

        var warning = nextActive ? await OverlapWarningAsync(row, ct) : null;
        await SaveAsync(ct);
        return new PricelistSaveOutcome(await ResultAsync(row, warning, ct), Diff(before, Snapshot(row)));
    }

    public async Task<PricelistItem?> DeactivateAsync(Guid uuid, int userId, CancellationToken ct = default)
    {
        var row = await Own().FirstOrDefaultAsync(r => r.UUID == uuid && r.IsActive, ct);
        if (row is null) return null;

        row.IsActive     = false;
        row.ModifiedBy   = userId;
        row.ModifiedDate = DateTime.UtcNow;
        await SaveAsync(ct);
        return (await ToItemsAsync([row], ct))[0];
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private const string StaleMessage = "This price record was changed by someone else. Reload it and try again.";

    private static byte[] ParseRowVersion(string? rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion))
            throw new BadRequestException("The record's rowVersion is required so a concurrent change is not overwritten.");
        try { return Convert.FromBase64String(rowVersion.Trim()); }
        catch (FormatException) { throw new BadRequestException("rowVersion is not a valid value. Reload the record and try again."); }
    }

    private async Task ValidateAsync(SupplierPricelistWriteRequest req, CancellationToken ct)
    {
        if (req.ProductUuid == Guid.Empty)  throw new BadRequestException("A product is required.");
        if (req.SupplierUuid == Guid.Empty) throw new BadRequestException("A vendor is required.");
        if (req.CurrencyId == Guid.Empty)   throw new BadRequestException("A currency is required.");
        if (req.UnitPrice <= 0m)            throw new BadRequestException("Unit price must be greater than zero.");
        if (req.UnitPrice > MaxMoney)       throw new BadRequestException("Unit price is too large.");
        if (req.MinQuantity < 0m)           throw new BadRequestException("Minimum quantity must be zero or greater.");
        if (req.MinQuantity > MaxMoney)     throw new BadRequestException("Minimum quantity is too large.");
        if (req.ValidFrom == default || req.ValidTo == default)
            throw new BadRequestException("Valid from and valid to dates are required.");
        if (req.ValidTo.Date < req.ValidFrom.Date)
            throw new BadRequestException("Valid to must be on or after valid from.");
        if (req.PricingDateBasis is < 0 or > 1)
            throw new BadRequestException("Pricing date basis must be 0 (order date) or 1 (delivery date).");
        if (req.LeadTimeDays is < 0)        throw new BadRequestException("Lead time must be zero or more days.");
        if (req.LeadTimeDays > 3650)        throw new BadRequestException("Lead time must not exceed 3650 days.");
        if (req.Priority < 0)               throw new BadRequestException("Priority must be zero or greater (0 is the highest priority).");
        if (req.VendorProductCode?.Trim().Length > 100) throw new BadRequestException("Vendor product code must not exceed 100 characters.");
        if (req.VendorProductName?.Trim().Length > 255) throw new BadRequestException("Vendor product name must not exceed 255 characters.");
        if (req.Notes?.Trim().Length > 500)             throw new BadRequestException("Notes must not exceed 500 characters.");

        if (_inv is not null)
        {
            var product = await _inv.Products.AsNoTracking().Where(p => p.Uuid == req.ProductUuid)
                .Select(p => new { p.Uuid, p.Name }).FirstOrDefaultAsync(ct);
            if (product is null) throw new BadRequestException("The selected product was not found.");

            if (req.VariantUuid is { } variantUuid)
            {
                var belongs = await _inv.ProductVariants.AsNoTracking()
                    .AnyAsync(v => v.Uuid == variantUuid && v.Product.Uuid == req.ProductUuid, ct);
                if (!belongs) throw new BadRequestException("The selected variant was not found for this product.");
            }
        }

        if (_partners is not null)
        {
            var partner = await _partners.GetAsync(req.SupplierUuid, ct);
            if (partner is null)           throw new BadRequestException("The selected vendor was not found.");
            if (!partner.IsVendor)         throw new BadRequestException($"{partner.Name} is not a vendor.");
        }

        if (_codes is not null)
        {
            var code = await _codes.GetCodeAsync(req.CurrencyId, ct);
            if (code is null) throw new BadRequestException("The selected currency was not found.");
            // A price can be agreed in any active currency of the organization (the organization's own base is always fine).
            await DemandCurrency.RequireActiveOrgCurrencyAsync(_orgCurrencies, _orgCurrency, Org, req.CurrencyId, TransactionDomain.Purchase, code);
        }
    }

    private static void Apply(SupplierPricelist row, SupplierPricelistWriteRequest req, DateTime from, DateTime to)
    {
        row.ProductUuid       = req.ProductUuid;
        row.VariantUuid       = req.VariantUuid;
        row.SupplierUuid      = req.SupplierUuid;
        row.VendorProductCode = Clean(req.VendorProductCode);
        row.VendorProductName = Clean(req.VendorProductName);
        row.MinQuantity       = req.MinQuantity;
        row.UnitPrice         = req.UnitPrice;
        row.CurrencyId        = req.CurrencyId;
        row.PricingDateBasis  = (PricingDateBasis)req.PricingDateBasis;
        row.ValidFrom         = from;
        row.ValidTo           = to;
        row.LeadTimeDays      = req.LeadTimeDays;
        row.Priority          = req.Priority;
        row.Notes             = Clean(req.Notes);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>An exact duplicate of an active row (same product, variant, vendor, quantity break and period) is refused (409).</summary>
    private async Task RefuseDuplicateAsync(int? ownId, SupplierPricelistWriteRequest req, DateTime from, DateTime to, CancellationToken ct)
    {
        var exists = await Own().AnyAsync(r => r.IsActive && r.Id != (ownId ?? 0)
            && r.ProductUuid == req.ProductUuid && r.VariantUuid == req.VariantUuid && r.SupplierUuid == req.SupplierUuid
            && r.MinQuantity == req.MinQuantity && r.ValidFrom == from && r.ValidTo == to, ct);
        if (exists)
            throw new ConflictException(
                "An active price record for this product, vendor, minimum quantity and validity period already exists. Edit that record instead.");
    }

    /// <summary>C6/BR-11 — another active row for the same product, variant, vendor and minimum quantity whose dates overlap. A warning, never a block.</summary>
    private async Task<string?> OverlapWarningAsync(SupplierPricelist row, CancellationToken ct)
    {
        var other = await Own().AsNoTracking()
            .Where(r => r.IsActive && r.Id != row.Id
                && r.ProductUuid == row.ProductUuid && r.VariantUuid == row.VariantUuid && r.SupplierUuid == row.SupplierUuid
                && r.MinQuantity == row.MinQuantity && r.ValidFrom <= row.ValidTo && r.ValidTo >= row.ValidFrom)
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .FirstOrDefaultAsync(ct);
        return other is null ? null
            : $"Another price record (Priority {other.Priority}, Valid {Day(other.ValidFrom)} to {Day(other.ValidTo)}) overlaps with this date range. " +
              "The record with the lowest priority number will be selected during price resolution.";
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException(StaleMessage); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UQ_SupplierPricelists_NoDuplicate", StringComparison.Ordinal) == true)
        {
            // Two saves of the same row at once: the unique index is what holds under the race.
            throw new ConflictException(
                "An active price record for this product, vendor, minimum quantity and validity period already exists. Edit that record instead.");
        }
    }

    // ── Mapping ──────────────────────────────────────────────────────────────

    private static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<PricelistSaveResult> ResultAsync(SupplierPricelist row, string? warning, CancellationToken ct) =>
        new() { Item = (await ToItemsAsync([row], ct))[0], OverlapWarning = warning };

    private async Task<List<PricelistItem>> ToItemsAsync(IReadOnlyList<SupplierPricelist> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var products = new Dictionary<Guid, (string Name, string Sku)>();
        var variants = new Dictionary<Guid, string>();
        if (_inv is not null)
        {
            var productIds = rows.Select(r => r.ProductUuid).Distinct().ToList();
            var variantIds = rows.Where(r => r.VariantUuid != null).Select(r => r.VariantUuid!.Value).Distinct().ToList();
            products = (await _inv.Products.AsNoTracking().Where(p => productIds.Contains(p.Uuid))
                .Select(p => new { p.Uuid, p.Name, p.Sku }).ToListAsync(ct)).ToDictionary(p => p.Uuid, p => (p.Name, p.Sku));
            if (variantIds.Count > 0)
                variants = (await _inv.ProductVariants.AsNoTracking().Where(v => variantIds.Contains(v.Uuid))
                    .Select(v => new { v.Uuid, v.VariantName }).ToListAsync(ct)).ToDictionary(v => v.Uuid, v => v.VariantName);
        }

        IReadOnlyDictionary<Guid, string> suppliers = new Dictionary<Guid, string>();
        if (_supplierNames is not null)
            suppliers = await _supplierNames.GetNamesAsync(rows.Select(r => r.SupplierUuid).Distinct().ToList());

        var codes = new Dictionary<Guid, string?>();
        if (_codes is not null)
            foreach (var id in rows.Select(r => r.CurrencyId).Distinct())
                codes[id] = (await _codes.GetCodeAsync(id, ct))?.Trim();

        return rows.Select(r => new PricelistItem
        {
            Uuid                  = r.UUID,
            ProductUuid           = r.ProductUuid,
            ProductName           = products.TryGetValue(r.ProductUuid, out var pr) ? pr.Name : null,
            ProductSku            = products.TryGetValue(r.ProductUuid, out var ps) ? ps.Sku : null,
            VariantUuid           = r.VariantUuid,
            VariantName           = r.VariantUuid is { } vu && variants.TryGetValue(vu, out var vn) ? vn : null,
            SupplierUuid          = r.SupplierUuid,
            SupplierName          = suppliers.TryGetValue(r.SupplierUuid, out var sn) ? sn : null,
            VendorProductCode     = r.VendorProductCode,
            VendorProductName     = r.VendorProductName,
            MinQuantity           = r.MinQuantity,
            UnitPrice             = r.UnitPrice,
            CurrencyId            = r.CurrencyId,
            CurrencyCode          = codes.TryGetValue(r.CurrencyId, out var cc) ? cc : null,
            PricingDateBasis      = (int)r.PricingDateBasis,
            PricingDateBasisLabel = r.PricingDateBasis == PricingDateBasis.DeliveryDate ? "Delivery Date" : "Order Date",
            ValidFrom             = Day(r.ValidFrom),
            ValidTo               = Day(r.ValidTo),
            LeadTimeDays          = r.LeadTimeDays,
            Priority              = r.Priority,
            Notes                 = r.Notes,
            IsActive              = r.IsActive,
            CreatedAt             = DateTime.SpecifyKind(r.CreatedDate, DateTimeKind.Utc),
            ModifiedAt            = r.ModifiedDate is { } m ? DateTime.SpecifyKind(m, DateTimeKind.Utc) : null,
            RowVersion            = Convert.ToBase64String(r.RowVersion ?? [])
        }).ToList();
    }

    // ── Audit diff ───────────────────────────────────────────────────────────

    private static Dictionary<string, string?> Snapshot(SupplierPricelist r) => new()
    {
        ["Product"]             = r.ProductUuid.ToString(),
        ["Variant"]             = r.VariantUuid?.ToString(),
        ["Vendor"]              = r.SupplierUuid.ToString(),
        ["Vendor product code"] = r.VendorProductCode,
        ["Vendor product name"] = r.VendorProductName,
        ["Minimum quantity"]    = r.MinQuantity.ToString("0.####", CultureInfo.InvariantCulture),
        ["Unit price"]          = r.UnitPrice.ToString("0.####", CultureInfo.InvariantCulture),
        ["Currency"]            = r.CurrencyId.ToString(),
        ["Pricing date basis"]  = r.PricingDateBasis.ToString(),
        ["Valid from"]          = Day(r.ValidFrom),
        ["Valid to"]            = Day(r.ValidTo),
        ["Lead time days"]      = r.LeadTimeDays?.ToString(CultureInfo.InvariantCulture),
        ["Priority"]            = r.Priority.ToString(CultureInfo.InvariantCulture),
        ["Notes"]               = r.Notes,
        ["Active"]              = r.IsActive ? "true" : "false"
    };

    private static IReadOnlyList<PoFieldChange> Diff(Dictionary<string, string?> before, Dictionary<string, string?> after) =>
        after.Where(kv => !string.Equals(before[kv.Key], kv.Value, StringComparison.Ordinal))
             .Select(kv => new PoFieldChange(kv.Key, before[kv.Key], kv.Value))
             .ToList();
}
