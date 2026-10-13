using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using static SMS.Modules.Inventory.Import.ImportValues;

namespace SMS.Modules.Inventory.Import;

/// <summary>
/// A40 D-7 — OPENING_STOCK (FSD B.8: product_sku, warehouse_code, location_code, quantity, unit_cost, batch_number,
/// expiry_date). Each row puts stock IN to the warehouse (the bin when location_code is given; its batch and expiry for a
/// batch-tracked product), dated at the organization's opening balance date (Finance, <see cref="IOpeningBalanceDateProvider"/>):
/// an inventory ledger entry of type OPENING_STOCK (mirrored to the master product ledger, OPENING_BALANCE → WAREHOUSE) and a
/// Finance product ledger IN at the unit cost, so later sales cost correctly — all in Inventory's one transaction, the
/// product ledger enlisted in it the way a GRN approval posts both (EfGrnInventoryPoster).
/// <para>Rules: refused until the opening date is set; one opening per variant + warehouse (+ batch for a batch-tracked
/// product) — a second is DUPLICATE_IN_DB (IMP-OB-03); never updated (IMP-OB-06); quantity ≥ 0 (IMP-OB-04); serial-tracked
/// products are refused (their units are entered serial by serial through a GRN or adjustment); services hold no stock.</para>
/// </summary>
internal sealed class OpeningStockImportTarget : IImportTarget
{
    public const string TransactionType = "OPENING_STOCK";
    public const string ReferenceType   = "OPENING_STOCK";
    public const string OpeningDateNotSetMessage = "Set the opening balance date in Finance settings first.";
    private const int Chunk = 1000;

    private readonly InventoryDbContext _db;
    private readonly IInventoryLedgerService _ledger;
    private readonly IOpeningBalanceDateProvider? _openingDate;
    private readonly IProductLedgerService? _productLedger;

    public OpeningStockImportTarget(
        InventoryDbContext db, IInventoryLedgerService ledger,
        IOpeningBalanceDateProvider? openingDate = null, IProductLedgerService? productLedger = null)
    {
        _db            = db;
        _ledger        = ledger;
        _openingDate   = openingDate;
        _productLedger = productLedger;
    }

    /// <summary>The Finance product ledger entry type for an opening: OPENING when Finance knows one, else an ADJUSTMENT IN.</summary>
    internal static string ProductLedgerEntryType =>
        ProductLedgerEntryTypes.All.FirstOrDefault(t => t is "OPENING" or "OPENING_STOCK" or "OPENING_BALANCE")
        ?? ProductLedgerEntryTypes.Adjustment;

    public ImportProfileDefinition Describe() => new(
        ImportEntityTypes.OpeningStock, "Opening stock",
        "Stock on hand at the opening balance date, per product, warehouse (and bin / batch), with its unit cost.",
        ImportOrder: 11, IsOpeningBalance: true, MatchFields: ["product_sku", "warehouse_code", "batch_number"],
        Fields:
        [
            new("product_sku", "Product SKU", "Variant SKU, or a product SKU for its default variant.", ImportFieldTypes.Lookup, true, true, 1,
                "CAB-4MM", MaxLength: 50, LookupList: ImportListKeys.Skus, Aliases: ["sku", "product_code", "item_code", "material"]),
            new("warehouse_code", "Warehouse Code", null, ImportFieldTypes.Lookup, true, true, 2, "WH-MAIN", MaxLength: 20,
                LookupList: ImportListKeys.Warehouses, Aliases: ["warehouse", "plant", "storage location"]),
            new("location_code", "Bin", "Optional bin of that warehouse: BIN, or ZONE/BIN when the bin code repeats across zones.",
                ImportFieldTypes.Lookup, false, false, 3, null, MaxLength: 41, LookupList: ImportListKeys.Bins,
                Aliases: ["bin", "bin_code", "storage bin", "location"]),
            new("quantity", "Quantity", null, ImportFieldTypes.Decimal, true, false, 4, "100", MinValue: 0m, DecimalPlaces: 4,
                Aliases: ["qty", "on_hand", "stock", "unrestricted stock"]),
            new("unit_cost", "Unit Cost", "What one unit is valued at (base currency).", ImportFieldTypes.Decimal, true, false, 5, "95.50",
                MinValue: 0m, DecimalPlaces: 4, Aliases: ["cost", "unit_price", "valuation price", "moving price"]),
            new("batch_number", "Batch Number", "Required for batch-tracked products, empty otherwise.", ImportFieldTypes.Text, false, true, 6,
                null, MaxLength: 42, Aliases: ["batch", "lot", "lot_number"]),
            new("expiry_date", "Expiry Date", null, ImportFieldTypes.Date, false, false, 7, null, Aliases: ["expiry", "best before", "sled"])
        ],
        AllowUpdate: false);

    public async Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var skus = await _db.ProductVariants.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.OrganizationId == organizationId && v.IsActive && v.Product.IsActive && v.Product.IsStockable && !v.Product.IsSerialTracked)
            .OrderBy(v => v.Sku).Select(v => v.Sku).Take(ImportListKeys.MaxValues).ToListAsync(ct);
        var warehouses = await _db.Warehouses.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.OrganizationId == organizationId && w.IsActive)
            .OrderBy(w => w.Code).Select(w => w.Code).Take(ImportListKeys.MaxValues).ToListAsync(ct);
        var bins = await _db.Bins.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.OrganizationId == organizationId && b.IsActive)
            .Select(b => b.Code).Distinct().OrderBy(c => c).Take(ImportListKeys.MaxValues).ToListAsync(ct);
        return [new(ImportListKeys.Skus, skus), new(ImportListKeys.Warehouses, warehouses), new(ImportListKeys.Bins, bins)];
    }

    public async Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default) =>
        (await CheckAsync(organizationId, rows.Select(r => (r.RowNumber, r.Values)).ToList(), ct)).Checks.ToList();

    public async Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default)
    {
        var update = rows.FirstOrDefault(r => r.Action != "CREATE");
        if (update is not null)
            throw new UnprocessableEntityException($"Row {update.RowNumber}: opening stock is never updated by import (IMP-OB-06).");

        return await ImportUnitOfWork.RunAsync<IReadOnlyList<ImportRowOutcome>>(_db, async tx =>
        {
            var check = await CheckAsync(organizationId, rows.Select(r => (r.RowNumber, r.Values)).ToList(), ct);
            check.Checks.ThrowIfAny();
            var openingDate = check.OpeningDate!.Value;
            var entryDate = DateTime.SpecifyKind(openingDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

            var outcomes = new List<ImportRowOutcome>();
            foreach (var row in rows)
            {
                var p = check.Plans[row.RowNumber];
                var referenceId = Guid.NewGuid();

                var item = await StockRowAsync(organizationId, p, ct);
                if (item is null)
                {
                    item = new InventoryItem
                    {
                        Uuid           = Guid.NewGuid(),
                        OrganizationId = organizationId,
                        VariantId      = p.VariantId,
                        WarehouseId    = p.WarehouseId,
                        ZoneId         = p.ZoneId,
                        BinId          = p.BinId,
                        BatchNumber    = p.BatchNumber,
                        ExpiryDate     = p.ExpiryDate?.ToDateTime(TimeOnly.MinValue),
                        QtyOnHand      = p.Quantity,
                        UnitCost       = p.UnitCost,
                        LastUpdated    = DateTime.UtcNow
                    };
                    _db.InventoryItems.Add(item);
                }
                else
                {
                    var total = item.QtyOnHand + p.Quantity;
                    item.UnitCost = item.UnitCost is { } old && item.QtyOnHand > 0m && total > 0m
                        ? Math.Round((item.QtyOnHand * old + p.Quantity * p.UnitCost) / total, 4)
                        : p.UnitCost;
                    item.QtyOnHand = total;
                    if (p.BinId is not null && item.BinId is null) { item.BinId = p.BinId; item.ZoneId = p.ZoneId; }
                    if (p.ExpiryDate is { } e) item.ExpiryDate = e.ToDateTime(TimeOnly.MinValue);
                    item.LastUpdated = DateTime.UtcNow;
                }

                await _ledger.CreateEntryAsync(new LedgerEntryCommand
                {
                    VariantId       = p.VariantId,
                    WarehouseId     = p.WarehouseId,
                    TransactionType = TransactionType,
                    ReferenceType   = ReferenceType,
                    ReferenceId     = referenceId,
                    ReferenceNumber = p.ReferenceNumber,
                    QuantityIn      = p.Quantity,
                    UnitCost        = p.UnitCost,
                    TransactionDate = entryDate,
                    Notes           = p.BatchNumber is { } batch
                        ? $"Opening stock at {openingDate:yyyy-MM-dd}, batch {batch} (import row {row.RowNumber})"
                        : $"Opening stock at {openingDate:yyyy-MM-dd} (import row {row.RowNumber})",
                    CreatedBy       = userId,
                    SourceType      = "OPENING_BALANCE",
                    SourceName      = "Opening stock import",
                    DestinationType = "WAREHOUSE",
                    DestinationName = p.WarehouseName
                }, tx);
                StampOrganization(organizationId);

                // Saved per entry: the ledger reads the running balance from the database.
                await _db.SaveChangesAsync(ct);

                // A29 §11 — the Finance product ledger IN at the unit cost, in the same transaction (a zero opening moves nothing).
                if (_productLedger is not null && p.Quantity > 0m)
                    await _productLedger.AppendEntryAsync(new ProductLedgerPosting(
                        p.VariantUuid, ProductLedgerEntryType, ProductLedgerDirections.In, p.Quantity, p.UnitCost,
                        ReferenceType, referenceId, p.ReferenceNumber, userId,
                        ProductUuid: p.ProductUuid,
                        Narration: $"Opening stock, {p.WarehouseName}",
                        EntryDate: entryDate),
                        tx?.GetDbTransaction());

                outcomes.Add(new ImportRowOutcome(row.RowNumber, "IMPORTED", referenceId));
            }
            return outcomes;
        }, ct);
    }

    private async Task<InventoryItem?> StockRowAsync(Guid org, Plan p, CancellationToken ct)
    {
        if (p.BatchNumber is { } batch)
            return _db.InventoryItems.Local.FirstOrDefault(i => i.VariantId == p.VariantId && i.WarehouseId == p.WarehouseId && i.BatchNumber == batch)
                ?? await _db.InventoryItems.IgnoreQueryFilters().FirstOrDefaultAsync(i =>
                       i.OrganizationId == org && i.VariantId == p.VariantId && i.WarehouseId == p.WarehouseId && i.BatchNumber == batch, ct);

        return _db.InventoryItems.Local.FirstOrDefault(i => i.VariantId == p.VariantId && i.WarehouseId == p.WarehouseId
                                                          && i.BatchNumber == null && i.SerialNumber == null)
            ?? await _db.InventoryItems.IgnoreQueryFilters().FirstOrDefaultAsync(i =>
                   i.OrganizationId == org && i.VariantId == p.VariantId && i.WarehouseId == p.WarehouseId
                   && i.BatchNumber == null && i.SerialNumber == null, ct);
    }

    /// <summary>The ledger service relies on the ambient tenant to stamp its rows; an import names the organization itself.</summary>
    private void StampOrganization(Guid org)
    {
        foreach (var entry in _db.ChangeTracker.Entries<InventoryLedgerEntry>())
            if (entry.State == EntityState.Added && entry.Entity.OrganizationId == Guid.Empty)
                entry.Entity.OrganizationId = org;
    }

    // ── Checks ──────────────────────────────────────────────────────────────────

    private sealed record Plan(
        int VariantId, Guid VariantUuid, Guid ProductUuid, int WarehouseId, string WarehouseName, int? ZoneId, int? BinId,
        decimal Quantity, decimal UnitCost, string? BatchNumber, DateOnly? ExpiryDate, string ReferenceNumber);

    private sealed record Checked(RowChecks Checks, Dictionary<int, Plan> Plans, DateOnly? OpeningDate);

    private sealed record VariantInfo(
        int Id, Guid Uuid, string Sku, Guid ProductUuid, string ProductSku, bool IsDefault,
        bool IsStockable, bool IsBatchTracked, bool IsSerialTracked);

    private async Task<Checked> CheckAsync(
        Guid org, IReadOnlyList<(int RowNumber, IReadOnlyDictionary<string, string?> Values)> rows, CancellationToken ct)
    {
        var checks = new RowChecks(rows.Select(r => r.RowNumber));
        var plans = new Dictionary<int, Plan>();

        // D-7 — nothing is accepted before Finance has an opening balance date.
        DateOnly? openingDate = _openingDate is null ? null : await _openingDate.GetAsync(org, ct);
        if (openingDate is null)
        {
            foreach (var (row, _) in rows)
                checks.Error(row, string.Empty, ImportErrorCodes.OpeningDateNotSet, OpeningDateNotSetMessage);
            return new Checked(checks, plans, null);
        }

        var skus = rows.Select(r => Text(r.Values, "product_sku")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var variants = new Dictionary<string, VariantInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in skus.Chunk(Chunk))
        {
            var found = await _db.ProductVariants.IgnoreQueryFilters().AsNoTracking()
                .Where(v => v.OrganizationId == org && (chunk.Contains(v.Sku) || (v.IsDefault && chunk.Contains(v.Product.Sku))))
                .Select(v => new VariantInfo(v.Id, v.Uuid, v.Sku, v.Product.Uuid, v.Product.Sku, v.IsDefault,
                    v.Product.IsStockable, v.Product.IsBatchTracked, v.Product.IsSerialTracked))
                .ToListAsync(ct);
            foreach (var v in found.Where(v => chunk.Contains(v.Sku, StringComparer.OrdinalIgnoreCase))) variants.TryAdd(v.Sku, v);
            foreach (var v in found.Where(v => v.IsDefault)) variants.TryAdd(v.ProductSku, v);
        }

        var warehouses = await _db.Warehouses.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.OrganizationId == org).Select(w => new { w.Id, w.Code, w.Name, w.IsActive }).ToListAsync(ct);
        var bins = await _db.Bins.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.OrganizationId == org)
            .Select(b => new { b.Id, b.Code, b.ZoneId, ZoneCode = b.Zone.Code, b.Zone.WarehouseId, b.IsActive }).ToListAsync(ct);

        var variantIds = variants.Values.Select(v => v.Id).Distinct().ToList();
        var openings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in variantIds.Chunk(Chunk))
            foreach (var e in await _db.InventoryLedgerEntries.IgnoreQueryFilters().AsNoTracking()
                         .Where(e => e.OrganizationId == org && e.TransactionType == TransactionType && chunk.Contains(e.VariantId))
                         .Select(e => new { e.VariantId, e.WarehouseId, e.ReferenceNumber }).ToListAsync(ct))
                openings.Add($"{e.VariantId}|{e.WarehouseId}|{e.ReferenceNumber}");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (row, v) in rows)
        {
            var sku = Text(v, "product_sku");
            VariantInfo? variant = null;
            if (sku is null) checks.Error(row, "product_sku", ImportErrorCodes.Required, "Product SKU is required.");
            else if (!variants.TryGetValue(sku, out variant))
                checks.Error(row, "product_sku", ImportErrorCodes.LookupNotFound, $"SKU '{sku}' does not exist. Import products first.", sku);
            else if (!variant.IsStockable)
                checks.Error(row, "product_sku", ImportErrorCodes.NotStockable, $"'{sku}' is not a stocked product (e.g. a service), so it has no opening stock.", sku);
            else if (variant.IsSerialTracked)
                checks.Error(row, "product_sku", ImportErrorCodes.SerialTracked,
                    $"'{sku}' is serial-tracked: its opening units must be entered serial by serial (GRN or stock adjustment), not by import.", sku);

            var warehouseCode = Text(v, "warehouse_code");
            var warehouse = warehouseCode is null ? null
                : warehouses.FirstOrDefault(w => string.Equals(w.Code.Trim(), warehouseCode, StringComparison.OrdinalIgnoreCase));
            if (warehouseCode is null) checks.Error(row, "warehouse_code", ImportErrorCodes.Required, "Warehouse code is required.");
            else if (warehouse is null)
                checks.Error(row, "warehouse_code", ImportErrorCodes.LookupNotFound, $"Warehouse '{warehouseCode}' does not exist.", warehouseCode);
            else if (!warehouse.IsActive)
                checks.Error(row, "warehouse_code", ImportErrorCodes.LookupNotFound, $"Warehouse '{warehouseCode}' is inactive.", warehouseCode);

            int? zoneId = null, binId = null;
            if (Code(v, "location_code") is { } location && warehouse is not null)
            {
                var slash = location.IndexOf('/');
                var zonePart = slash > 0 ? location[..slash].Trim() : null;
                var binPart = slash > 0 ? location[(slash + 1)..].Trim() : location;
                var matches = bins.Where(b => b.WarehouseId == warehouse.Id && b.IsActive
                                           && string.Equals(b.Code.Trim(), binPart, StringComparison.OrdinalIgnoreCase)
                                           && (zonePart is null || string.Equals(b.ZoneCode?.Trim(), zonePart, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (matches.Count == 0)
                    checks.Error(row, "location_code", ImportErrorCodes.LookupNotFound,
                        $"Bin '{location}' does not exist in warehouse '{warehouseCode}'. Import warehouse locations first.", location);
                else if (matches.Count > 1)
                    checks.Error(row, "location_code", ImportErrorCodes.AmbiguousCode,
                        $"Bin '{location}' exists in {matches.Count} zones of '{warehouseCode}'; write it as ZONE/BIN.", location);
                else { binId = matches[0].Id; zoneId = matches[0].ZoneId; }
            }

            var quantity = Decimal(v, "quantity", out var qtyOk);
            if (!qtyOk) checks.Error(row, "quantity", ImportErrorCodes.InvalidType, "Quantity must be a number.", Text(v, "quantity"));
            else if (quantity is null) checks.Error(row, "quantity", ImportErrorCodes.Required, "Quantity is required.");
            else if (quantity < 0) checks.Error(row, "quantity", ImportErrorCodes.OutOfRange, "Opening quantity cannot be negative.", Format(quantity));
            else if (decimal.Round(quantity.Value, 4) != quantity) checks.Error(row, "quantity", "INVALID_PRECISION", "Quantity has more than 4 decimal places.", Format(quantity));

            var unitCost = Decimal(v, "unit_cost", out var costOk);
            if (!costOk) checks.Error(row, "unit_cost", ImportErrorCodes.InvalidType, "Unit cost must be a number.", Text(v, "unit_cost"));
            else if (unitCost is null) checks.Error(row, "unit_cost", ImportErrorCodes.Required, "Unit cost is required.");
            else if (unitCost < 0) checks.Error(row, "unit_cost", ImportErrorCodes.OutOfRange, "Unit cost cannot be negative.", Format(unitCost));
            else if (decimal.Round(unitCost.Value, 4) != unitCost) checks.Error(row, "unit_cost", "INVALID_PRECISION", "Unit cost has more than 4 decimal places.", Format(unitCost));

            var batch = Text(v, "batch_number");
            if (variant is { IsBatchTracked: true } && batch is null)
                checks.Error(row, "batch_number", ImportErrorCodes.Required, $"'{sku}' is batch-tracked: a batch number is required.");
            if (variant is { IsBatchTracked: false } && batch is not null)
                checks.Error(row, "batch_number", ImportErrorCodes.BatchNotTracked, $"'{sku}' is not batch-tracked; leave the batch number empty.", batch);

            var expiry = Date(v, "expiry_date", out var expiryOk);
            if (!expiryOk) checks.Error(row, "expiry_date", ImportErrorCodes.InvalidDate, "Expiry date is not a date (yyyy-MM-dd).", Text(v, "expiry_date"));

            if (variant is null || warehouse is null) continue;

            var referenceNumber = variant.IsBatchTracked && batch is not null ? $"OPENING/{batch}" : "OPENING";
            var key = $"{variant.Id}|{warehouse.Id}|{referenceNumber}";
            if (openings.Contains(key))
                checks.Error(row, "product_sku", ImportErrorCodes.DuplicateInDb,
                    $"Opening stock of '{sku}' in '{warehouse.Code}'{(variant.IsBatchTracked && batch is not null ? $", batch {batch}" : string.Empty)} is already recorded; an opening is imported once.", sku);
            else if (!seen.Add(key))
                checks.Error(row, "product_sku", ImportErrorCodes.DuplicateInFile,
                    $"Opening stock of '{sku}' in '{warehouse.Code}'{(batch is not null ? $", batch {batch}" : string.Empty)} appears more than once in the file.", sku);

            plans[row] = new Plan(variant.Id, variant.Uuid, variant.ProductUuid, warehouse.Id, warehouse.Name, zoneId, binId,
                quantity ?? 0m, unitCost ?? 0m, variant.IsBatchTracked ? batch : null, expiry, referenceNumber);
        }

        return new Checked(checks, plans, openingDate);
    }
}
