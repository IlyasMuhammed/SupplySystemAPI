using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// A structured, reusable postal address.
/// <para>
/// This replaces <c>Shipment.DestinationAddress</c>, a single <c>nvarchar(300)</c> of free text.
/// No courier API accepts that: booking needs a city, a postal code and a reachable phone number
/// as separate, validated fields.
/// </para>
/// <para>
/// Addresses are <b>snapshots, not master data</b>. Two deliveries to the same place produce two
/// rows. That is deliberate: a delivery has to keep the address it actually shipped to, and
/// de-duplicating would let editing a shared row silently rewrite where a past shipment went.
/// </para>
/// </summary>
internal class Address : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UUID           { get; set; }

    // ── Postal ────────────────────────────────────────────────────────────────

    public string  Line1      { get; set; } = string.Empty;
    public string? Line2      { get; set; }

    /// <summary>
    /// Reference to <c>lookups.Cities</c>. A bare Guid with no foreign key — Cities lives in
    /// another DbContext and another schema, matching the <c>GrnLine.PoLineUuid</c> convention
    /// used everywhere else for cross-module references.
    /// </summary>
    public Guid? CityId { get; set; }

    /// <summary>
    /// The city as text. Required, and kept even when <see cref="CityId"/> resolves — an address
    /// must stay readable without a join, and the legacy rows backfilled in T-16 have text and
    /// no id at all.
    /// </summary>
    public string CityName { get; set; } = string.Empty;

    public string? State      { get; set; }
    public string? PostalCode { get; set; }

    public Guid?  CountryId   { get; set; }
    public string CountryName { get; set; } = string.Empty;

    /// <summary>
    /// ISO-3166 alpha-2, e.g. "PK". Held separately from the Lookups country because that
    /// catalog's <c>Code</c> is free text entered through an admin screen with only a uniqueness
    /// check — it is not reliably ISO. This field is what phone normalization and, later, carrier
    /// booking actually use, so it is validated on the way in rather than trusted.
    /// </summary>
    public string? CountryIsoCode { get; set; }

    // ── Contact ───────────────────────────────────────────────────────────────

    public string? ContactName { get; set; }

    /// <summary>The phone exactly as entered, never rewritten. Kept so a failed normalization can be retried or corrected by hand.</summary>
    public string? ContactPhone { get; set; }

    /// <summary>The normalized E.164 form, or null when it could not be parsed.</summary>
    public string? ContactPhoneE164 { get; set; }

    public string? ContactEmail { get; set; }

    // ── Geo ───────────────────────────────────────────────────────────────────

    public decimal? Latitude  { get; set; }
    public decimal? Longitude { get; set; }

    // ── Classification ────────────────────────────────────────────────────────

    /// <summary>See <see cref="Domain.AddressType"/>. Stored as its code.</summary>
    public string AddressType { get; set; } = LogisticsCode.Of(Domain.AddressType.Other);

    /// <summary>
    /// The warehouse, supplier, project — or, once one exists, customer — this address belongs
    /// to. Null for a one-off address typed straight onto a delivery.
    /// </summary>
    public Guid? ConsigneeUuid { get; set; }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>See <see cref="AddressValidationStatus"/>. Stored as its code.</summary>
    public string ValidationStatus { get; set; } =
        LogisticsCode.Of(AddressValidationStatus.Unvalidated);

    /// <summary>
    /// Why the address is not VALID, in plain words, for the operations queue that has to fix it.
    /// </summary>
    public string? ValidationNotes { get; set; }

    // ── Audit ─────────────────────────────────────────────────────────────────

    public bool      IsActive     { get; set; } = true;
    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}
