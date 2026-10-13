using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Integration;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using static SMS.Modules.Inventory.Import.ImportValues;

namespace SMS.Modules.Inventory.Import;

/// <summary>
/// A40 D-8 — PRODUCT (FSD §3.2: sku, name, description, category_code, unit_code, sale_price, cost_price, barcode, weight,
/// min_stock_level, max_stock_level, is_service, is_active). One row = one product with its single default variant (A26):
/// the product and the variant both carry the row's SKU; barcode, prices and weight live on the variant. category_code is a
/// category code, or a sub-category code (then the product gets that sub-category and its parent). unit_code must be one
/// of the organization's units (Lookups uom values, through the UNIT target). is_service → ProductType SERVICE, supply
/// method SERVICE, not stockable (A30 §6.1 / A36); other rows are STOCK_ITEM / PURCHASE. Match = sku (the product's SKU,
/// or its default variant's SKU).
/// </summary>
internal sealed class ProductImportTarget : IImportTarget
{
    private const int Chunk = 1000;

    private readonly InventoryDbContext _db;
    private readonly IServiceProvider? _services;
    private readonly IBackgroundJobClient? _jobs;
    private readonly VariantQuickBooksPublisher? _quickBooks;

    public ProductImportTarget(
        InventoryDbContext db, IServiceProvider? services = null, IBackgroundJobClient? jobs = null,
        VariantQuickBooksPublisher? quickBooks = null)
    {
        _db         = db;
        _services   = services;
        _jobs       = jobs;
        _quickBooks = quickBooks;
    }

    public ImportProfileDefinition Describe() => new(
        ImportEntityTypes.Product, "Products",
        "Products with their default variant (SKU, barcode and prices). Further variants are added on the product page.",
        ImportOrder: 3, IsOpeningBalance: false, MatchFields: ["sku"],
        Fields:
        [
            new("sku", "SKU", "Unique product code; also the default variant's SKU.", ImportFieldTypes.Text, true, true, 1, "CAB-4MM",
                MaxLength: 30, Aliases: ["product_code", "product code", "item_code", "item code", "material", "material number", "code"]),
            new("name", "Name", null, ImportFieldTypes.Text, true, false, 2, "4mm Copper Cable", MaxLength: 200,
                Aliases: ["product_name", "product name", "item_name", "material description"]),
            new("description", "Description", null, ImportFieldTypes.Text, false, false, 3, null, MaxLength: 2000),
            new("category_code", "Category Code", "A category code, or a sub-category code.", ImportFieldTypes.Lookup, false, false, 4,
                "ELEC", MaxLength: 20, LookupList: ImportListKeys.Categories, Aliases: ["category", "material group", "product_category"]),
            new("unit_code", "Unit", "Unit of measure code (Settings > Lookups > Unit of Measure).", ImportFieldTypes.Lookup, false, false, 5,
                "PCS", MaxLength: 20, LookupList: ImportListKeys.Units, Aliases: ["uom", "unit", "base unit", "uom_code"]),
            new("sale_price", "Sale Price", null, ImportFieldTypes.Decimal, false, false, 6, "150.00", MinValue: 0m, DecimalPlaces: 4,
                Aliases: ["selling_price", "selling price", "price", "list price"]),
            new("cost_price", "Cost Price", "Purchase price of the default variant (0 when empty).", ImportFieldTypes.Decimal, false, false, 7,
                "100.00", MinValue: 0m, DecimalPlaces: 4, Aliases: ["purchase_price", "purchase price", "cost", "standard price"]),
            new("barcode", "Barcode", "Unique per organization.", ImportFieldTypes.Text, false, false, 8, null, MaxLength: 50,
                Aliases: ["ean", "upc", "gtin"]),
            new("weight", "Weight (kg)", null, ImportFieldTypes.Decimal, false, false, 9, null, MinValue: 0m, DecimalPlaces: 3,
                Aliases: ["weight_kg", "gross weight"]),
            new("min_stock_level", "Min Stock Level", null, ImportFieldTypes.Decimal, false, false, 10, null, MinValue: 0m, DecimalPlaces: 4,
                Aliases: ["min_stock", "minimum stock", "safety stock"]),
            new("max_stock_level", "Max Stock Level", null, ImportFieldTypes.Decimal, false, false, 11, null, MinValue: 0m, DecimalPlaces: 4,
                Aliases: ["max_stock", "maximum stock"]),
            new("is_service", "Is Service", "true = a service (not held in stock).", ImportFieldTypes.Boolean, false, false, 12, "false",
                DefaultValue: "false", Aliases: ["service"]),
            new("is_active", "Active", null, ImportFieldTypes.Boolean, false, false, 13, "true", DefaultValue: "true",
                Aliases: ["active", "status"]),
            // Default variant's channel availability. Blank = unticked on a new product, unchanged on an update.
            new(Retail, "Available for Retail", "Sale orders / quotations may sell it. Blank = No on a new product.",
                ImportFieldTypes.Boolean, false, false, 14, "Yes", Aliases: ["retail", "available for retail"]),
            new(Pos, "Available for POS", "Blank = No on a new product.",
                ImportFieldTypes.Boolean, false, false, 15, "No", Aliases: ["pos", "available for pos"]),
            new(MirMiv, "Available for MIR/MIV", "Material issue requests / vouchers. Blank = No on a new product.",
                ImportFieldTypes.Boolean, false, false, 16, "No", Aliases: ["mir_miv", "available for mir/miv"]),
            new(Production, "Available for Production",
                "BOM input: raw materials, components, semi-finished / finished goods, consumables and services only. Blank = No on a new product.",
                ImportFieldTypes.Boolean, false, false, 17, "No", Aliases: ["production", "available for production"]),
            new(Services, "Available for Services", "Blank = No on a new product.",
                ImportFieldTypes.Boolean, false, false, 18, "No", Aliases: ["services", "available for services"]),
            // A30 §6 classification, as on the product screens. Blank type = from is_service (SERVICE / STOCK_ITEM).
            new(ProductTypeField, "Product Type",
                $"One of {string.Join(", ", ProductType.All)}. Wins over Is Service. Blank = SERVICE or STOCK_ITEM from Is Service (new), unchanged (update).",
                ImportFieldTypes.Lookup, false, false, 19, "STOCK_ITEM", MaxLength: 20, LookupList: ImportListKeys.ProductTypes,
                Aliases: ["type", "material type", "item type"]),
            new(SupplyMethodField, "Supply Method",
                $"One of {string.Join(", ", SupplyMethod.All)}. Blank = the product type's default (finished / semi-finished goods: MANUFACTURE).",
                ImportFieldTypes.Lookup, false, false, 20, "PURCHASE", MaxLength: 20, LookupList: ImportListKeys.SupplyMethods,
                Aliases: ["procurement type", "supply"])
        ]);

    internal const string ProductTypeField = "product_type", SupplyMethodField = "supply_method";

    internal const string Retail = "available_for_retail", Pos = "available_for_pos", MirMiv = "available_for_mir_miv",
        Production = "available_for_production", Services = "available_for_services";

    /// <summary>Sets the sent channel flags on the default variant; a blank column leaves the flag as it is.</summary>
    private static void ApplyChannels(IReadOnlyDictionary<string, string?> v, ProductVariant variant)
    {
        if (Bool(v, Retail, out _) is { } retail)         variant.IsAvailableForRetail     = retail;
        if (Bool(v, Pos, out _) is { } pos)               variant.IsAvailableForPos        = pos;
        if (Bool(v, MirMiv, out _) is { } mirMiv)         variant.IsAvailableForMirMiv     = mirMiv;
        if (Bool(v, Production, out _) is { } production) variant.IsAvailableForProduction = production;
        if (Bool(v, Services, out _) is { } services)     variant.IsAvailableForServices   = services;
    }

    public async Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var categories = await _db.ProductCategories.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.IsActive)
            .OrderBy(c => c.Code).Select(c => c.Code).Take(ImportListKeys.MaxValues).ToListAsync(ct);
        var subs = await _db.ProductSubCategories.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId && s.IsActive)
            .Select(s => s.Code).Distinct().OrderBy(c => c).Take(ImportListKeys.MaxValues).ToListAsync(ct);
        var units = await ImportTargetLists.OfAsync(_services, ImportEntityTypes.Unit, ImportListKeys.Units, organizationId, ct) ?? [];
        return
        [
            new(ImportListKeys.Categories, categories), new(ImportListKeys.SubCategories, subs), new(ImportListKeys.Units, units),
            new(ImportListKeys.ProductTypes, ProductType.All), new(ImportListKeys.SupplyMethods, SupplyMethod.All)
        ];
    }

    public async Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default) =>
        (await CheckAsync(organizationId, rows.Select(r => (r.RowNumber, r.Values, r.ExistingEntityUuid)).ToList(), ct)).Checks.ToList();

    public async Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default)
    {
        var touched = new List<Product>();

        var outcomes = await ImportUnitOfWork.RunAsync<IReadOnlyList<ImportRowOutcome>>(_db, async _tx =>
        {
            touched.Clear();
            var check = await CheckAsync(organizationId, rows.Select(r => (r.RowNumber, r.Values, r.ExistingEntityUuid)).ToList(), ct);
            check.Checks.ThrowIfAny();

            var result = new List<(int Row, string Status, Product Product)>();
            foreach (var row in rows)
            {
                var plan = check.Plans[row.RowNumber];
                var v = row.Values;
                var update = row.Action == "UPDATE";
                if (update && plan.ExistingProductId is null)
                    throw new UnprocessableEntityException($"Row {row.RowNumber}: there is no existing product '{plan.Sku}' to update.");
                if (!update && plan.ExistingProductId is not null)
                    throw new UnprocessableEntityException($"Row {row.RowNumber}: SKU '{plan.Sku}' already exists.");

                var sale     = Decimal(v, "sale_price", out _);
                var cost     = Decimal(v, "cost_price", out _);
                var weight   = Decimal(v, "weight", out _);
                var min      = Decimal(v, "min_stock_level", out _);
                var max      = Decimal(v, "max_stock_level", out _);
                var isActive = Bool(v, "is_active", out _);
                var barcode  = Text(v, "barcode");

                Product product;
                if (update)
                {
                    product = await _db.Products.IgnoreQueryFilters().Include(p => p.Variants)
                        .FirstAsync(p => p.OrganizationId == organizationId && p.Id == plan.ExistingProductId, ct);
                    if (Text(v, "name") is { } name)      product.Name        = name;
                    if (Text(v, "description") is { } d)  product.Description = d;
                    if (plan.CategoryId is { } c)         { product.CategoryId = c; product.SubCategoryId = plan.SubCategoryId; }
                    if (plan.UnitCode is { } u)           product.UomCode     = u;
                    if (weight is not null)               product.WeightKg    = weight;
                    if (min is not null)                  product.MinStockLevel = min;
                    if (max is not null)                  product.MaxStockLevel = max;
                    if (isActive is { } a)                { product.IsActive = a; product.Status = a ? "ACTIVE" : "INACTIVE"; }
                    plan.Classification!.ApplyTo(product);
                    product.UpdatedDate = DateTime.UtcNow;

                    var variant = product.Variants.FirstOrDefault(x => x.IsDefault) ?? product.Variants.OrderBy(x => x.Id).First();
                    if (barcode is not null) variant.Barcode       = barcode;
                    if (sale is not null)    variant.SellingPrice  = sale;
                    if (cost is not null)    variant.PurchasePrice = cost.Value;
                    if (weight is not null)  variant.WeightKg      = weight;
                    ApplyChannels(v, variant);
                    result.Add((row.RowNumber, "UPDATED", product));
                }
                else
                {
                    var name      = Text(v, "name")!;
                    product = new Product
                    {
                        Uuid           = Guid.NewGuid(),
                        OrganizationId = organizationId,
                        Sku            = plan.Sku,
                        Name           = name,
                        Description    = Text(v, "description"),
                        CategoryId     = plan.CategoryId,
                        SubCategoryId  = plan.SubCategoryId,
                        UomCode        = plan.UnitCode,
                        WeightKg       = weight,
                        MinStockLevel  = min,
                        MaxStockLevel  = max,
                        Status        = isActive == false ? "INACTIVE" : "ACTIVE",
                        IsActive       = isActive ?? true,
                        CreatedDate    = DateTime.UtcNow,
                        CreatedBy      = userId
                    };
                    product.Variants.Add(new ProductVariant
                    {
                        Uuid           = Guid.NewGuid(),
                        OrganizationId = organizationId,
                        Sku            = plan.Sku,
                        VariantName    = name,
                        Barcode        = barcode,
                        PurchasePrice  = cost ?? 0m,
                        SellingPrice   = sale,
                        WeightKg       = weight,
                        IsDefault      = true,
                        IsActive       = true,
                        SortOrder      = 0,
                        CreatedDate    = DateTime.UtcNow,
                        CreatedBy      = userId
                    });
                    plan.Classification!.ApplyTo(product);
                    ApplyChannels(v, product.Variants.Single());
                    _db.Products.Add(product);
                    result.Add((row.RowNumber, "IMPORTED", product));
                }
            }

            await _db.SaveChangesAsync(ct);
            touched.AddRange(result.Select(r => r.Product));
            return result.Select(r => new ImportRowOutcome(r.Row, r.Status, r.Product.Uuid)).ToList();
        }, ct);

        // After the commit, as the product screens do: reindex for search, tell QuickBooks. Neither may fail the import.
        foreach (var product in touched)
        {
            if (_jobs is not null)
                foreach (var variantId in product.Variants.Select(x => x.Id).ToList())
                {
                    try { _jobs.Enqueue<IProductSearchIndexService>(s => s.RebuildForVariantAsync(variantId)); }
                    catch { /* best effort: the admin "rebuild search index" recovers it */ }
                }
            if (_quickBooks is not null) await _quickBooks.PublishProductAsync(product.Id);
        }

        return outcomes;
    }

    // ── Checks ──────────────────────────────────────────────────────────────────

    private sealed record Plan(string Sku, int? ExistingProductId, int? CategoryId, int? SubCategoryId, string? UnitCode,
        Classification? Classification);

    /// <summary>The resolved type, supply method and flags; TypeChanged = an existing product gets another type.</summary>
    private sealed record Classification(
        string ProductType, string SupplyMethod, bool IsSaleable, bool IsPurchasable, bool IsStockable, bool TypeChanged)
    {
        public void ApplyTo(Product p)
        {
            p.ProductType      = ProductType;
            p.SupplyMethod     = SupplyMethod;
            p.IsSaleable       = IsSaleable;
            p.IsPurchasable    = IsPurchasable;
            p.IsStockable      = IsStockable;
            p.IsManufacturable = SupplyMethod == Shared.Common.SupplyMethod.Manufacture;
            // A36 D-2 — a product that is no longer a service keeps no service configuration.
            if (TypeChanged && ProductType != Shared.Common.ProductType.Service) ServiceConfiguration.None.ApplyTo(p);
        }
    }

    private sealed record ProductRow(
        int Id, Guid Uuid, string Sku, string Name, bool IsActive, string ProductType, string SupplyMethod,
        bool IsSaleable, bool IsPurchasable, bool IsStockable);

    private sealed record Checked(RowChecks Checks, Dictionary<int, Plan> Plans);

    private sealed record VariantRow(int Id, int ProductId, string Sku, string? Barcode, bool IsDefault);

    private async Task<Checked> CheckAsync(
        Guid org, IReadOnlyList<(int RowNumber, IReadOnlyDictionary<string, string?> Values, Guid? Existing)> rows, CancellationToken ct)
    {
        var skus     = rows.Select(r => Text(r.Values, "sku")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var barcodes = rows.Select(r => Text(r.Values, "barcode")).OfType<string>().Distinct().ToList();
        var names    = rows.Select(r => Text(r.Values, "name")?.ToLower()).OfType<string>().Distinct().ToList();
        var given    = rows.Select(r => r.Existing).OfType<Guid>().Distinct().ToList();

        var products = new List<ProductRow>();
        IQueryable<ProductRow> OfOrg(IQueryable<Product> q) => q
            .Select(p => new ProductRow(p.Id, p.Uuid, p.Sku, p.Name, p.IsActive, p.ProductType, p.SupplyMethod,
                p.IsSaleable, p.IsPurchasable, p.IsStockable));
        var all = _db.Products.IgnoreQueryFilters().AsNoTracking().Where(p => p.OrganizationId == org);
        foreach (var chunk in skus.Chunk(Chunk))
            products.AddRange(await OfOrg(all.Where(p => chunk.Contains(p.Sku))).ToListAsync(ct));

        var variants = new List<VariantRow>();
        foreach (var chunk in skus.Chunk(Chunk))
            variants.AddRange(await _db.ProductVariants.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.OrganizationId == org && chunk.Contains(x.Sku))
                .Select(x => new VariantRow(x.Id, x.ProductId, x.Sku, x.Barcode, x.IsDefault)).ToListAsync(ct));
        foreach (var chunk in barcodes.Chunk(Chunk))
            variants.AddRange(await _db.ProductVariants.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.OrganizationId == org && x.Barcode != null && chunk.Contains(x.Barcode))
                .Select(x => new VariantRow(x.Id, x.ProductId, x.Sku, x.Barcode, x.IsDefault)).ToListAsync(ct));

        // Products reached through a variant SKU, through an existing uuid, or sharing a name with a row.
        var extraIds = variants.Select(x => x.ProductId).Except(products.Select(p => p.Id)).Distinct().ToList();
        foreach (var chunk in extraIds.Chunk(Chunk))
            products.AddRange(await OfOrg(all.Where(p => chunk.Contains(p.Id))).ToListAsync(ct));
        foreach (var chunk in given.Chunk(Chunk))
            products.AddRange(await OfOrg(all.Where(p => chunk.Contains(p.Uuid))).ToListAsync(ct));
        var sameName = new List<(int Id, string Name)>();
        foreach (var chunk in names.Chunk(Chunk))
            sameName.AddRange((await _db.Products.IgnoreQueryFilters().AsNoTracking()
                    .Where(p => p.OrganizationId == org && p.IsActive && chunk.Contains(p.Name.ToLower()))
                    .Select(p => new { p.Id, p.Name }).ToListAsync(ct))
                .Select(p => (p.Id, p.Name)));
        var productById = products.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());

        // Which variants of the matched products are available for production (A30: a type that cannot be a BOM input
        // is refused while one is — ApplyClassificationAsync, the product edit screen's rule).
        var productionVariants = new List<(int ProductId, bool IsDefault)>();
        foreach (var chunk in productById.Keys.Chunk(Chunk))
            productionVariants.AddRange((await _db.ProductVariants.IgnoreQueryFilters().AsNoTracking()
                    .Where(x => x.OrganizationId == org && x.IsAvailableForProduction && chunk.Contains(x.ProductId))
                    .Select(x => new { x.ProductId, x.IsDefault }).ToListAsync(ct))
                .Select(x => (x.ProductId, x.IsDefault)));

        var categories = await _db.ProductCategories.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == org).Select(c => new { c.Id, c.Code, c.IsActive }).ToListAsync(ct);
        var subs = await _db.ProductSubCategories.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == org).Select(s => new { s.Id, s.CategoryId, s.Code, s.IsActive }).ToListAsync(ct);
        var unitList = await ImportTargetLists.OfAsync(_services, ImportEntityTypes.Unit, ImportListKeys.Units, org, ct);
        var units = unitList?.ToDictionary(u => u, u => u, StringComparer.OrdinalIgnoreCase);

        var checks = new RowChecks(rows.Select(r => r.RowNumber));
        var plans = new Dictionary<int, Plan>();
        var seenSku = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenBarcode = new HashSet<string>(StringComparer.Ordinal);
        var seenName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (row, v, inputExisting) in rows)
        {
            var sku = Text(v, "sku");
            if (sku is null)
            {
                checks.Error(row, "sku", ImportErrorCodes.Required, "SKU is required.");
                continue;
            }

            // The product this SKU names: by its own SKU, else through its default variant's SKU.
            int? existingId = null;
            var byProductSku = products.FirstOrDefault(p => string.Equals(p.Sku, sku, StringComparison.OrdinalIgnoreCase));
            if (byProductSku is not null) existingId = byProductSku.Id;
            else if (variants.FirstOrDefault(x => string.Equals(x.Sku, sku, StringComparison.OrdinalIgnoreCase)) is { } variantSku)
            {
                if (variantSku.IsDefault) existingId = variantSku.ProductId;
                else
                {
                    var owner = productById.GetValueOrDefault(variantSku.ProductId);
                    checks.Error(row, "sku", ImportErrorCodes.SkuInUse,
                        $"SKU '{sku}' belongs to a variant of product '{owner?.Sku}'; a product import cannot reuse it.", sku);
                }
            }
            else if (inputExisting is { } u && products.FindIndex(p => p.Uuid == u) is var at and >= 0)
                existingId = products[at].Id;

            var existing = existingId is { } eid ? productById[eid] : null;
            checks.Existing(row, existing?.Uuid);

            if (!seenSku.Add(sku))
                checks.Error(row, "sku", ImportErrorCodes.DuplicateInFile, $"SKU '{sku}' appears more than once in the file.", sku);

            var name = Text(v, "name");
            if (existingId is null && name is null)
                checks.Error(row, "name", ImportErrorCodes.Required, "Name is required for a new product.");
            if (name is not null)
            {
                if (sameName.Any(p => p.Id != existingId && string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                    checks.Error(row, "name", ImportErrorCodes.DuplicateName, $"A product named '{name}' already exists. Use a unique name.", name);
                else if (!seenName.Add(name))
                    checks.Error(row, "name", ImportErrorCodes.DuplicateName, $"Product name '{name}' appears more than once in the file.", name);
            }

            // Category or sub-category.
            int? categoryId = null, subCategoryId = null;
            if (Text(v, "category_code") is { } categoryCode)
            {
                var category = categories.FirstOrDefault(c => string.Equals(c.Code.Trim(), categoryCode, StringComparison.OrdinalIgnoreCase));
                if (category is not null)
                {
                    if (!category.IsActive)
                        checks.Error(row, "category_code", ImportErrorCodes.LookupNotFound, $"Category '{categoryCode}' is inactive.", categoryCode);
                    categoryId = category.Id;
                }
                else
                {
                    var matches = subs.Where(s => string.Equals(s.Code.Trim(), categoryCode, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 0)
                        checks.Error(row, "category_code", ImportErrorCodes.LookupNotFound,
                            $"Category '{categoryCode}' does not exist. Import categories first.", categoryCode);
                    else if (matches.Count > 1)
                        checks.Error(row, "category_code", ImportErrorCodes.AmbiguousCode,
                            $"Sub-category code '{categoryCode}' exists under {matches.Count} categories; use a unique code.", categoryCode);
                    else
                    {
                        if (!matches[0].IsActive)
                            checks.Error(row, "category_code", ImportErrorCodes.LookupNotFound, $"Sub-category '{categoryCode}' is inactive.", categoryCode);
                        categoryId = matches[0].CategoryId;
                        subCategoryId = matches[0].Id;
                    }
                }
            }

            // Unit of measure.
            string? unitCode = null;
            if (Text(v, "unit_code") is { } unit)
            {
                if (units is null) unitCode = unit.ToUpperInvariant();
                else if (units.TryGetValue(unit, out var known)) unitCode = known;
                else checks.Error(row, "unit_code", ImportErrorCodes.LookupNotFound,
                    $"Unit '{unit}' does not exist. Import units first or add it under Lookups > Unit of Measure.", unit);
            }

            // Numbers.
            decimal? NonNegative(string field, string display)
            {
                var d = Decimal(v, field, out var ok);
                if (!ok) checks.Error(row, field, ImportErrorCodes.InvalidType, $"{display} must be a number.", Text(v, field));
                else if (d < 0) checks.Error(row, field, ImportErrorCodes.OutOfRange, $"{display} cannot be negative.", Text(v, field));
                return d;
            }
            NonNegative("sale_price", "Sale price");
            NonNegative("cost_price", "Cost price");
            NonNegative("weight", "Weight");
            var min = NonNegative("min_stock_level", "Min stock level");
            var max = NonNegative("max_stock_level", "Max stock level");
            if (min is { } mn && max is { } mx && mn > mx)
                checks.Error(row, "max_stock_level", ImportErrorCodes.OutOfRange, "Max stock level cannot be below the min stock level.", Format(mx));

            var isService = Bool(v, "is_service", out var serviceOk);
            if (!serviceOk) checks.Error(row, "is_service", ImportErrorCodes.InvalidType, "Is service must be true or false.", Text(v, "is_service"));
            Bool(v, "is_active", out var activeOk);
            if (!activeOk) checks.Error(row, "is_active", ImportErrorCodes.InvalidType, "Active must be true or false.", Text(v, "is_active"));
            foreach (var channel in new[] { Retail, Pos, MirMiv, Production, Services })
            {
                Bool(v, channel, out var channelOk);
                if (!channelOk) checks.Error(row, channel, ImportErrorCodes.InvalidType, "Use Yes or No (true / false).", Text(v, channel));
            }
            var productName = name ?? existing?.Name ?? sku;

            // Classification (A30 §6.5) — the product screens' rules. product_type wins over is_service; they may not disagree.
            var typeCode = Code(v, ProductTypeField);
            var supplyCode = Code(v, SupplyMethodField);
            if (typeCode is not null && !ProductType.IsKnown(typeCode))
                checks.Error(row, ProductTypeField, ImportErrorCodes.LookupNotFound,
                    $"'{typeCode}' is not a product type. Use one of: {string.Join(", ", ProductType.All)}.", typeCode);
            if (supplyCode is not null && !SupplyMethod.IsKnown(supplyCode))
                checks.Error(row, SupplyMethodField, ImportErrorCodes.LookupNotFound,
                    $"'{supplyCode}' is not a supply method. Use one of: {string.Join(", ", SupplyMethod.All)}.", supplyCode);
            if (typeCode is not null && isService is { } said && said != (typeCode == ProductType.Service))
                checks.Error(row, "is_service", ImportErrorCodes.RuleViolation,
                    $"Is service says {(said ? "Yes" : "No")} but the product type is {typeCode}; leave is service empty or make them agree.",
                    Text(v, "is_service"));

            Classification? classification = null;
            if ((typeCode is null || ProductType.IsKnown(typeCode)) && (supplyCode is null || SupplyMethod.IsKnown(supplyCode)))
            {
                string newType;
                if (typeCode is not null) newType = typeCode;
                else if (existing is null) newType = isService == true ? ProductType.Service : ProductType.StockItem;
                else if (isService == true) newType = ProductType.Service;
                else if (isService == false && existing.ProductType == ProductType.Service)
                {
                    checks.Error(row, "is_service", ImportErrorCodes.TypeChangeRefused,
                        $"'{existing.Sku}' is a service; to make it something else, give the new product type.", Text(v, "is_service"));
                    newType = existing.ProductType;
                }
                else newType = existing.ProductType;

                // As the product edit screen: a new type re-defaults the supply method and the flags; otherwise they stay.
                var typeChanged = existing is null || newType != existing.ProductType;
                var defaults = ProductTypeRules.For(newType);
                classification = typeChanged
                    ? new Classification(newType, supplyCode ?? ProductTypeRules.DefaultSupplyMethod(newType),
                        defaults.CanSell, defaults.CanPurchase, defaults.CanStock, existing is not null)
                    : new Classification(newType, supplyCode ?? existing!.SupplyMethod,
                        existing!.IsSaleable, existing.IsPurchasable, existing.IsStockable, false);

                if (ProductClassificationRules.Problem(classification.ProductType, classification.SupplyMethod, classification.IsStockable) is { } problem)
                    checks.Error(row, typeCode is null && supplyCode is not null ? SupplyMethodField : ProductTypeField,
                        ImportErrorCodes.RuleViolation, problem, typeCode ?? supplyCode);

                // ApplyClassificationAsync — not while a variant is available for production and the new type cannot be a BOM input.
                if (classification.TypeChanged && !ProductTypeRules.For(newType).CanBeBomInput)
                {
                    var stillProduction = productionVariants.Any(p => p.ProductId == existingId
                                                                   && (!p.IsDefault || Bool(v, Production, out _) != false));
                    if (stillProduction)
                        checks.Error(row, ProductTypeField, ImportErrorCodes.RuleViolation,
                            $"'{productName}' cannot become a {ProductClassificationRules.Describe(newType)} while one of its variants is available for production. Untick that on the variant first.",
                            typeCode);
                }

                // Channel availability of the default variant: production only for a type that can be a BOM input.
                if (Bool(v, Production, out _) == true &&
                    ProductClassificationRules.ProductionInputProblem(newType, productName) is { } productionProblem)
                    checks.Error(row, Production, ImportErrorCodes.RuleViolation, productionProblem, Text(v, Production));
            }

            // Barcode: unique per organization.
            if (Text(v, "barcode") is { } barcode)
            {
                var holder = variants.FirstOrDefault(x => x.Barcode == barcode);
                if (holder is not null && !(holder.ProductId == existingId && holder.IsDefault))
                    checks.Error(row, "barcode", ImportErrorCodes.DuplicateInDb, $"Barcode '{barcode}' is already used by SKU '{holder.Sku}'.", barcode);
                else if (!seenBarcode.Add(barcode))
                    checks.Error(row, "barcode", ImportErrorCodes.DuplicateInFile, $"Barcode '{barcode}' appears more than once in the file.", barcode);
            }

            plans[row] = new Plan(sku, existingId, categoryId, subCategoryId, unitCode, classification);
        }

        return new Checked(checks, plans);
    }
}
