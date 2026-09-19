using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// Evidence that goods reached somebody — who took them, when, where, and the artefacts that
/// show it.
/// <para>
/// <b>Why it exists.</b> The only proof of delivery this system had was
/// <c>Shipment.ProofOfDeliveryUrl</c> (finding F47): 500 characters of free text on the legacy
/// table, pointing somewhere nobody controls, with no equivalent anywhere on the four-layer model
/// that replaced it. A link rots, and a proof that has rotted is worth less than no proof at all,
/// because it was relied on. What is stored here is the artefact itself.
/// </para>
/// <para>
/// <b>One per drop.</b> A courier parcel has one signature; a multi-stop run has one per stop. Both
/// are the same row with <see cref="ConsignmentStopId"/> either set or not, and a unique index makes
/// a second proof for the same drop impossible.
/// </para>
/// </summary>
internal class DeliveryProof : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int         ConsignmentId { get; set; }
    public Consignment Consignment   { get; set; } = null!;

    /// <summary>Which drop on a multi-stop run. Null means the consignment as a whole.</summary>
    public int?             ConsignmentStopId { get; set; }
    public ConsignmentStop? ConsignmentStop   { get; set; }

    /// <summary>
    /// Who took the goods.
    /// <para>
    /// <b>Nullable, and required anyway for anything keyed by a person.</b> A carrier scan that says
    /// DELIVERED and names nobody is still what the carrier told us, and refusing to store it would
    /// mean discarding the only record of the event. Somebody keying one in has the docket in front
    /// of them and has no such excuse, so the service demands a name from them and reports the
    /// carrier's silence as a warning instead of inventing a name to fill it.
    /// </para>
    /// </summary>
    public string? ReceivedBy { get; set; }

    /// <summary>
    /// How they relate to the consignee — "neighbour", "security guard", "reception". The difference
    /// between delivered and left with someone, and the first thing asked when a delivery is denied.
    /// </summary>
    public string? Relationship { get; set; }

    public DateTime DeliveredAt { get; set; }

    /// <summary>Where it was handed over, as the carrier reported it or as somebody recorded it.</summary>
    public string? Location { get; set; }

    /// <summary>See <see cref="ProofSource"/>. Stored as its code.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// The DELIVERED scan this came from, where one did. Null when a person recorded it — which is
    /// the only way a manual carrier is ever proved delivered.
    /// </summary>
    public int?                      TrackingEventId { get; set; }
    public ConsignmentTrackingEvent? TrackingEvent   { get; set; }

    public string? Notes { get; set; }

    public ICollection<DeliveryProofFile> Files { get; set; } = new List<DeliveryProofFile>();

    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}

/// <summary>
/// One artefact belonging to a proof — the signature, a photograph of the goods at the door, or a
/// scanned delivery note.
/// <para>
/// <b>The bytes, not a link.</b> This is the whole point of F47. A row here is useless without the
/// content, so the content is the row.
/// </para>
/// </summary>
internal class DeliveryProofFile : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int           DeliveryProofId { get; set; }
    public DeliveryProof DeliveryProof   { get; set; } = null!;

    /// <summary>See <see cref="ProofFileKind"/>. Stored as its code.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Normalised from the allowed set, and checked against the bytes — never as claimed.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Built here from values this system issued, never taken from an upload.</summary>
    public string FileName { get; set; } = string.Empty;

    public byte[] Content   { get; set; } = [];
    public int    SizeBytes { get; set; }

    /// <summary>SHA-256 of <see cref="Content"/>. The same artefact uploaded twice is stored once.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public bool     IsDelete    { get; set; }
    public int      CreatedBy   { get; set; }
    public DateTime CreatedDate { get; set; }
}
