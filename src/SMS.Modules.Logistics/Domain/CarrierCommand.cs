using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// One call to a carrier that changes something there — a booking or a cancellation — written
/// <em>before</em> the call goes out.
/// <para>
/// <b>Why it exists.</b> A booking call that times out may have created a real parcel. Without a
/// record made before the call, a retry cannot ask "did this already happen?", and the honest
/// answer to a timeout becomes a second booking, a second label and a second invoice. This row is
/// what a retry consults instead.
/// </para>
/// <para>
/// <b>Never deleted.</b> There is no <c>IsDelete</c>: when a carrier disputes a booking months
/// later, this is the record of what was asked, when, how many times, and what came back.
/// </para>
/// </summary>
internal class CarrierCommand : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>See <see cref="Domain.CarrierCommandType"/>. Stored as its code.</summary>
    public string CommandType { get; set; } = string.Empty;

    /// <summary>
    /// The key sent to the carrier. Unique per organization and command type — the database, not
    /// the code, is what stops two rows claiming one key.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the request, credentials excluded. A key presented again with a different
    /// request is refused: reusing a key for a changed booking would let the carrier's
    /// deduplication hand back the <em>old</em> parcel for the new request.
    /// </summary>
    public string RequestFingerprint { get; set; } = string.Empty;

    public int         ConsignmentId { get; set; }
    public Consignment Consignment   { get; set; } = null!;

    /// <summary>Null for a manual carrier, which needs no account.</summary>
    public int?            CarrierAccountId { get; set; }
    public CarrierAccount? CarrierAccount   { get; set; }

    /// <summary>The adapter the call went through, as it was when the call was made.</summary>
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>See <see cref="CarrierCommandStatus"/>. Stored as its code.</summary>
    public string Status { get; set; } = string.Empty;

    // ── Attempts ──────────────────────────────────────────────────────────────

    /// <summary>How many times the call has been sent. More than one means the carrier deduplicated.</summary>
    public int      AttemptCount   { get; set; }
    public DateTime FirstAttemptAt { get; set; }
    public DateTime LastAttemptAt  { get; set; }

    /// <summary>
    /// While <c>IN_FLIGHT</c>, when the worker's claim runs out. A worker that dies mid-call never
    /// reports back; past this point the call is treated as <c>UNKNOWN</c> rather than left
    /// in flight forever, blocking every retry.
    /// </summary>
    public DateTime? LeaseExpiresAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    // ── What came back ────────────────────────────────────────────────────────

    public string?  AwbNumber        { get; set; }
    public string?  CarrierReference { get; set; }
    public string?  TrackingUrl      { get; set; }
    public decimal? Cost             { get; set; }
    public string?  CostCurrency     { get; set; }
    public string?  CarrierErrorCode { get; set; }
    public string?  Message          { get; set; }

    /// <summary>What the carrier actually said. The parsed fields never settle a dispute.</summary>
    public string? RawResponse { get; set; }

    /// <summary>
    /// Why the outcome is unknown — the exception type and message, or an expired lease. Ours,
    /// not the carrier's; kept apart from <see cref="Message"/> so neither is mistaken for the other.
    /// </summary>
    public string? FailureDetail { get; set; }

    // ── Human resolution ──────────────────────────────────────────────────────

    /// <summary>
    /// Set when a person settled an <c>UNKNOWN</c> by checking with the carrier. A status reached
    /// by resolution looks like any other, so this is how it is told apart.
    /// </summary>
    public int?      ResolvedBy     { get; set; }
    public DateTime? ResolvedAt     { get; set; }
    public string?   ResolutionNote { get; set; }

    /// <summary>Optimistic concurrency — two workers cannot both claim one retry.</summary>
    public byte[] RowVersion { get; set; } = [];

    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}
