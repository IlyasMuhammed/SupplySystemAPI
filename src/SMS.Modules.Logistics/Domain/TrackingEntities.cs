using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// One thing the carrier said happened to a consignment — picked up, arrived at a hub, delivered.
/// <para>
/// Append-only. The same event arriving twice — a webhook the carrier resends, then the poll
/// finding it again — is stored once, keyed on what it says rather than on how it arrived.
/// </para>
/// </summary>
internal class ConsignmentTrackingEvent : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int         ConsignmentId { get; set; }
    public Consignment Consignment   { get; set; } = null!;

    /// <summary>See <see cref="TrackingMilestone"/>. Stored as its code.</summary>
    public string Milestone { get; set; } = string.Empty;

    /// <summary>The carrier's own status code, kept for anything our milestones flatten away.</summary>
    public string? CarrierStatus { get; set; }
    public string? Description   { get; set; }
    public string? Location      { get; set; }
    public string? SignedBy      { get; set; }

    /// <summary>When it happened at the carrier, in UTC.</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>When it reached us.</summary>
    public DateTime ReceivedAt { get; set; }

    /// <summary>See <see cref="TrackingEventSource"/>. Stored as its code.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>SHA-256 of milestone, time, carrier status and location — what makes two arrivals one event.</summary>
    public string EventKey { get; set; } = string.Empty;

    /// <summary>The consignment status this event moved it to, if it moved it. Null for most events.</summary>
    public string? AppliedStatus { get; set; }

    public DateTime CreatedDate { get; set; }
}

/// <summary>
/// One webhook delivery from a carrier, kept once its signature has verified.
/// <para>
/// <b>The inbox exists for three reasons:</b> carriers resend deliveries they think failed, so the
/// same one must not be applied twice; a delivery that failed to process must be retryable when the
/// carrier resends it; and when a carrier disputes what it told us, the body it signed is the record.
/// </para>
/// <para>
/// Only verified deliveries are stored. An endpoint that stored whatever anyone posted to it would
/// be free storage for anyone on the internet.
/// </para>
/// </summary>
internal class CarrierWebhookDelivery : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int            CarrierAccountId { get; set; }
    public CarrierAccount CarrierAccount   { get; set; } = null!;

    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>The carrier's own id for the delivery when it sends one; otherwise the body's hash.</summary>
    public string DedupeKey { get; set; } = string.Empty;

    public string BodySha256 { get; set; } = string.Empty;

    /// <summary>The body exactly as signed. Headers are not kept — they can carry credentials.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>See <see cref="WebhookDeliveryStatus"/>. Stored as its code.</summary>
    public string Status { get; set; } = string.Empty;

    public int AttemptCount   { get; set; }
    public int EventCount     { get; set; }
    public int RecordedCount  { get; set; }
    public int DuplicateCount { get; set; }

    /// <summary>Events for airway bills that match no consignment here — booked outside the system, most likely.</summary>
    public int UnmatchedCount { get; set; }

    /// <summary>Why it was rejected or failed.</summary>
    public string? Detail { get; set; }

    public DateTime  ReceivedAt   { get; set; }
    public DateTime? ProcessedAt  { get; set; }

    /// <summary>Optimistic concurrency — a resend racing its own retry cannot both process.</summary>
    public byte[] RowVersion { get; set; } = [];
}
