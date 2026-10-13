using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using static SMS.Modules.Inventory.Import.ImportValues;

namespace SMS.Modules.Inventory.Import;

/// <summary>
/// A40 D-6 — PRICING_RULE: a price list item becomes a <see cref="PricingRule"/> (sku, customer_code, price_type
/// SELLING/PROMOTIONAL/CONTRACT, min_qty, price, currency_code, valid_from, valid_to). sku is a variant SKU, or a product SKU
/// (→ its default variant). customer_code is a customer's code (C-00001), read through Suppliers' shared customer source;
/// CONTRACT needs one (same rule as PricingRuleService). currency_code must be one of the organization's currencies;
/// empty = its sale base currency. Match = an active rule of the same variant, customer, price type and min qty starting on
/// valid_from (no valid_from: the latest such rule).
/// </summary>
internal sealed class PricingRuleImportTarget : IImportTarget
{
    private const int Chunk = 1000;
    private static readonly string[] PriceTypes = [PricingRuleType.Selling, PricingRuleType.Promotional, PricingRuleType.Contract];

    private readonly InventoryDbContext _db;
    private readonly IPosCustomerSource? _customers;
    private readonly IOrgCurrencyLookup? _currencies;
    private readonly IOrganizationCurrencyService? _orgCurrency;

    public PricingRuleImportTarget(
        InventoryDbContext db, IPosCustomerSource? customers = null, IOrgCurrencyLookup? currencies = null,
        IOrganizationCurrencyService? orgCurrency = null)
    {
        _db          = db;
        _customers   = customers;
        _currencies  = currencies;
        _orgCurrency = orgCurrency;
    }

    public ImportProfileDefinition Describe() => new(
        ImportEntityTypes.PricingRule, "Price list (pricing rules)",
        "Selling, promotional and customer contract prices per product variant, optionally by quantity band and validity dates.",
        ImportOrder: 10, IsOpeningBalance: false, MatchFields: ["sku", "customer_code", "price_type", "min_qty", "valid_from"],
        Fields:
        [
            new("sku", "SKU", "Variant SKU, or a product SKU for its default variant.", ImportFieldTypes.Lookup, true, true, 1, "CAB-4MM",
                MaxLength: 50, LookupList: ImportListKeys.Skus, Aliases: ["product_code", "item_code", "material", "product_sku"]),
            new("customer_code", "Customer Code", "Empty = a price for every customer.", ImportFieldTypes.Text, false, true, 2, null,
                MaxLength: 30, Aliases: ["customer", "customer number", "partner_code"]),
            new("price_type", "Price Type", "SELLING, PROMOTIONAL or CONTRACT (CONTRACT needs a customer).", ImportFieldTypes.Lookup,
                true, true, 3, "SELLING", MaxLength: 20, LookupList: ImportListKeys.PriceTypes, Aliases: ["type", "condition type"]),
            new("min_qty", "Min Quantity", null, ImportFieldTypes.Decimal, false, true, 4, null, MinValue: 0m, DecimalPlaces: 4,
                Aliases: ["minimum quantity", "scale quantity", "qty_from"]),
            new("price", "Price", null, ImportFieldTypes.Decimal, true, false, 5, "150.00", MinValue: 0m, DecimalPlaces: 4,
                Aliases: ["unit_price", "unit price", "amount", "rate"]),
            new("currency_code", "Currency", "Empty = the organization's sale base currency.", ImportFieldTypes.Currency, false, false, 6,
                "PKR", MaxLength: 10, LookupList: ImportListKeys.Currencies, Aliases: ["currency"]),
            new("valid_from", "Valid From", "Empty = today.", ImportFieldTypes.Date, false, true, 7, "2026-01-01",
                Aliases: ["effective_from", "start date", "valid from"]),
            new("valid_to", "Valid To", null, ImportFieldTypes.Date, false, false, 8, null, Aliases: ["effective_to", "end date", "valid to"])
        ]);

    public async Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var skus = await _db.ProductVariants.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.OrganizationId == organizationId && v.IsActive && v.Product.IsActive && v.Product.IsSaleable)
            .OrderBy(v => v.Sku).Select(v => v.Sku).Take(ImportListKeys.MaxValues).ToListAsync(ct);
        var currencies = _currencies is null ? [] : (await _currencies.ListAsync(organizationId, activeOnly: true, ct))
            .OrderBy(c => c.DisplayOrder).Select(c => c.Code).ToList();
        return [new(ImportListKeys.Skus, skus), new(ImportListKeys.PriceTypes, PriceTypes), new(ImportListKeys.Currencies, currencies)];
    }

    public async Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default) =>
        (await CheckAsync(organizationId, rows.Select(r => (r.RowNumber, r.Values)).ToList(), ct)).Checks.ToList();

    public Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default) =>
        ImportUnitOfWork.RunAsync<IReadOnlyList<ImportRowOutcome>>(_db, async _tx =>
        {
            var check = await CheckAsync(organizationId, rows.Select(r => (r.RowNumber, r.Values)).ToList(), ct);
            check.Checks.ThrowIfAny();

            var result = new List<(int Row, string Status, PricingRule Rule)>();
            foreach (var row in rows)
            {
                var plan = check.Plans[row.RowNumber];
                var v = row.Values;
                if (row.Action == "UPDATE")
                {
                    var target = row.ExistingEntityUuid ?? plan.ExistingUuid
                        ?? throw new UnprocessableEntityException($"Row {row.RowNumber}: there is no existing pricing rule to update.");
                    var rule = await _db.PricingRules.IgnoreQueryFilters()
                        .FirstOrDefaultAsync(r => r.OrganizationId == organizationId && r.Uuid == target, ct)
                        ?? throw new UnprocessableEntityException($"Row {row.RowNumber}: pricing rule {target} does not exist.");
                    if (plan.Price is { } price)                                rule.UnitPrice   = price;
                    if (Text(v, "currency_code") is not null)                   rule.CurrencyId  = plan.CurrencyId;
                    if (plan.ValidTo is { } to)                                 rule.EffectiveTo = to.ToDateTime(TimeOnly.MinValue);
                    rule.IsActive = true;
                    result.Add((row.RowNumber, "UPDATED", rule));
                }
                else
                {
                    var rule = new PricingRule
                    {
                        Uuid           = Guid.NewGuid(),
                        OrganizationId = organizationId,
                        VariantId      = plan.VariantId,
                        PartnerId      = plan.PartnerId,
                        PriceType      = plan.PriceType,
                        MinQty         = plan.MinQty,
                        UnitPrice      = plan.Price!.Value,
                        CurrencyId     = plan.CurrencyId,
                        EffectiveFrom  = (plan.ValidFrom ?? DateOnly.FromDateTime(DateTime.UtcNow)).ToDateTime(TimeOnly.MinValue),
                        EffectiveTo    = plan.ValidTo?.ToDateTime(TimeOnly.MinValue),
                        IsActive       = true,
                        CreatedBy      = userId,
                        CreatedDate    = DateTime.UtcNow
                    };
                    _db.PricingRules.Add(rule);
                    result.Add((row.RowNumber, "IMPORTED", rule));
                }
            }

            await _db.SaveChangesAsync(ct);
            return result.Select(r => new ImportRowOutcome(r.Row, r.Status, r.Rule.Uuid)).ToList();
        }, ct);

    // ── Checks ──────────────────────────────────────────────────────────────────

    private sealed record Plan(
        int VariantId, Guid? PartnerId, string PriceType, decimal? MinQty, decimal? Price, Guid CurrencyId,
        DateOnly? ValidFrom, DateOnly? ValidTo, Guid? ExistingUuid);

    private sealed record Checked(RowChecks Checks, Dictionary<int, Plan> Plans);

    private async Task<Checked> CheckAsync(
        Guid org, IReadOnlyList<(int RowNumber, IReadOnlyDictionary<string, string?> Values)> rows, CancellationToken ct)
    {
        var skus = rows.Select(r => Text(r.Values, "sku")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var variants = await VariantsBySkuAsync(org, skus, ct);

        var variantIds = variants.Values.Select(v => v.Id).Distinct().ToList();
        var rules = new List<PricingRule>();
        foreach (var chunk in variantIds.Chunk(Chunk))
            rules.AddRange(await _db.PricingRules.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.OrganizationId == org && r.IsActive && chunk.Contains(r.VariantId)).ToListAsync(ct));

        var customerCodes = rows.Select(r => Text(r.Values, "customer_code")).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var customers = await CustomersByCodeAsync(org, customerCodes, ct);

        Guid? baseCurrency = _orgCurrency is null ? null : await _orgCurrency.GetBaseCurrencyIdAsync(org);
        var currencyByCode = new Dictionary<string, OrgCurrencyInfo?>(StringComparer.OrdinalIgnoreCase);

        var checks = new RowChecks(rows.Select(r => r.RowNumber));
        var plans = new Dictionary<int, Plan>();
        var seen = new HashSet<string>();

        foreach (var (row, v) in rows)
        {
            var sku = Text(v, "sku");
            (int Id, bool Saleable)? variant = null;
            if (sku is null) checks.Error(row, "sku", ImportErrorCodes.Required, "SKU is required.");
            else if (variants.TryGetValue(sku, out var found)) variant = (found.Id, found.Saleable);
            else checks.Error(row, "sku", ImportErrorCodes.LookupNotFound, $"SKU '{sku}' does not exist. Import products first.", sku);
            if (variant is { Saleable: false })
                checks.Error(row, "sku", ImportErrorCodes.RuleViolation, $"'{sku}' is not a saleable product, so it has no selling price.", sku);

            var priceType = Code(v, "price_type");
            if (priceType is null) checks.Error(row, "price_type", ImportErrorCodes.Required, "Price type is required.");
            else if (!PriceTypes.Contains(priceType))
                checks.Error(row, "price_type", ImportErrorCodes.LookupNotFound,
                    $"'{priceType}' is not a price type. Use one of: {string.Join(", ", PriceTypes)}.", priceType);

            Guid? partnerId = null;
            if (Text(v, "customer_code") is { } customerCode)
            {
                if (_customers is null)
                    checks.Error(row, "customer_code", ImportErrorCodes.LookupNotFound, "Customers are not available on this server.", customerCode);
                else if (!customers.TryGetValue(customerCode, out var customer))
                    checks.Error(row, "customer_code", ImportErrorCodes.LookupNotFound, $"Customer '{customerCode}' does not exist. Import customers first.", customerCode);
                else if (!customer.IsActive)
                    checks.Error(row, "customer_code", ImportErrorCodes.RuleViolation, $"Customer '{customerCode}' is inactive.", customerCode);
                else partnerId = customer.Uuid;
            }
            else if (priceType == PricingRuleType.Contract)
                checks.Error(row, "customer_code", ImportErrorCodes.Required, "A CONTRACT price requires a customer.");

            var price = Decimal(v, "price", out var priceOk);
            if (!priceOk) checks.Error(row, "price", ImportErrorCodes.InvalidType, "Price must be a number.", Text(v, "price"));
            else if (price is null) checks.Error(row, "price", ImportErrorCodes.Required, "Price is required.");
            else if (price < 0) checks.Error(row, "price", ImportErrorCodes.OutOfRange, "Price cannot be negative.", Format(price));

            var minQty = Decimal(v, "min_qty", out var minOk);
            if (!minOk) checks.Error(row, "min_qty", ImportErrorCodes.InvalidType, "Min quantity must be a number.", Text(v, "min_qty"));
            else if (minQty < 0) checks.Error(row, "min_qty", ImportErrorCodes.OutOfRange, "Min quantity cannot be negative.", Format(minQty));

            var from = Date(v, "valid_from", out var fromOk);
            var to = Date(v, "valid_to", out var toOk);
            if (!fromOk) checks.Error(row, "valid_from", ImportErrorCodes.InvalidDate, "Valid from is not a date (yyyy-MM-dd).", Text(v, "valid_from"));
            if (!toOk) checks.Error(row, "valid_to", ImportErrorCodes.InvalidDate, "Valid to is not a date (yyyy-MM-dd).", Text(v, "valid_to"));
            if (from is { } f && to is { } t && f > t)
                checks.Error(row, "valid_to", ImportErrorCodes.InvalidDate, "Valid to cannot be before valid from.", Text(v, "valid_to"));

            var currencyId = Guid.Empty;
            if (Text(v, "currency_code") is { } currencyCode)
            {
                if (_currencies is null)
                    checks.Error(row, "currency_code", ImportErrorCodes.LookupNotFound, "Currencies are not available on this server.", currencyCode);
                else
                {
                    if (!currencyByCode.TryGetValue(currencyCode, out var info))
                        currencyByCode[currencyCode] = info = await _currencies.GetByCodeAsync(org, currencyCode.ToUpperInvariant(), ct);
                    if (info is null || !info.IsActive)
                        checks.Error(row, "currency_code", ImportErrorCodes.LookupNotFound,
                            $"Currency '{currencyCode}' is not one of this organization's active currencies.", currencyCode);
                    else currencyId = info.CurrencyId;
                }
            }
            else if (baseCurrency is { } b) currencyId = b;
            else checks.Error(row, "currency_code", ImportErrorCodes.Required,
                "Currency is required — this organization has no base currency configured.");

            if (variant is null || priceType is null) continue;

            var candidates = rules.Where(r => r.VariantId == variant.Value.Id && r.PartnerId == partnerId && r.PriceType == priceType
                                           && r.MinQty == minQty);
            var match = from is { } start
                ? candidates.FirstOrDefault(r => DateOnly.FromDateTime(r.EffectiveFrom) == start)
                : candidates.OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.Id).FirstOrDefault();
            checks.Existing(row, match?.Uuid);
            plans[row] = new Plan(variant.Value.Id, partnerId, priceType, minQty, price, currencyId, from, to, match?.Uuid);

            if (!seen.Add($"{variant.Value.Id}|{partnerId}|{priceType}|{minQty}|{from}"))
                checks.Error(row, "sku", ImportErrorCodes.DuplicateInFile,
                    "The same SKU, customer, price type, min quantity and valid-from date appear more than once in the file.", sku);
        }

        return new Checked(checks, plans);
    }

    /// <summary>A variant by its own SKU, else a product's default variant by the product SKU.</summary>
    private async Task<Dictionary<string, (int Id, bool Saleable)>> VariantsBySkuAsync(Guid org, List<string> skus, CancellationToken ct)
    {
        var found = new Dictionary<string, (int Id, bool Saleable)>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in skus.Chunk(Chunk))
        {
            var direct = await _db.ProductVariants.IgnoreQueryFilters().AsNoTracking()
                .Where(v => v.OrganizationId == org && chunk.Contains(v.Sku))
                .Select(v => new { v.Id, v.Sku, v.Product.IsSaleable }).ToListAsync(ct);
            foreach (var d in direct) found.TryAdd(d.Sku, (d.Id, d.IsSaleable));

            var byProduct = await _db.ProductVariants.IgnoreQueryFilters().AsNoTracking()
                .Where(v => v.OrganizationId == org && v.IsDefault && chunk.Contains(v.Product.Sku))
                .Select(v => new { v.Id, v.Product.Sku, v.Product.IsSaleable }).ToListAsync(ct);
            foreach (var d in byProduct) found.TryAdd(d.Sku, (d.Id, d.IsSaleable));
        }
        return found;
    }

    /// <summary>
    /// The organization's customers by code, read through Suppliers' shared customer source (explicit organization,
    /// paged oldest change first) until every wanted code is found.
    /// </summary>
    private async Task<Dictionary<string, PosCustomer>> CustomersByCodeAsync(Guid org, List<string> codes, CancellationToken ct)
    {
        var found = new Dictionary<string, PosCustomer>(StringComparer.OrdinalIgnoreCase);
        if (codes.Count == 0 || _customers is null) return found;

        var wanted = codes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        DateTime? since = null;
        for (var page = 0; page < 1000 && wanted.Count > 0; page++)
        {
            var batch = await _customers.GetChangedAsync(org, since, 1000, ct);
            if (batch.Count == 0) break;
            foreach (var c in batch)
                if (wanted.Remove(c.Code)) found[c.Code] = c;
            var last = batch[^1].ModifiedAt;
            if (since == last) break;
            since = last;
        }
        return found;
    }
}
