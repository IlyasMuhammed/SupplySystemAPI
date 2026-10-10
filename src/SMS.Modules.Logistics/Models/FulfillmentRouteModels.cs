namespace SMS.Modules.Logistics.Models;

// A33 C1 — request/response shapes for api/fulfillment-routes (docs/fulfillment-routes/API-CONTRACT.md §3).

public class FulfillmentRouteStepRequest
{
    /// <summary>PICK | PACK | STAGE | APPROVAL | GOODS_ISSUE | SHIP.</summary>
    public string  StepCode    { get; set; } = string.Empty;

    /// <summary>1, 2, 3 … with no gaps (BR-C1-04). Steps are sorted by it before validation.</summary>
    public int     StepOrder   { get; set; }

    /// <summary>Forced true for PICK and GOODS_ISSUE.</summary>
    public bool    IsMandatory { get; set; } = true;

    /// <summary>Max 200.</summary>
    public string? Description { get; set; }
}

public class CreateFulfillmentRouteRequest
{
    /// <summary>Max 30, upper-cased by the server, letters/digits/underscores; unique per organization; never changes.</summary>
    public string  Code        { get; set; } = string.Empty;

    /// <summary>Max 100.</summary>
    public string  Name        { get; set; } = string.Empty;

    /// <summary>Max 500.</summary>
    public string? Description { get; set; }

    /// <summary>Last + 10 when omitted.</summary>
    public int?    DisplayOrder { get; set; }

    public List<FulfillmentRouteStepRequest> Steps { get; set; } = [];

    /// <summary>A34 — STOCK | MANUFACTURE (trimmed, upper-cased); omitted / blank = STOCK. BUY / DROPSHIP are refused (D-7).</summary>
    public string? RouteCategory { get; set; }
}

/// <summary>Replaces name, description, display order and (custom routes only) the steps. The code never changes.</summary>
public class UpdateFulfillmentRouteRequest
{
    public string  Name         { get; set; } = string.Empty;
    public string? Description  { get; set; }
    public int     DisplayOrder { get; set; }

    /// <summary>
    /// Null = leave the steps as they are. On a system route the steps are locked (D-10): sending steps that differ
    /// from the current ones is a 400.
    /// </summary>
    public List<FulfillmentRouteStepRequest>? Steps { get; set; }

    /// <summary>
    /// A34 — null / blank = leave the category as it is. A change is refused (409) on a system, default or in-use route
    /// (D-8); BUY / DROPSHIP are refused (400, D-7).
    /// </summary>
    public string? RouteCategory { get; set; }
}

public class FulfillmentRouteStepModel
{
    public string  StepCode    { get; set; } = string.Empty;
    /// <summary>"Pick", "Pack", "Stage", "Approval", "Goods Issue", "Ship".</summary>
    public string  Label       { get; set; } = string.Empty;
    public int     StepOrder   { get; set; }
    public bool    IsMandatory { get; set; }
    public string? Description { get; set; }
}

/// <summary>A37 D-12 — one row of <c>GET /api/products/{id}/routes</c> (API-CONTRACT §4).</summary>
public class ProductVariantRouteModel
{
    public Guid    VariantUuid        { get; set; }
    public string  VariantName        { get; set; } = string.Empty;
    public string  Sku                { get; set; } = string.Empty;
    /// <summary>The variant's configured default route; null when it has none.</summary>
    public Guid?   RouteUuid          { get; set; }
    public string? RouteName          { get; set; }
    public string? Category           { get; set; }
    /// <summary>False when the configured route needs a module that is switched off. True when nothing is configured.</summary>
    public bool    IsAvailable        { get; set; } = true;
    public string? UnavailableReason  { get; set; }
    /// <summary>What a shipped order line would use: the configured route when available, else the SHIP default.</summary>
    public Guid?   EffectiveRouteUuid { get; set; }
    public string? EffectiveRouteName { get; set; }
    public string? Warning            { get; set; }
}

public class FulfillmentRouteModel
{
    public Guid    Uuid        { get; set; }
    public string  Code        { get; set; } = string.Empty;
    public string  Name        { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>The organization's default for its class: SHIP orders when <see cref="RequiresShipping"/>, else SELF_PICKUP orders (D-4).</summary>
    public bool IsDefault        { get; set; }
    public bool IsActive         { get; set; }
    public bool IsSystem         { get; set; }
    public bool RequiresPacking  { get; set; }
    public bool RequiresShipping { get; set; }
    public int  DisplayOrder     { get; set; }

    /// <summary>A34 — STOCK | MANUFACTURE (| BUY | DROPSHIP, reserved). A MANUFACTURE route is never a default (D-6).</summary>
    public string RouteCategory  { get; set; } = SMS.Shared.Common.FulfillmentRouteCategory.Stock;

    /// <summary>A37 D-12 — false for a MANUFACTURE route while Manufacturing is switched off; pickers hide it.</summary>
    public bool    IsAvailable       { get; set; } = true;
    /// <summary>A37 — "Manufacturing is switched off" when <see cref="IsAvailable"/> is false.</summary>
    public string? UnavailableReason { get; set; }

    public List<FulfillmentRouteStepModel> Steps { get; set; } = [];

    /// <summary>"Pick → Pack → Goods Issue → Ship".</summary>
    public string StepsText { get; set; } = string.Empty;

    /// <summary>The delivery statuses the route is seen to pass through, DRAFT … DELIVERED (the editor's preview line).</summary>
    public List<string> StatusPath { get; set; } = [];

    public DateTime  CreatedDate  { get; set; }
    public DateTime? ModifiedDate { get; set; }
}
