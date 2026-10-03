using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// A33 PE-01 (D-2, D-3, D-7; contract §7/§8) — what a delivery's route snapshot means for it. Pure functions over the
/// delivery row, so the guards in the operations, the detail model and "advance" all read one rule.
/// <para>
/// <b>No route = today's behaviour (R-1).</b> Every guard here is a no-op for a delivery whose
/// <see cref="DeliveryOrder.RouteSteps"/> is empty (PO, SRO, MIV, transfer, manual and pre-A33 sale-order deliveries):
/// <see cref="Lacks"/> is false and <see cref="RequiresApproval"/> is false, so nothing is auto-completed or refused.
/// </para>
/// </summary>
internal static class DeliveryRouteFlow
{
    public const string Complete = "COMPLETE";

    public static IReadOnlyList<string> Steps(DeliveryOrder d) => FulfillmentStepCode.Parse(d.RouteSteps);

    public static bool HasRoute(DeliveryOrder d) => Steps(d).Count > 0;

    public static bool Has(DeliveryOrder d, string step) => Steps(d).Contains(step, StringComparer.Ordinal);

    /// <summary>The delivery has a route and the route leaves <paramref name="step"/> out. False with no route.</summary>
    public static bool Lacks(DeliveryOrder d, string step) => HasRoute(d) && !Has(d, step);

    /// <summary>D-7: the route has APPROVAL, so goods issue waits for "Approve dispatch".</summary>
    public static bool RequiresApproval(DeliveryOrder d) => Has(d, FulfillmentStepCode.Approval);

    public static bool AwaitsApproval(DeliveryOrder d) => RequiresApproval(d) && d.ApprovedAt is null;

    /// <summary>The customer collects: the route has no SHIP; with no route, the delivery's own mode says so.</summary>
    public static bool IsCollected(DeliveryOrder d) =>
        HasRoute(d)
            ? !Has(d, FulfillmentStepCode.Ship)
            : string.Equals(d.DeliveryMode, LogisticsCode.Of(DeliveryMode.SelfPickup), StringComparison.Ordinal);

    /// <summary>"Route X (Pick → Goods Issue)" — how refusals name the route.</summary>
    public static string Describe(DeliveryOrder d) =>
        $"route {d.FulfillmentRouteCode ?? "(unnamed)"} ({string.Join(" → ", Steps(d).Select(FulfillmentRouteText.StepLabel))})";

    // ── Tracker (contract §8) ─────────────────────────────────────────────────

    private static readonly DeliveryStatus[] Order =
    [
        DeliveryStatus.Draft, DeliveryStatus.Released, DeliveryStatus.Picking, DeliveryStatus.Picked,
        DeliveryStatus.Packed, DeliveryStatus.Staged, DeliveryStatus.PendingApproval, DeliveryStatus.GoodsIssued,
        DeliveryStatus.InTransit, DeliveryStatus.PartiallyDelivered, DeliveryStatus.Delivered, DeliveryStatus.Closed
    ];

    private static int Rank(DeliveryStatus s) => Array.IndexOf(Order, s);

    /// <summary>
    /// Where the delivery stands on the path. ON_HOLD reads its <c>StatusBeforeHold</c>; CANCELLED and SHORT_CLOSED
    /// "stay where they were", which the row only remembers through its quantities and stamps.
    /// </summary>
    private static int Position(DeliveryOrder d)
    {
        var status = LogisticsCode.Parse<DeliveryStatus>(d.Status);

        if (status == DeliveryStatus.OnHold && LogisticsCode.TryParse<DeliveryStatus>(d.StatusBeforeHold, out var before))
            status = before;

        if (status is DeliveryStatus.Cancelled or DeliveryStatus.ShortClosed)
        {
            if (d.Lines.Any(l => l.QtyDelivered > 0)) return Rank(DeliveryStatus.PartiallyDelivered);
            if (d.GoodsIssuedAt is not null)          return Rank(DeliveryStatus.GoodsIssued);
            if (d.Lines.Any(l => l.QtyPacked > 0))    return Rank(DeliveryStatus.Packed);
            if (d.Lines.Any(l => l.QtyPicked > 0))    return Rank(DeliveryStatus.Picked);
            return Rank(DeliveryStatus.Draft);
        }

        return Math.Max(0, Rank(status));
    }

    private static bool IsDone(DeliveryOrder d, string step, int at) => step switch
    {
        FulfillmentStepCode.Pick       => at >= Rank(DeliveryStatus.Picked),
        FulfillmentStepCode.Pack       => at >= Rank(DeliveryStatus.Packed),
        FulfillmentStepCode.Stage      => at >= Rank(DeliveryStatus.Staged),
        FulfillmentStepCode.Approval   => d.ApprovedAt is not null || at >= Rank(DeliveryStatus.GoodsIssued),
        FulfillmentStepCode.GoodsIssue => at >= Rank(DeliveryStatus.GoodsIssued),
        FulfillmentStepCode.Ship       => at >= Rank(DeliveryStatus.Delivered),
        Complete                       => at >= Rank(DeliveryStatus.Delivered),
        _                              => false
    };

    /// <summary>Only the route's steps (BR-C5-04), then COMPLETE. Empty with no route: the page shows today's status tag.</summary>
    public static List<RouteStepProgressModel> Progress(DeliveryOrder d)
    {
        var steps = Steps(d);
        if (steps.Count == 0) return [];

        var at       = Position(d);
        var progress = new List<RouteStepProgressModel>();
        var current  = false;

        foreach (var step in steps.Append(Complete))
        {
            var done  = IsDone(d, step, at);
            var state = done ? "DONE" : current ? "PENDING" : "CURRENT";
            if (!done) current = true;

            progress.Add(new RouteStepProgressModel
            {
                StepCode = step,
                Label    = step == Complete ? "Complete" : FulfillmentRouteText.StepLabel(step),
                State    = state
            });
        }

        return progress;
    }

    public static string? NextStep(List<RouteStepProgressModel> progress) =>
        progress.FirstOrDefault(s => s.State == "CURRENT")?.StepCode;

    // ── What can be done now (contract §6 nextActions) ────────────────────────

    /// <summary>
    /// The <b>first</b> forward action is the route's next step (the page's primary button; QA PF-01/02 assert it).
    /// The only secondary forward action is RECORD_COLLECTION on a collected delivery that is packed, staged or in
    /// workflow approval and not waiting for D-7 approval — today's counter collection issues and delivers in one go
    /// (REV: hiding it would be a regression). Then the off-path moves the state machine allows (HOLD, RESUME,
    /// SHORT_CLOSE, CANCEL).
    /// </summary>
    public static List<string> NextActions(DeliveryOrder d, bool hasLiveConsignment)
    {
        var status    = LogisticsCode.Parse<DeliveryStatus>(d.Status);
        var collected = IsCollected(d);
        var awaiting  = AwaitsApproval(d);
        var actions   = new List<string>();

        switch (status)
        {
            case DeliveryStatus.Draft:
                actions.Add(DeliveryNextAction.Release);
                break;
            case DeliveryStatus.Released:
                actions.Add(DeliveryNextAction.GeneratePickList);
                break;
            case DeliveryStatus.Picking:
                actions.Add(DeliveryNextAction.ConfirmPick);
                break;
            case DeliveryStatus.Picked:
                if (d.Lines.Any(l => l.QtyPicked > l.QtyPacked)) actions.Add(DeliveryNextAction.Pack);
                break;
            case DeliveryStatus.Packed:
                actions.Add(DeliveryNextAction.Stage);
                if (collected && !awaiting) actions.Add(DeliveryNextAction.RecordCollection);
                break;
            case DeliveryStatus.Staged:
                actions.Add(awaiting ? DeliveryNextAction.Approve : DeliveryNextAction.GoodsIssue);
                if (collected && !awaiting) actions.Add(DeliveryNextAction.RecordCollection);
                break;
            case DeliveryStatus.PendingApproval:
                // The workflow engine's approval is under way; D-7's, if the route has one, still has to be given.
                if (!awaiting)
                {
                    actions.Add(DeliveryNextAction.GoodsIssue);
                    if (collected) actions.Add(DeliveryNextAction.RecordCollection);
                }
                break;
            case DeliveryStatus.GoodsIssued:
                if (collected) actions.Add(DeliveryNextAction.RecordCollection);
                else if (!hasLiveConsignment) actions.Add(DeliveryNextAction.CreateConsignment);
                break;
            case DeliveryStatus.OnHold:
                actions.Add(DeliveryNextAction.Resume);
                break;
        }

        // The off-path moves, straight from the state machine so they can never disagree with it.
        var machine = Domain.StateMachines.DeliveryStateMachine.Instance;
        if (machine.CanHold(status))                                            actions.Add(DeliveryNextAction.Hold);
        if (machine.CanTransition(status, DeliveryStatus.ShortClosed))         actions.Add(DeliveryNextAction.ShortClose);
        if (machine.CanTransition(status, DeliveryStatus.Cancelled))           actions.Add(DeliveryNextAction.Cancel);

        return actions;
    }
}
