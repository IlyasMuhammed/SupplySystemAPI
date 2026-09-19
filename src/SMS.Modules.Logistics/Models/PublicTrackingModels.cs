namespace SMS.Modules.Logistics.Models;

/// <summary>
/// One step on the public timeline. <b>Milestone, time and place — nothing else.</b>
/// <para>
/// The carrier's free-text description is deliberately absent: it is whatever the carrier chose to
/// write, and carriers write things like "left with Mrs Khan at 14 Jubilee Road". A field we cannot
/// predict the contents of cannot be shown to the public.
/// </para>
/// </summary>
public class PublicTrackingEventModel
{
    /// <summary>Said in plain words, not as our internal code.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    /// <summary>The hub or city, where the carrier gave one. Never a street address.</summary>
    public string? Location { get; set; }
}

/// <summary>
/// What a consignee sees. Every field on it was chosen; nothing is here because it happened to be
/// on the consignment.
/// </summary>
public class PublicTrackingModel
{
    /// <summary>Our own reference, which the consignee was given with the despatch notice.</summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>Plain words: "Out for delivery", not OUT_FOR_DELIVERY.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Who is carrying it. A name the consignee can telephone.</summary>
    public string? Carrier { get; set; }

    public DateTime? EstimatedArrival { get; set; }
    public DateTime? DeliveredAt      { get; set; }

    /// <summary>Oldest first, as a timeline reads.</summary>
    public List<PublicTrackingEventModel> Events { get; set; } = [];

    /// <summary>When this page last had anything new on it.</summary>
    public DateTime? LastUpdatedAt { get; set; }
}

public class TrackingLinkModel
{
    public Guid   ConsignmentUuid   { get; set; }
    public string ConsignmentNumber { get; set; } = string.Empty;

    /// <summary>The token itself. Give it to the consignee; it is the whole of the address.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Relative, because this module does not know what host it is served on.</summary>
    public string Path { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; }

    /// <summary>Set when this call replaced an existing token, which stopped working the moment it did.</summary>
    public bool ReplacedPrevious { get; set; }
}
