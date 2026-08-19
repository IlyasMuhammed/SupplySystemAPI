using System.Data.Common;

namespace SMS.Shared.Common;

/// <summary>
/// FSD Addendum 24 (ML-003) — implemented by SMS.Modules.Finance (MasterProductLedger lives in
/// finance_schema), consumed by SMS.Modules.Inventory's InventoryLedgerService without a direct
/// project reference — mirrors the ITraceIdResolver / IUserLookupService pattern: the interface
/// lives in Shared so a module can depend on it purely through DI.
/// </summary>
public interface IMasterProductLedgerService
{
    /// <summary>
    /// Adds a MasterProductLedger row for this movement. When <paramref name="transaction"/> is
    /// given, the implementation enlists its own DbContext into that ambient ADO.NET transaction
    /// (via SetDbConnection + UseTransaction) before saving, so the write is genuinely atomic with
    /// whatever business transaction the caller (a different module, a different DbContext) is
    /// already running — see InventoryLedgerService.CreateEntryAsync for the caller side.
    /// </summary>
    Task PostMovementAsync(ProductMovementContext context, DbTransaction? transaction = null);
}

/// <summary>Everything needed to record one product stock movement in the master product ledger.</summary>
public class ProductMovementContext
{
    // PV-005 — stock movements are recorded per variant now. ProductCode/ProductName stay
    // denormalised display fields (ProductCode holds the variant's SKU; ProductName holds the
    // parent product's name, plus the variant's name in parentheses for non-default variants).
    public int      VariantId       { get; set; }
    public string   ProductCode     { get; set; } = string.Empty;
    public string   ProductName     { get; set; } = string.Empty;
    // PV-008 — distinct VariantName/Sku alongside ProductCode/ProductName.
    public string?  VariantName     { get; set; }
    public string?  Sku             { get; set; }
    public int?     CategoryId      { get; set; }
    public string?  CategoryName    { get; set; }
    public int      WarehouseId     { get; set; }
    public string   WarehouseName   { get; set; } = string.Empty;

    public string   TransactionType { get; set; } = string.Empty;
    public string   ReferenceType   { get; set; } = string.Empty;
    public Guid     ReferenceId     { get; set; }
    public string   ReferenceNumber { get; set; } = string.Empty;

    public decimal? QuantityIn      { get; set; }
    public decimal? QuantityOut     { get; set; }
    public decimal  UnitCost        { get; set; }

    // SUPPLIER | WAREHOUSE | PROJECT | DEPARTMENT | ADJUSTMENT | ...
    public string   SourceType      { get; set; } = string.Empty;
    public string?  SourceName      { get; set; }
    public string   DestinationType { get; set; } = string.Empty;
    public string?  DestinationName { get; set; }

    public string?  Notes           { get; set; }
    public int      CreatedBy       { get; set; }
}
