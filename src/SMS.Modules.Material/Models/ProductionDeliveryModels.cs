namespace SMS.Modules.Material.Models;

/// <summary>
/// A34 C6 — <c>POST api/production-orders/{uuid}/create-delivery</c> ("Create delivery now", D-20;
/// docs/route-classification/API-CONTRACT.md §7).
/// </summary>
public class ProductionDeliveryHandoffModel
{
    public Guid    ProductionOrderUuid  { get; set; }
    /// <summary>Put on deliveries by this call; 0 on a repeat (the D-20 formula).</summary>
    public decimal QuantityCreated      { get; set; }
    /// <summary>The delivery this call created (the first, when the line's holds sit in several warehouses).</summary>
    public Guid?   DeliveryUuid         { get; set; }
    public string? DeliveryNumber       { get; set; }
    /// <summary>The latest non-cancelled delivery made from this production order, created now or earlier.</summary>
    public Guid?   LatestDeliveryUuid   { get; set; }
    public string? LatestDeliveryNumber { get; set; }
    /// <summary>Why nothing (or less than accepted) was created.</summary>
    public string? SkippedReason        { get; set; }
}
