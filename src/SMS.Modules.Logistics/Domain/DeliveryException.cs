using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// Something that has gone wrong with a movement, named and owned until it is settled.
/// <para>
/// <b>Why it exists.</b> <see cref="DeliveryExceptionType"/> has held eight named causes since T-04
/// and nothing has ever used one (finding F46). A consignment sitting in <c>EXCEPTION</c> could say
/// only <em>that</em> something was wrong — never what, never who was dealing with it, never
/// whether it had been.
/// </para>
/// <para>
/// <b>It is not the tracking timeline.</b> A carrier scan saying "customs hold" is news; this is
/// the piece of work that news creates. They are related but not the same, and collapsing them
/// would mean either losing the carrier's exact words or inventing a resolution it never gave.
/// </para>
/// </summary>
internal class DeliveryException : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int         ConsignmentId { get; set; }
    public Consignment Consignment   { get; set; } = null!;

    public int?     CarrierId   { get; set; }
    public Carrier? Carrier     { get; set; }
    public string?  CarrierName { get; set; }

    /// <summary>See <see cref="DeliveryExceptionType"/>. Stored as its code.</summary>
    public string ExceptionType { get; set; } = string.Empty;

    /// <summary>See <see cref="ExceptionSeverity"/>. Stored as its code.</summary>
    public string Severity { get; set; } = LogisticsCode.Of(ExceptionSeverity.Normal);

    /// <summary>See <see cref="ExceptionStatus"/>. Stored as its code.</summary>
    public string Status { get; set; } = LogisticsCode.Of(ExceptionStatus.Open);

    /// <summary>What happened, in whatever words the person or the carrier used.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// How it came to be known — a carrier event, or somebody raising it. Kept because an exception
    /// the carrier told us about and one we noticed ourselves carry different weight in a dispute.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// The tracking event that caused it, where one did. Null when a person raised it, which is
    /// how most COD mismatches and damage claims start.
    /// </summary>
    public int?                      TrackingEventId { get; set; }
    public ConsignmentTrackingEvent? TrackingEvent   { get; set; }

    public DateTime OccurredAt { get; set; }

    // ── Who has it ────────────────────────────────────────────────────────────

    /// <summary>
    /// Null means nobody. An exception with no owner is the one that sits untouched for a week,
    /// so the queue reports them separately rather than mixing them in.
    /// </summary>
    public int? AssignedToUserId { get; set; }

    public DateTime? AssignedAt { get; set; }

    // ── How it ended ──────────────────────────────────────────────────────────

    public DateTime? ResolvedAt { get; set; }
    public int?      ResolvedBy { get; set; }

    /// <summary>Required to close one. An exception that closes without a reason teaches nobody anything.</summary>
    public string? Resolution { get; set; }

    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}
