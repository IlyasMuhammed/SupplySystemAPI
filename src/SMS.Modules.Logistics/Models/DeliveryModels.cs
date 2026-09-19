namespace SMS.Modules.Logistics.Models;

// ── Address payload ───────────────────────────────────────────────────────────

/// <summary>
/// An address supplied inline on a delivery. Normalized on the way in — an unparseable phone or
/// an unknown city downgrades it to UNVALIDATED rather than rejecting the delivery.
/// </summary>
public class AddressRequest
{
    public string  Line1          { get; set; } = string.Empty;
    public string? Line2          { get; set; }
    public Guid?   CityId         { get; set; }
    public string  CityName       { get; set; } = string.Empty;
    public string? State          { get; set; }
    public string? PostalCode     { get; set; }
    public string  CountryName    { get; set; } = string.Empty;
    public string? CountryIsoCode { get; set; }
    public string? ContactName    { get; set; }
    public string? ContactPhone   { get; set; }
    public string? ContactEmail   { get; set; }
    public decimal? Latitude      { get; set; }
    public decimal? Longitude     { get; set; }
    public string? AddressType    { get; set; }
    public Guid?   ConsigneeUuid  { get; set; }
}

public class AddressModel
{
    public Guid    UUID             { get; set; }
    public string  Line1            { get; set; } = string.Empty;
    public string? Line2            { get; set; }
    public Guid?   CityId           { get; set; }
    public string  CityName         { get; set; } = string.Empty;
    public string? State            { get; set; }
    public string? PostalCode       { get; set; }
    public string  CountryName      { get; set; } = string.Empty;
    public string? CountryIsoCode   { get; set; }
    public string? ContactName      { get; set; }
    public string? ContactPhone     { get; set; }
    public string? ContactPhoneE164 { get; set; }
    public string? ContactEmail     { get; set; }
    public string  AddressType      { get; set; } = string.Empty;
    public string  ValidationStatus { get; set; } = string.Empty;
    public string? ValidationNotes  { get; set; }
}

// ── Requests ──────────────────────────────────────────────────────────────────

public class CreateDeliveryLineRequest
{
    public Guid?   VariantUuid     { get; set; }
    public Guid?   ProductUuid     { get; set; }
    public string  ItemDescription { get; set; } = string.Empty;
    public string? UnitOfMeasure   { get; set; }
    public decimal QtyOrdered      { get; set; }
    public string? BatchNumber     { get; set; }
    public string? SerialNumber    { get; set; }
    public Guid?   SourceLineUuid  { get; set; }
    public Guid?   BinUuid         { get; set; }
    public decimal? UnitValue      { get; set; }
    public bool    IsHazardous             { get; set; }
    public bool    IsFragile               { get; set; }
    public bool    IsTemperatureControlled { get; set; }
}

public class CreateDeliveryRequest
{
    /// <summary>PO, SRO, MIV, TRANSFER or MANUAL. Defaults to MANUAL.</summary>
    public string? SourceType   { get; set; }
    public Guid?   SourceUuid   { get; set; }
    public string? SourceNumber { get; set; }

    /// <summary>
    /// INBOUND, OUTBOUND or TRANSFER. Required only for MANUAL deliveries — every other source
    /// type implies its own direction, and supplying a conflicting one is an error rather than
    /// an override.
    /// </summary>
    public string? Direction { get; set; }

    public AddressRequest? ShipFromAddress { get; set; }
    public AddressRequest? ShipToAddress   { get; set; }

    /// <summary>
    /// Which stock ledgers move. A TRANSFER requires both, and they must differ; every other
    /// source type treats them as optional.
    /// </summary>
    public Guid? ShipFromWarehouseUuid { get; set; }
    public Guid? ShipToWarehouseUuid   { get; set; }

    public DateTime? RequestedDate { get; set; }
    public DateTime? PromisedDate  { get; set; }
    public string?   Priority      { get; set; }
    public string?   Incoterm      { get; set; }
    public string?   Notes         { get; set; }

    public List<CreateDeliveryLineRequest> Lines { get; set; } = [];
}

/// <summary>
/// Picks one source line into the new delivery. Omit the whole list to take every line that still
/// has an outstanding quantity.
/// </summary>
public class SourceLineSelection
{
    public Guid     SourceLineUuid { get; set; }
    /// <summary>How much to advise. Omit for the whole outstanding balance.</summary>
    public decimal? Qty            { get; set; }
}

public class CreateDeliveryFromSourceRequest
{
    /// <summary>PO, SRO, MIV or SALE_ORDER. MANUAL and TRANSFER deliveries use the plain create endpoint.</summary>
    public string SourceType { get; set; } = string.Empty;
    public Guid   SourceUuid { get; set; }

    /// <summary>
    /// SALE_ORDER only: SHIP or SELF_PICKUP. Omit to take the sale order's own mode. Each delivery
    /// of an order chooses independently — part of an order shipped and the rest collected is
    /// normal.
    /// </summary>
    public string? DeliveryMode { get; set; }

    /// <summary>
    /// For a SALE_ORDER, omit to ship from the warehouse the order's stock is reserved in.
    /// </summary>
    public Guid? ShipFromWarehouseUuid { get; set; }
    public Guid? ShipToWarehouseUuid   { get; set; }

    public AddressRequest? ShipFromAddress { get; set; }
    public AddressRequest? ShipToAddress   { get; set; }

    public DateTime? RequestedDate { get; set; }
    public DateTime? PromisedDate  { get; set; }
    public string?   Priority      { get; set; }
    public string?   Incoterm      { get; set; }
    public string?   Notes         { get; set; }

    /// <summary>Optional. Omit to advise every outstanding line in full.</summary>
    public List<SourceLineSelection>? Lines { get; set; }
}

/// <summary>
/// <c>POST /api/sale-orders/{id}/create-delivery</c> (A29 §7.8): a delivery for some or all of a
/// sale order's outstanding lines. The same options as the generic from-source create, with the
/// source already known. Every field is optional; an empty body delivers everything outstanding
/// in the order's own mode.
/// </summary>
public class CreateSaleOrderDeliveryRequest
{
    /// <summary>SHIP or SELF_PICKUP. Omit to take the sale order's own mode.</summary>
    public string? DeliveryMode { get; set; }

    /// <summary>Omit to ship from the warehouse the order's stock is reserved in.</summary>
    public Guid? ShipFromWarehouseUuid { get; set; }

    public AddressRequest? ShipFromAddress { get; set; }
    /// <summary>Omit to ship to the order's own address (SHIP) or nowhere (SELF_PICKUP).</summary>
    public AddressRequest? ShipToAddress   { get; set; }

    public DateTime? RequestedDate { get; set; }
    public DateTime? PromisedDate  { get; set; }
    public string?   Priority      { get; set; }
    public string?   Incoterm      { get; set; }
    public string?   Notes         { get; set; }

    /// <summary>
    /// Which sale order lines, and how much of each. <c>SourceLineUuid</c> is the sale order
    /// line's id. Omit to deliver every line's outstanding balance in full.
    /// </summary>
    public List<SourceLineSelection>? Lines { get; set; }
}

/// <summary>
/// Carries the reason for a hold, cancellation or short close. Required in every case.
/// </summary>
public class DeliveryReasonRequest
{
    public string Reason { get; set; } = string.Empty;
}

/// <summary>What to do when there is not enough stock to release everything.</summary>
public static class ShortageAction
{
    /// <summary>Refuse the release and reserve nothing. The default.</summary>
    public const string Block = "BLOCK";

    /// <summary>
    /// Release what can be covered and move the rest to a new draft delivery against the same
    /// source, so the shortfall is a document someone can act on rather than a failed attempt.
    /// </summary>
    public const string Split = "SPLIT";
}

public class ReleaseDeliveryRequest
{
    /// <summary>BLOCK (default) or SPLIT.</summary>
    public string? OnShortage { get; set; }
}

public class DeliveryAvailabilityLineModel
{
    public Guid    LineUuid        { get; set; }
    public int     LineNo          { get; set; }
    public string  ItemDescription { get; set; } = string.Empty;
    public Guid?   VariantUuid     { get; set; }
    public decimal QtyOrdered      { get; set; }

    /// <summary>
    /// What the best single warehouse can cover — the same rule the reservation itself uses.
    /// Summing across warehouses would promise stock no one warehouse can actually ship.
    /// </summary>
    public decimal QtyAvailable { get; set; }
    public decimal Shortfall    { get; set; }

    /// <summary>The warehouse that figure came from, so a picker knows where to look.</summary>
    public string? WarehouseName { get; set; }

    /// <summary>Why this line cannot be reserved at all, when that is the case.</summary>
    public string? Warning { get; set; }
}

public class DeliveryAvailabilityModel
{
    public Guid   DeliveryUuid   { get; set; }
    public string DeliveryNumber { get; set; } = string.Empty;
    public string Status         { get; set; } = string.Empty;

    /// <summary>False for inbound deliveries — goods are arriving, so there is nothing to hold.</summary>
    public bool RequiresStock { get; set; }

    public bool CanReleaseInFull    { get; set; }
    /// <summary>True when at least one line can be partly covered, so a split would achieve something.</summary>
    public bool CanReleasePartially { get; set; }

    public List<DeliveryAvailabilityLineModel> Lines { get; set; } = [];
}

/// <summary>
/// Who collected a self-pickup delivery (A29 §8.2). Recorded at the moment the goods leave the
/// building, so the gate pass can name them.
/// </summary>
public class RecordPickupRequest
{
    public string  PickupPersonName     { get; set; } = string.Empty;
    /// <summary>CNIC, LICENSE or PASSPORT.</summary>
    public string  PickupPersonIdType   { get; set; } = string.Empty;
    public string  PickupPersonIdNumber { get; set; } = string.Empty;
    /// <summary>Optional: the letter or person authorising a collector who is not the customer.</summary>
    public string? PickupAuthorization  { get; set; }
}

public class PickupResultModel
{
    public Guid     DeliveryUuid   { get; set; }
    public string   DeliveryNumber { get; set; } = string.Empty;
    public string   Status         { get; set; } = string.Empty;
    public DateTime PickedUpAt     { get; set; }
    public string   PickupPersonName { get; set; } = string.Empty;

    /// <summary>
    /// Set when this call is what took the stock off the books. Null when the goods had already
    /// been issued before the customer arrived.
    /// </summary>
    public GoodsIssueResultModel? GoodsIssue { get; set; }

    /// <summary>
    /// The sale order's status once this collection was counted — PARTIALLY_FULFILLED or FULFILLED.
    /// Null when the order could not be updated (logged for reconciliation).
    /// </summary>
    public string? SaleOrderStatus { get; set; }
}

/// <summary>What the goods issue did — and, just as importantly, what it deliberately did not do.</summary>
public class GoodsIssueResultModel
{
    public Guid   DeliveryUuid   { get; set; }
    public string DeliveryNumber { get; set; } = string.Empty;
    public string Status         { get; set; } = string.Empty;

    /// <summary>
    /// False when the source document already posted this movement — an MIV issue or an SRO
    /// dispatch. The delivery then records the movement rather than repeating it.
    /// </summary>
    public bool PostedStock { get; set; }

    /// <summary>Ledger entries written. Two per line for a transfer: one out, one in.</summary>
    public int MovementsPosted { get; set; }

    public int ReservationsClosed { get; set; }

    public decimal QtyShipped { get; set; }
    public decimal QtyOut     { get; set; }
    public decimal QtyIn      { get; set; }

    /// <summary>Set when stock was not posted, explaining which document posted it instead.</summary>
    public string? Note { get; set; }
}

public class PatchDeliveryRequest
{
    public DateTime? RequestedDate { get; set; }
    public DateTime? PromisedDate  { get; set; }
    public string?   Priority      { get; set; }
    public string?   Incoterm      { get; set; }
    public string?   Notes         { get; set; }
    public AddressRequest? ShipToAddress { get; set; }
}

// ── Responses ─────────────────────────────────────────────────────────────────

public class DeliveryLineModel
{
    public Guid    UUID            { get; set; }
    public int     LineNo          { get; set; }
    public Guid?   VariantUuid     { get; set; }
    public Guid?   ProductUuid     { get; set; }
    public string  ItemDescription { get; set; } = string.Empty;
    public string? UnitOfMeasure   { get; set; }
    public decimal QtyOrdered      { get; set; }
    public decimal QtyPicked       { get; set; }
    public decimal QtyPacked       { get; set; }
    public decimal QtyShipped      { get; set; }
    public decimal QtyDelivered    { get; set; }
    public decimal QtyShort        { get; set; }
    public string? ShortReason     { get; set; }
    public string? BatchNumber     { get; set; }
    public string? SerialNumber    { get; set; }
    public Guid?   SourceLineUuid  { get; set; }
    /// <summary>The sale order line this line fulfils. Null unless the delivery is for a sale order.</summary>
    public Guid?   SoLineUuid      { get; set; }
    public decimal? UnitValue      { get; set; }
    public bool    IsHazardous             { get; set; }
    public bool    IsFragile               { get; set; }
    public bool    IsTemperatureControlled { get; set; }
}

public class DeliveryListItemModel
{
    public Guid      UUID           { get; set; }
    public string    DeliveryNumber { get; set; } = string.Empty;
    public string    Direction      { get; set; } = string.Empty;
    public string    SourceType     { get; set; } = string.Empty;
    public string?   SourceNumber   { get; set; }
    /// <summary>SHIP or SELF_PICKUP for a sale-order delivery; null otherwise.</summary>
    public string?   DeliveryMode   { get; set; }
    public string    Status         { get; set; } = string.Empty;
    public string    Priority       { get; set; } = string.Empty;
    public DateTime? RequestedDate  { get; set; }
    public DateTime? PromisedDate   { get; set; }
    public string?   ShipToCity     { get; set; }
    public int       LineCount      { get; set; }

    /// <summary>
    /// True for rows backfilled from the legacy shipments table, which had no lines. The cockpit
    /// must show this — a delivery with zero lines is otherwise indistinguishable from a
    /// complete one.
    /// </summary>
    public bool LinesUnknown { get; set; }

    public DateTime CreatedDate { get; set; }
}

public class DeliveryDetailModel
{
    public Guid      UUID           { get; set; }
    public string    DeliveryNumber { get; set; } = string.Empty;
    public Guid      TraceId        { get; set; }
    public string    Direction      { get; set; } = string.Empty;
    public string    SourceType     { get; set; } = string.Empty;
    public Guid?     SourceUuid     { get; set; }
    public string?   SourceNumber   { get; set; }

    /// <summary>Derived from the source type, never stored. See <c>DeliverySourceTypeInfo</c>.</summary>
    public bool PostsGoodsIssue { get; set; }

    /// <summary>Set only when the delivery fulfils a sale order.</summary>
    public Guid?   SaleOrderUuid { get; set; }
    /// <summary>SHIP or SELF_PICKUP for a sale-order delivery; null otherwise.</summary>
    public string? DeliveryMode  { get; set; }

    /// <summary>Who collected a self-pickup delivery, and when. Null until it has been collected.</summary>
    public string?   PickupPersonName     { get; set; }
    public string?   PickupPersonIdType   { get; set; }
    public string?   PickupPersonIdNumber { get; set; }
    public string?   PickupAuthorization  { get; set; }
    public DateTime? PickedUpAt           { get; set; }

    public AddressModel? ShipFromAddress { get; set; }
    public AddressModel? ShipToAddress   { get; set; }

    public DateTime? RequestedDate { get; set; }
    public DateTime? PromisedDate  { get; set; }
    public string    Priority      { get; set; } = string.Empty;
    public string?   Incoterm      { get; set; }
    public string    Status        { get; set; } = string.Empty;
    public string?   StatusBeforeHold { get; set; }
    public string?   HoldReason       { get; set; }
    public bool      LinesUnknown  { get; set; }
    public string?   Notes         { get; set; }

    /// <summary>The statuses this delivery may legally move to next, straight from the state machine.</summary>
    public List<string> AllowedNextStatuses { get; set; } = [];

    public DateTime  CreatedDate  { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public List<DeliveryLineModel> Lines { get; set; } = [];
}

public class DeliveryFilter
{
    public string? Status     { get; set; }
    public string? Direction  { get; set; }
    public string? SourceType { get; set; }
    public string? Search     { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate   { get; set; }
    public int Page     { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
