namespace SMS.Modules.Demand.Services;

// A29-P5-06 §6.4 — what Warehouse tells Demand once a GRN has been approved and its stock posted:
// "this much of these variants just landed in this warehouse". Lives on the Demand side because
// everything it decides — whether the PO is one a sale order is waiting on, how much of that order's
// deficit is still open, what happens to the order's line — is sale order state, and Warehouse
// already depends on Demand (never the reverse), so Warehouse calls in through this interface.
public interface ISaleOrderGrnLinkService
{
    /// <summary>
    /// If the GRN's purchase order was raised for a sale order line, reserves what arrived against
    /// that line, moves the line OPEN to RESERVED once its whole deficit is covered, and records the
    /// receipt on the order's timeline and to its creator. Does nothing, and says so in the result,
    /// for any other PO. Must be called after the GRN's stock is posted — a reservation can only hold
    /// stock that is already on the shelf.
    /// </summary>
    Task<GrnReservationResult> ReserveForGrnAsync(GrnReceipt receipt);
}

/// <param name="Lines">Per variant, the quantity that actually went into stock — accepted quantity,
/// not merely delivered quantity, since rejected goods never reach the shelf to be reserved.</param>
/// <param name="ApprovedBy">0 when the approval carries no single user (the workflow audit log does).</param>
public sealed record GrnReceipt(
    Guid                        PoUuid,
    Guid                        GrnUuid,
    string                      GrnNumber,
    Guid                        WarehouseUuid,
    IReadOnlyList<GrnReceiptLine> Lines,
    int                         ApprovedBy);

public sealed record GrnReceiptLine(Guid VariantUuid, decimal PostedQty);

/// <param name="Linked">False when the PO is not one a sale order line is waiting on — nothing else
/// in the result means anything then.</param>
/// <param name="ReservedQty">Zero when the receipt was linked but nothing could be reserved — order
/// no longer open, deficit already covered, or the stock was taken between posting and reserving.</param>
/// <param name="RemainingDeficit">What the line still needs after this receipt.</param>
/// <param name="Note">Why nothing (or less than everything) was reserved, when that is the case.</param>
public sealed record GrnReservationResult(
    bool    Linked,
    Guid?   SaleOrderUuid,
    decimal ReservedQty,
    decimal RemainingDeficit,
    string? Note);
