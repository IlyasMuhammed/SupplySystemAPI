namespace SMS.Shared.Common;

/// <summary>
/// The codes written to <c>inventory.InventoryLedgerEntries.TransactionType</c>. A plain string
/// column, so adding a kind of movement is adding a constant here, not a migration (A30 M11).
/// </summary>
public static class InventoryTransactionType
{
    // Existing movements, named where they were previously inline strings.
    public const string GrnReceipt      = "GRN_RECEIPT";
    public const string StockAdjustment = "STOCK_ADJUSTMENT";
    public const string ReturnDispatch  = "RETURN_DISPATCH";
    /// <summary>A material issue voucher leaving the warehouse for a project site.</summary>
    public const string Issue           = "ISSUE";
    public const string TransferIn      = "TRANSFER_IN";
    public const string Wastage         = "WASTAGE";
    /// <summary>Unused site material coming back from a project (Material module).</summary>
    public const string MaterialReturn  = "MATERIAL_RETURN";
    public const string Return          = "RETURN";

    // A30 §16, §18, §19 — manufacturing. Prefixed PRODUCTION_ so they cannot be confused with the
    // project-site ISSUE / MATERIAL_RETURN above, which the spec's MATERIAL_ISSUE / MATERIAL_RETURN
    // names would have been.
    /// <summary>Materials issued from stock to the production floor against a production order.</summary>
    public const string ProductionIssue       = "PRODUCTION_ISSUE";
    /// <summary>Accepted production output received into finished-goods stock.</summary>
    public const string FinishedGoodsReceipt  = "FINISHED_GOODS_RECEIPT";
    /// <summary>Output rejected at quality inspection, or material scrapped on the floor.</summary>
    public const string ProductionScrap       = "PRODUCTION_SCRAP";
    /// <summary>Unused issued material returned from the production floor to stock.</summary>
    public const string ProductionReturn      = "PRODUCTION_RETURN";

    // A36 D-7 — service orders. Same shape as the production pair: an issue takes stock out, a return puts unused
    // issued material back.
    /// <summary>Materials issued from stock to a service order.</summary>
    public const string ServiceIssue          = "SERVICE_ISSUE";
    /// <summary>Unused issued material returned from a service order to stock.</summary>
    public const string ServiceReturn         = "SERVICE_RETURN";
}
