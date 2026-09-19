namespace SMS.Shared.Common;

/// <summary>One line of a delivery, as invoicing needs to read it.</summary>
/// <param name="SoLineUuid">The sale order line this line fulfilled. Null on a delivery that is not for a sale order.</param>
/// <param name="Description">The readable item description carried on the delivery line.</param>
/// <param name="QtyDelivered">What actually reached the customer — the ceiling on what may be billed for this line.</param>
public sealed record DeliveredLineForInvoicing(
    Guid    DeliveryLineUuid,
    int     LineNo,
    Guid?   SoLineUuid,
    Guid?   VariantUuid,
    string  Description,
    decimal QtyDelivered);

/// <param name="Status">The delivery's status code — DELIVERED and CLOSED are the ones that have reached the customer.</param>
/// <param name="SaleOrderUuid">Null for a delivery that is not for a sale order (a PO advice, a transfer).</param>
public sealed record DeliveryForInvoicing(
    Guid    DeliveryUuid,
    string  DeliveryNumber,
    string  Status,
    Guid?   SaleOrderUuid,
    IReadOnlyList<DeliveredLineForInvoicing> Lines);

/// <summary>
/// Reads a delivery for the module that bills it (A29 §9.5: "invoice only against delivered /
/// picked-up lines").
/// <para>
/// A contract rather than a project reference because Finance and Logistics do not depend on each
/// other — Logistics already reaches Finance through <see cref="ISupplierInvoicePoster"/>, and this is
/// the same arrangement in the other direction. Implemented in SMS.Modules.Logistics and resolved
/// through DI; tenant-scoped, so another organization's delivery reads as not found.
/// </para>
/// </summary>
public interface IDeliveryFulfillmentReader
{
    /// <summary>The delivery, or null when it does not exist (or belongs to another organization).</summary>
    Task<DeliveryForInvoicing?> GetAsync(Guid deliveryUuid, CancellationToken ct = default);
}
