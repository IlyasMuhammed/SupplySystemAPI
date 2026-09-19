using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// One contract with one carrier: the account a booking goes out on.
/// <para>
/// <b>Why this is not just more columns on <see cref="Carrier"/>.</b> A carrier row is the
/// organization's record of <em>who</em> the carrier is — it is read by every list, every
/// consignment and every report. An account is the far narrower thing a booking needs, and there
/// is genuinely more than one: a domestic contract and an international one, a sandbox and a live
/// account, a separate account per site. Folding them together would mean one carrier can only
/// ever be dealt with one way, which is a rewrite the first time that is untrue.
/// </para>
/// <para>
/// <b>Credentials are deliberately not here.</b> They arrive in T-35 as their own table, encrypted
/// from the first write. Putting them on this row now would mean plaintext secrets in a database
/// and a migration to fix it later — and every query that lists accounts would be touching
/// ciphertext it has no business reading.
/// </para>
/// </summary>
internal class CarrierAccount : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int     CarrierId { get; set; }
    public Carrier Carrier   { get; set; } = null!;

    /// <summary>What a human calls it — "Domestic", "International", "Sandbox".</summary>
    public string AccountName { get; set; } = string.Empty;

    /// <summary>
    /// The carrier's own account identifier. Not a secret — it appears on invoices and consignment
    /// notes — so it lives here rather than in the vault.
    /// </summary>
    public string? AccountNumber { get; set; }

    /// <summary>
    /// Used when a consignment names no account. Exactly one per carrier, enforced on write: two
    /// defaults means the account a booking goes out on depends on row order.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// A test account at the carrier. Kept as data rather than inferred from the environment,
    /// because a sandbox account in production is a legitimate setup while an integration is being
    /// proven — and the screens need to say so loudly.
    /// </summary>
    public bool IsSandbox { get; set; }

    /// <summary>The service to book when the consignment does not name one.</summary>
    public string? DefaultServiceCode { get; set; }

    // ── What this account may be asked for ────────────────────────────────────
    //
    // Null means "whatever the adapter says". False switches something off for this account. An
    // account can never switch something *on* that the adapter cannot do — see
    // CarrierCapabilityResolver, where the narrowing is enforced rather than merely intended.

    public bool? CodEnabled            { get; set; }
    public bool? LabelsEnabled         { get; set; }
    public bool? TrackingEnabled       { get; set; }
    public bool? CancellationEnabled   { get; set; }
    public bool? PickupBookingEnabled  { get; set; }

    public string? Notes { get; set; }

    public bool      IsActive     { get; set; } = true;
    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}
