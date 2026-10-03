using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Services;

/// <summary>A33 PE-01/02 (D-7) — "Approve dispatch" and the route-aware "advance one step".</summary>
public interface IDeliveryRouteService
{
    /// <summary>D-7. Null when the delivery is not the caller organization's.</summary>
    Task<DeliveryApprovalModel?> ApproveAsync(Guid uuid, int userId);

    /// <summary>
    /// PE-02. Performs the one next step that needs no input, after checking the caller holds that operation's own
    /// permission (<paramref name="hasPermission"/>; 403 otherwise). Null when the delivery is not the caller's.
    /// </summary>
    Task<AdvanceDeliveryResultModel?> AdvanceAsync(
        Guid uuid, AdvanceDeliveryRequest? req, Func<string, bool> hasPermission, int userId);
}

/// <summary>
/// A33 PE-01/02 — the two route-aware actions that are not an existing operation:
/// <list type="bullet">
/// <item><b>Approve dispatch</b> (D-7): a STAGED delivery whose route has APPROVAL gets <c>ApprovedAt/By</c>; goods
/// issue is refused until then (the guard sits in <c>GoodsIssueRepository.IssueAsync</c>). The workflow engine's own
/// PENDING_APPROVAL path is untouched.</item>
/// <item><b>Advance</b>: no new transition — it runs the existing operation for the next step that needs no input
/// (release, stage, approve, goods issue) and names the one that does with a 409 (confirm the pick, pack, create a
/// consignment, record the collection).</item>
/// </list>
/// Both look the delivery up in the caller's own organization explicitly (R-14): another organization's is absent,
/// super admin included.
/// </summary>
internal sealed class DeliveryRouteService : IDeliveryRouteService
{
    private readonly LogisticsDbContext         _db;
    private readonly IDeliveryReleaseRepository _release;
    private readonly IGoodsIssueRepository      _goodsIssue;

    public DeliveryRouteService(LogisticsDbContext db, IDeliveryReleaseRepository release, IGoodsIssueRepository goodsIssue)
    {
        _db         = db;
        _release    = release;
        _goodsIssue = goodsIssue;
    }

    private Task<DeliveryOrder?> OwnAsync(Guid uuid, bool tracked)
    {
        var ownOrg = _db.TenantContext.OrganizationId;
        var query  = _db.DeliveryOrders.Where(d => d.UUID == uuid && !d.IsDelete && d.OrganizationId == ownOrg);
        return (tracked ? query : query.AsNoTracking()).FirstOrDefaultAsync();
    }

    // ── Approve dispatch (D-7) ────────────────────────────────────────────────

    public async Task<DeliveryApprovalModel?> ApproveAsync(Guid uuid, int userId)
    {
        var delivery = await OwnAsync(uuid, tracked: true);
        if (delivery is null) return null;

        if (!DeliveryRouteFlow.RequiresApproval(delivery))
            throw new BadRequestException(
                $"Delivery {delivery.DeliveryNumber} " +
                (DeliveryRouteFlow.HasRoute(delivery)
                    ? $"follows {DeliveryRouteFlow.Describe(delivery)}, which has no APPROVAL step"
                    : "has no fulfillment route with an APPROVAL step") +
                ", so there is no dispatch to approve.");

        if (delivery.ApprovedAt is { } approvedAt)
            return new DeliveryApprovalModel
            {
                DeliveryUuid = delivery.UUID, ApprovedAt = approvedAt, ApprovedBy = delivery.ApprovedBy, AlreadyApproved = true
            };

        if (LogisticsCode.Parse<DeliveryStatus>(delivery.Status) != DeliveryStatus.Staged)
            throw new BadRequestException(
                $"Delivery {delivery.DeliveryNumber} is {delivery.Status}. Dispatch is approved once the goods are " +
                "STAGED at the dock.");

        var now = DateTime.UtcNow;
        delivery.ApprovedAt   = now;
        delivery.ApprovedBy   = userId;
        delivery.ModifiedBy   = userId;
        delivery.ModifiedDate = now;
        await _db.SaveChangesAsync();

        return new DeliveryApprovalModel { DeliveryUuid = delivery.UUID, ApprovedAt = now, ApprovedBy = userId };
    }

    // ── Advance (PE-02) ───────────────────────────────────────────────────────

    private const string Release    = DeliveryNextAction.Release;
    private const string Stage      = DeliveryNextAction.Stage;
    private const string Approve    = DeliveryNextAction.Approve;
    private const string GoodsIssue = DeliveryNextAction.GoodsIssue;

    public async Task<AdvanceDeliveryResultModel?> AdvanceAsync(
        Guid uuid, AdvanceDeliveryRequest? req, Func<string, bool> hasPermission, int userId)
    {
        ArgumentNullException.ThrowIfNull(hasPermission);

        var delivery = await OwnAsync(uuid, tracked: false);
        if (delivery is null) return null;

        if (!string.IsNullOrWhiteSpace(req?.ExpectedStatus)
            && !string.Equals(req.ExpectedStatus.Trim(), delivery.Status, StringComparison.OrdinalIgnoreCase))
            throw new ConflictException(
                $"Delivery {delivery.DeliveryNumber} is {delivery.Status} now, not {req.ExpectedStatus.Trim()} — someone " +
                "moved it on. Reload it and try again.");

        var previous = delivery.Status;
        var (action, permission) = NextFor(delivery);

        if (!hasPermission(permission))
            throw new ForbiddenException(
                $"The next step for delivery {delivery.DeliveryNumber} is {action}, which needs the {permission} permission.");

        switch (action)
        {
            case Release:    await _release.ReleaseAsync(uuid, null, userId); break;
            case Stage:      await _goodsIssue.StageAsync(uuid, userId);      break;
            case Approve:    await ApproveAsync(uuid, userId);                break;
            case GoodsIssue: await _goodsIssue.IssueAsync(uuid, userId);      break;
        }

        var status = await _db.DeliveryOrders.AsNoTracking()
            .Where(d => d.UUID == uuid).Select(d => d.Status).FirstAsync();

        return new AdvanceDeliveryResultModel { PreviousStatus = previous, Status = status, Action = action };
    }

    /// <summary>The step "advance" may take, and the permission it needs — or a 409 naming the step that needs input.</summary>
    private static (string Action, string Permission) NextFor(DeliveryOrder d)
    {
        var status   = LogisticsCode.Parse<DeliveryStatus>(d.Status);
        var awaiting = DeliveryRouteFlow.AwaitsApproval(d);
        var number   = d.DeliveryNumber;

        return status switch
        {
            DeliveryStatus.Draft  => (Release, PermissionCodes.DELIVERY_EDIT),
            DeliveryStatus.Packed => (Stage, PermissionCodes.DISPATCH),
            DeliveryStatus.Staged when awaiting => (Approve, PermissionCodes.DELIVERY_APPROVE),
            DeliveryStatus.Staged or DeliveryStatus.PendingApproval when !awaiting => (GoodsIssue, PermissionCodes.DISPATCH),

            DeliveryStatus.PendingApproval => throw Needs(number,
                "is waiting for its approval workflow. Once it is back at STAGED, approve the dispatch."),
            DeliveryStatus.Released => throw Needs(number,
                "is released. Generate its pick list — the warehouse works from it."),
            DeliveryStatus.Picking => throw Needs(number,
                "is being picked. Confirm the pick with the quantities actually picked."),
            DeliveryStatus.Picked => throw Needs(number,
                "is picked. Pack it at the pack station — the cartons and their contents are the packer's to state."),
            DeliveryStatus.GoodsIssued when DeliveryRouteFlow.IsCollected(d) => throw Needs(number,
                "is issued and waiting for the customer. Record the collection: who collected, and what ID they showed."),
            DeliveryStatus.GoodsIssued => throw Needs(number,
                "is issued. Create a consignment for it — the carrier and its booking are yours to choose."),
            DeliveryStatus.InTransit or DeliveryStatus.PartiallyDelivered => throw Needs(number,
                "is with the carrier; its consignment's progress moves it on."),
            DeliveryStatus.OnHold => throw Needs(number,
                "is on hold. Resume it first."),
            _ => throw Needs(number, $"is {d.Status}, so there is nothing left to advance.")
        };
    }

    private static ConflictException Needs(string number, string what) => new($"Delivery {number} {what}");
}
