using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// Connects deliveries to the generic workflow engine, so approvals, the inbox, delegation,
/// recall, escalation and audit all work without a line of approval code in this module.
/// <para>
/// The engine speaks its own vocabulary — PENDING, APPROVED, REJECTED, CANCELLED — and each
/// business module maps that onto its own statuses. This is the same arrangement
/// <c>GrnStatusHandler</c> uses.
/// </para>
/// </summary>
internal sealed class DeliveryStatusHandler : IDocumentStatusHandler
{
    /// <summary>
    /// How a delivery identifies itself to anything outside this module — the workflow engine's
    /// interface code and, for the same reason, the inventory ledger's reference type. One
    /// constant so the two cannot drift into calling the same document different things.
    /// </summary>
    internal const string Code = "DELIVERY";

    public string InterfaceCode => Code;

    private readonly LogisticsDbContext _db;

    public DeliveryStatusHandler(LogisticsDbContext db) => _db = db;

    public async Task<string?> GetStatusAsync(Guid documentId)
    {
        var delivery = await _db.DeliveryOrders
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.UUID == documentId && !d.IsDelete);

        return delivery?.Status;
    }

    public async Task UpdateStatusAsync(Guid documentId, string newStatus)
    {
        var delivery = await _db.DeliveryOrders
            .FirstOrDefaultAsync(d => d.UUID == documentId && !d.IsDelete);

        if (delivery is null) return;

        var current = LogisticsCode.Parse<DeliveryStatus>(delivery.Status);

        switch (newStatus)
        {
            // The approval workflow has started against a staged delivery.
            case "PENDING":
                Move(delivery, current, DeliveryStatus.PendingApproval);
                break;

            // Approved. The delivery returns to STAGED carrying an approval stamp rather than
            // jumping to GOODS_ISSUED: issuing goods writes a stock movement, and that belongs
            // to the goods-issue operation (T-27), not to an approval. A status that says the
            // stock has left while the ledger says otherwise is the worst of both.
            case "APPROVED":
                Move(delivery, current, DeliveryStatus.Staged);
                delivery.ApprovedAt = DateTime.UtcNow;
                break;

            // Rejected — same destination, no stamp. The two are told apart by ApprovedAt, and
            // the workflow's own history holds the reason and the actor.
            case "REJECTED":
                Move(delivery, current, DeliveryStatus.Staged);
                delivery.ApprovedAt = null;
                break;

            case "CANCELLED":
            case "CLOSED":
                // Validated like any other transition: the state machine refuses this at or
                // after GOODS_ISSUED, and a workflow cancellation is not a reason to make an
                // exception — the stock has already left.
                Move(delivery, current, DeliveryStatus.Cancelled);
                delivery.ClosureReason ??= "Cancelled by approval workflow.";
                delivery.ClosedAt      ??= DateTime.UtcNow;
                delivery.IsActive        = false;
                break;

            default:
                // Unknown workflow status — do nothing rather than guess, matching GrnStatusHandler.
                return;
        }

        delivery.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Applies a transition through the state machine.
    /// <para>
    /// The handler deliberately has no private path around it. Writing <c>delivery.Status = …</c>
    /// directly would let the workflow engine put a delivery into a status no operational path
    /// could ever produce — and the rest of the module trusts the status.
    /// </para>
    /// <para>
    /// A transition that is already satisfied is a no-op rather than an error, because a workflow
    /// can legitimately re-signal a status it has already applied.
    /// </para>
    /// </summary>
    private static void Move(DeliveryOrder delivery, DeliveryStatus current, DeliveryStatus target)
    {
        if (current == target) return;

        DeliveryStateMachine.Instance.EnsureCanTransition(current, target);
        delivery.Status = LogisticsCode.Of(target);
    }
}
