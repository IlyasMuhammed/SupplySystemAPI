namespace SMS.Shared.Common;

/// <summary>
/// What raised a production order (A30 §11.1 source_type). In <c>SMS.Shared.Common</c> rather than
/// Material's own domain because a caller with no project reference to Material — Sales, since
/// Phase 4 Track C — needs to name one of these to pass to <see cref="IProductionDemandService"/>.
/// </summary>
public static class ProductionSourceType
{
    public const string Manual                 = "MANUAL";
    public const string SalesOrder             = "SALES_ORDER";
    public const string FulfillmentRequirement = "FULFILLMENT_REQ";
    public const string Replenishment          = "REPLENISHMENT";
    /// <summary>A child order raised for a manufactured input another order was short of (chained manufacturing).</summary>
    public const string SupplyRequirement      = "SUPPLY_REQUIREMENT";

    public static readonly IReadOnlyList<string> All = [Manual, SalesOrder, FulfillmentRequirement, Replenishment, SupplyRequirement];
}
