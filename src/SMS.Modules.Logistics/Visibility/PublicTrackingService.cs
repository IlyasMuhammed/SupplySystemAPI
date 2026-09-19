using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Visibility;

/// <summary>
/// The consignee's own view of where their goods are — the one page in this module anybody can
/// open without logging in.
/// <para>
/// <b>Decision G11, resolved as an opaque per-consignment token.</b> Addressing the page by the
/// airway bill was the obvious alternative and is the wrong one: AWBs are sequential at most
/// carriers, so every other consignment's page would be one increment away, and a postcode
/// challenge is not a secret. This token is 256 bits of randomness, belongs to one consignment, and
/// is revoked by nulling a column.
/// </para>
/// <para>
/// <b>What the page shows is a whitelist, not a filter.</b> Every field on
/// <see cref="PublicTrackingModel"/> was chosen. Nothing is on it because it happened to be on the
/// consignment — which is why a new column on <c>Consignment</c> can never leak here by accident.
/// </para>
/// </summary>
public interface IPublicTrackingService
{
    /// <summary>
    /// Issues a token, replacing any existing one. Null when the consignment does not exist.
    /// </summary>
    Task<TrackingLinkModel?> IssueAsync(Guid consignmentUuid, int userId, CancellationToken ct = default);

    /// <summary>The live token, or null when none has been issued. Never creates one.</summary>
    Task<TrackingLinkModel?> GetLinkAsync(Guid consignmentUuid, CancellationToken ct = default);

    /// <summary>Stops the page working. False when the consignment does not exist.</summary>
    Task<bool> RevokeAsync(Guid consignmentUuid, int userId, CancellationToken ct = default);

    /// <summary>
    /// The public page. <b>Anonymous</b> — no user, no tenant, no permission — so it bypasses the
    /// query filter and checks the organization itself. Null for every kind of miss.
    /// </summary>
    Task<PublicTrackingModel?> TrackAsync(string token, CancellationToken ct = default);
}

internal sealed class PublicTrackingService : IPublicTrackingService
{
    /// <summary>Checked by the feature filter for logged-in callers; checked here by hand, because an anonymous request bypasses it.</summary>
    private const string ModuleFeature = "MODULE_LOGISTICS";

    /// <summary>32 bytes, hex-encoded to 64 characters. Guessing one is not a strategy.</summary>
    internal const int TokenBytes = 32;

    /// <summary>The whole timeline would be dozens of rows on a long international movement.</summary>
    internal const int MaxEvents = 40;

    /// <summary>
    /// Milestones a consignee is shown, and the words they are shown in.
    /// <para>
    /// <b>A whitelist, so a milestone added later is invisible here until somebody decides what the
    /// public should be told about it.</b> An unknown milestone is dropped rather than shown as its
    /// code — <c>RETURN_INITIATED</c> means something to us and nothing to the person waiting in.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PublicMilestones =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LogisticsCode.Of(TrackingMilestone.InfoReceived)]      = "Details received",
            [LogisticsCode.Of(TrackingMilestone.PickedUp)]          = "Collected",
            [LogisticsCode.Of(TrackingMilestone.InTransit)]         = "In transit",
            [LogisticsCode.Of(TrackingMilestone.ArrivedAtHub)]      = "Arrived at depot",
            [LogisticsCode.Of(TrackingMilestone.DepartedHub)]       = "Left depot",
            [LogisticsCode.Of(TrackingMilestone.CustomsHold)]       = "Held at customs",
            [LogisticsCode.Of(TrackingMilestone.OutForDelivery)]    = "Out for delivery",
            [LogisticsCode.Of(TrackingMilestone.DeliveryAttempted)] = "Delivery attempted",
            [LogisticsCode.Of(TrackingMilestone.Delivered)]         = "Delivered",
            [LogisticsCode.Of(TrackingMilestone.Exception)]         = "Delayed",
            [LogisticsCode.Of(TrackingMilestone.Returned)]          = "Returned to sender"
        };

    /// <summary>
    /// The consignment's own status, in words a consignee would use. Anything not listed reads as
    /// "In progress" rather than leaking an internal state such as RATED or LABEL_READY.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PublicStatuses =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LogisticsCode.Of(ShipmentStatus.PickedUp)]          = "Collected",
            [LogisticsCode.Of(ShipmentStatus.InTransit)]         = "In transit",
            [LogisticsCode.Of(ShipmentStatus.OutForDelivery)]    = "Out for delivery",
            [LogisticsCode.Of(ShipmentStatus.DeliveryAttempted)] = "Delivery attempted",
            [LogisticsCode.Of(ShipmentStatus.Delivered)]         = "Delivered",
            [LogisticsCode.Of(ShipmentStatus.Exception)]         = "Delayed",
            [LogisticsCode.Of(ShipmentStatus.ReturnedToOrigin)]  = "Returned to sender",
            [LogisticsCode.Of(ShipmentStatus.Cancelled)]         = "Cancelled"
        };

    private readonly LogisticsDbContext     _db;
    private readonly ITenantSnapshotProvider _tenants;

    public PublicTrackingService(LogisticsDbContext db, ITenantSnapshotProvider tenants)
    {
        _db      = db;
        _tenants = tenants;
    }

    // ── Issuing, from inside ──────────────────────────────────────────────────

    public async Task<TrackingLinkModel?> IssueAsync(
        Guid consignmentUuid, int userId, CancellationToken ct = default)
    {
        var consignment = await _db.Consignments
            .FirstOrDefaultAsync(c => c.UUID == consignmentUuid && !c.IsDelete, ct);

        if (consignment is null) return null;

        var replaced = consignment.TrackingToken is not null;
        var now = DateTime.UtcNow;

        consignment.TrackingToken         = NewToken();
        consignment.TrackingTokenIssuedAt = now;
        consignment.ModifiedBy            = userId;
        consignment.ModifiedDate          = now;

        await _db.SaveChangesAsync(ct);

        return new TrackingLinkModel
        {
            ConsignmentUuid   = consignment.UUID,
            ConsignmentNumber = consignment.ConsignmentNumber,
            Token             = consignment.TrackingToken!,
            Path              = PathFor(consignment.TrackingToken!),
            IssuedAt          = now,
            ReplacedPrevious  = replaced
        };
    }

    public async Task<TrackingLinkModel?> GetLinkAsync(
        Guid consignmentUuid, CancellationToken ct = default)
    {
        var consignment = await _db.Consignments.AsNoTracking()
            .FirstOrDefaultAsync(c => c.UUID == consignmentUuid && !c.IsDelete, ct);

        if (consignment?.TrackingToken is null) return null;

        return new TrackingLinkModel
        {
            ConsignmentUuid   = consignment.UUID,
            ConsignmentNumber = consignment.ConsignmentNumber,
            Token             = consignment.TrackingToken,
            Path              = PathFor(consignment.TrackingToken),
            IssuedAt          = consignment.TrackingTokenIssuedAt ?? default
        };
    }

    public async Task<bool> RevokeAsync(
        Guid consignmentUuid, int userId, CancellationToken ct = default)
    {
        var consignment = await _db.Consignments
            .FirstOrDefaultAsync(c => c.UUID == consignmentUuid && !c.IsDelete, ct);

        if (consignment is null) return false;

        consignment.TrackingToken         = null;
        consignment.TrackingTokenIssuedAt = null;
        consignment.ModifiedBy            = userId;
        consignment.ModifiedDate          = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    internal static string PathFor(string token) => $"/track/{token}";

    private static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes)).ToLowerInvariant();

    // ── The page itself, from outside ─────────────────────────────────────────

    public async Task<PublicTrackingModel?> TrackAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        // Trimmed and lowered first, then length-checked before touching the database: links get
        // retyped and pasted with whitespace, and a 4 KB "token" is somebody probing rather than a
        // consignee, so it should cost nothing.
        var normalised = token.Trim().ToLowerInvariant();

        if (normalised.Length != TokenBytes * 2) return null;

        // IgnoreQueryFilters because there is no tenant on an anonymous request — the token is the
        // only thing identifying anything. The organization is then checked explicitly, below.
        var consignment = await _db.Consignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(c => c.Carrier)
            .FirstOrDefaultAsync(c => c.TrackingToken == normalised && !c.IsDelete, ct);

        if (consignment is null) return null;

        // The feature filter cannot help here — an anonymous request bypasses it — so a deactivated
        // organization, or one with Logistics switched off, is checked by hand. Same nothing as a
        // wrong token: a different answer would confirm the token was right.
        var tenant = await _tenants.GetSnapshotAsync(consignment.OrganizationId);

        if (tenant is not { IsActive: true } || !tenant.EnabledFeatureCodes.Contains(ModuleFeature))
            return null;

        var events = await _db.ConsignmentTrackingEvents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.ConsignmentId == consignment.Id)
            .OrderByDescending(e => e.OccurredAt)
            .Take(MaxEvents)
            .ToListAsync(ct);

        return Build(consignment, events);
    }

    private static PublicTrackingModel Build(
        Consignment consignment, List<ConsignmentTrackingEvent> events)
    {
        var shown = events
            .Where(e => PublicMilestones.ContainsKey(e.Milestone))
            .OrderBy(e => e.OccurredAt)
            .Select(e => new PublicTrackingEventModel
            {
                Status     = PublicMilestones[e.Milestone],
                OccurredAt = e.OccurredAt,
                Location   = Trim(e.Location)
            })
            .ToList();

        return new PublicTrackingModel
        {
            // Our own number, which the consignee was given with the despatch notice. The airway
            // bill is deliberately absent: a forwarded link should not also hand over the ability
            // to query the carrier directly about somebody else's account.
            Reference        = consignment.ConsignmentNumber,
            Status           = PublicStatuses.TryGetValue(consignment.Status, out var said)
                                   ? said
                                   : "In progress",
            Carrier          = consignment.CarrierName ?? consignment.Carrier?.Name,
            EstimatedArrival = consignment.Eta,
            DeliveredAt      = consignment.Status == LogisticsCode.Of(ShipmentStatus.Delivered)
                                   ? consignment.ActualArrivalAt
                                   : null,
            Events           = shown,
            LastUpdatedAt    = shown.Count > 0 ? shown[^1].OccurredAt : null
        };
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
