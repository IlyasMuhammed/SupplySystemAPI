namespace SMS.Modules.Demand.Models;

// A32 C4 — manual reserve / release on sale order lines (PE-03/PE-06). See docs/sales-preorder/API-CONTRACT.md §5.
// Reservations go through IStockReservationService with source type SALES_ORDER, source = the order's UUID,
// source line = the line's UUID — the same rows confirm (AvailabilityCheckService), the GRN link, the delivery
// pipeline and ReservationExpirySweepJob already read and write.

/// <summary>POST /api/sale-orders/{uuid}/lines/{lineUuid}/reserve.</summary>
public class ReserveSaleOrderLineRequest
{
    /// <summary>How much to hold; the line's whole reservableQty when omitted. Must be &gt; 0 and ≤ reservableQty.</summary>
    public decimal? Quantity      { get; set; }
    /// <summary>
    /// False (default): when less is free than asked, nothing is held and the outcome is NEEDS_CONFIRMATION with
    /// availableQty — ask the user "Available X, required Y. Reserve partial?" and retry with true.
    /// True: hold whatever is free, up to the quantity (outcome PARTIAL when short).
    /// </summary>
    public bool     AllowPartial  { get; set; }
    /// <summary>Hold from this warehouse; the one with the most free stock when omitted (the confirm rule).</summary>
    public Guid?    WarehouseUuid { get; set; }
}

/// <summary>POST /api/sale-orders/{uuid}/lines/{lineUuid}/release.</summary>
public class ReleaseSaleOrderLineRequest
{
    /// <summary>How much to free; everything the order holds for the line when omitted.</summary>
    public decimal? Quantity { get; set; }
    public string?  Reason   { get; set; }
}

/// <summary>POST /api/sale-orders/{uuid}/reserve-all.</summary>
public class ReserveAllSaleOrderLinesRequest
{
    /// <summary>True (default) holds what is free on every line; false holds only lines that can be covered in full.</summary>
    public bool AllowPartial { get; set; } = true;
}

/// <summary>What one reserve/release did to one line, and where the line stands now.</summary>
public class SaleOrderLineReservationModel
{
    public Guid     LineUuid          { get; set; }
    public Guid     VariantUuid       { get; set; }
    /// <summary>RESERVED | PARTIAL | NEEDS_CONFIRMATION | NONE_AVAILABLE | RELEASED | SKIPPED.</summary>
    public string   Outcome           { get; set; } = string.Empty;
    /// <summary>What this call asked to hold (reserve) or free (release).</summary>
    public decimal  RequestedQty      { get; set; }
    /// <summary>What this call actually held (reserve) or freed (release).</summary>
    public decimal  ChangedQty        { get; set; }
    /// <summary>Free stock in the chosen warehouse when the call ran (reserve only).</summary>
    public decimal  AvailableQty      { get; set; }
    public Guid?    WarehouseUuid     { get; set; }
    public string?  WarehouseName     { get; set; }
    /// <summary>The line after the call — same meanings as on SaleOrderLineModel.</summary>
    public decimal  ReservedQty       { get; set; }
    public decimal  ReservableQty     { get; set; }
    public string   DeliveryIndicator { get; set; } = string.Empty;
    public string   LineStatus        { get; set; } = string.Empty;
    /// <summary>Why a line was SKIPPED or not covered, in words for the screen.</summary>
    public string?  Message           { get; set; }
}

public class SaleOrderReserveAllModel
{
    public List<SaleOrderLineReservationModel> Lines { get; set; } = [];
    public int ReservedLineCount { get; set; }
    public int PartialLineCount  { get; set; }
    /// <summary>Lines where nothing was held (NONE_AVAILABLE, NEEDS_CONFIRMATION or SKIPPED).</summary>
    public int UnchangedLineCount { get; set; }
}
