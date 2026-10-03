namespace SMS.Modules.Inventory.Models;

// A33 C2 — a variant's default fulfillment route (docs/fulfillment-routes/API-CONTRACT.md §4).

/// <summary>Body of <c>PUT api/variants/{uuid}/fulfillment-route</c>. Null clears the route (T-C2-04).</summary>
public class SetVariantFulfillmentRouteRequest
{
    public Guid? FulfillmentRouteUuid { get; set; }
}

/// <summary>What <c>PUT api/variants/{uuid}/fulfillment-route</c> returns.</summary>
public class VariantFulfillmentRouteModel
{
    public Guid VariantUuid { get; set; }
    public Guid? FulfillmentRouteUuid { get; set; }
    public string? FulfillmentRouteCode { get; set; }
    public string? FulfillmentRouteName { get; set; }
}

/// <summary>
/// Body of <c>POST api/fulfillment-routes/{uuid}/assign-by-category</c> (D-14). Categories have int ids and no UUID.
/// Without <see cref="SubCategoryId"/> the whole category is covered, its sub-categories included.
/// </summary>
public class AssignRouteByCategoryRequest
{
    public int CategoryId { get; set; }
    public int? SubCategoryId { get; set; }
}

/// <summary>
/// Outcome of a bulk assign. <see cref="Total"/> is every active variant of an active product in scope;
/// <see cref="Skipped"/> already had a route and kept it (BR-C2-02).
/// </summary>
public class FulfillmentRouteBulkAssignResult
{
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Total { get; set; }
}
