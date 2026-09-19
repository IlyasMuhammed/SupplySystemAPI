using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

internal class Carrier : ITenantScopedEntity
{
    public int    Id                  { get; set; }
    public Guid   OrganizationId      { get; set; }
    public Guid   UUID                { get; set; }
    public string Name                { get; set; } = string.Empty;
    public string Code                { get; set; } = string.Empty;
    public string? ServiceType        { get; set; }
    public string? TrackingUrlTemplate { get; set; }
    public string? ContactName        { get; set; }
    public string? ContactPhone       { get; set; }
    public string? ContactEmail       { get; set; }
    // RatePerKg was retired with finding F39: it was stored, shown on screen, and multiplied by
    // nothing. Rate cards (T-46) are what price carriage now.

    /// <summary>
    /// Selects the courier adapter at runtime (Phase 2). Nullable, and null means MANUAL —
    /// every carrier that existed before this column did is treated that way, and so is any row
    /// written by a path that does not know about it.
    /// </summary>
    public string? ProviderKey { get; set; }

    /// <summary>See <see cref="CarrierIntegrationMode"/>. Null means MANUAL.</summary>
    public string? IntegrationMode { get; set; }

    /// <summary>
    /// The supplier this carrier is, in Finance's books (decision G10). A bare UUID with no foreign
    /// key, like every other cross-module reference here.
    /// <para>
    /// <b>Null means its bills cannot be paid through the system.</b> A carrier is somebody the
    /// company owes money to, and Finance knows about suppliers, not carriers — without this link
    /// an approved freight bill reaches no payables ledger, which is the gap G10 closed. Most
    /// organizations already have the carrier set up as a supplier; this records which one.
    /// </para>
    /// </summary>
    public Guid? SupplierId { get; set; }

    /// <summary>Standard Carrier Alpha Code, or the local licence number where there is no SCAC.</summary>
    public string? ScacCode { get; set; }

    /// <summary>ISO 4217, for freight quoted in something other than the organization's currency.</summary>
    public string? DefaultCurrency { get; set; }

    public string  Status             { get; set; } = "Active";
    public bool    IsActive           { get; set; } = true;
    public bool    IsDelete           { get; set; }
    public int     CreatedBy          { get; set; }
    public DateTime CreatedDate       { get; set; }
    public int?    ModifiedBy         { get; set; }
    public DateTime? ModifiedDate     { get; set; }

    public ICollection<Shipment> Shipments { get; set; } = new List<Shipment>();
}

internal class Shipment : ITenantScopedEntity
{
    public int    Id                   { get; set; }
    public Guid   OrganizationId       { get; set; }
    public Guid   UUID                 { get; set; }
    public string ShipmentNumber       { get; set; } = string.Empty;
    public Guid   PoUuid               { get; set; }
    public string PoNumber             { get; set; } = string.Empty;
    public int?   CarrierId            { get; set; }
    public string? CarrierName         { get; set; }
    public string  ShipmentType        { get; set; } = "Courier";
    public DateTime DispatchDate       { get; set; }
    public DateTime EstimatedArrival   { get; set; }
    public DateTime? ActualArrival     { get; set; }
    public string? TrackingNumber      { get; set; }
    public string? TrackingUrl         { get; set; }
    public Guid?  OriginWarehouseUuid  { get; set; }
    public string DestinationAddress   { get; set; } = string.Empty;
    public decimal? WeightKg           { get; set; }
    public decimal? VolumeCbm          { get; set; }
    public decimal? FreightCost        { get; set; }
    public string  Status              { get; set; } = "Preparing";
    // ProofOfDeliveryUrl was retired with finding F47 — a path into wwwroot is a proof only until
    // the next redeploy. delivery_proofs (T-61) holds the artefact itself.
    public string? Notes               { get; set; }
    public bool    IsActive            { get; set; } = true;
    public bool    IsDelete            { get; set; }
    public int     CreatedBy           { get; set; }
    public DateTime CreatedDate        { get; set; }
    public int?    ModifiedBy          { get; set; }
    public DateTime? ModifiedDate      { get; set; }

    public Carrier? Carrier { get; set; }
}
