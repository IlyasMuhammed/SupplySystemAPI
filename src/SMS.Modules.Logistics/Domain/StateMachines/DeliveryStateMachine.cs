namespace SMS.Modules.Logistics.Domain.StateMachines;

/// <summary>
/// The delivery lifecycle: owned by the warehouse, ending at goods issue and then at closure.
/// Kept separate from the shipment lifecycle so a delivery can be re-shipped after a
/// return-to-origin without resurrecting a closed document.
/// </summary>
internal sealed class DeliveryStateMachine : StateMachine<DeliveryStatus>
{
    internal static readonly DeliveryStateMachine Instance = new();

    protected override string DocumentName => "delivery";

    /// <summary>
    /// The states a delivery can be held from, and therefore the states it can resume to.
    /// <para>
    /// DRAFT is deliberately absent: an unreleased delivery has reserved nothing and committed
    /// to nothing, so there is nothing to hold. Everything at or past GOODS_ISSUED is absent
    /// because the stock has left — holding it would suggest a reversal the module cannot do.
    /// </para>
    /// <para>
    /// Derived from the table rather than stored as a second list, so the two cannot disagree.
    /// It is a property, not a static field: a static field read during <c>BuildTable</c> would
    /// still be null, because <c>Instance</c>'s initializer runs before any field declared
    /// below it.
    /// </para>
    /// </summary>
    internal IReadOnlyCollection<DeliveryStatus> Holdable =>
        [.. From(DeliveryStatus.OnHold).Where(s => s != DeliveryStatus.Cancelled)];

    private DeliveryStateMachine() : base(BuildTable()) { }

    private static Dictionary<DeliveryStatus, DeliveryStatus[]> BuildTable()
    {
        // A local, not a static field — see the note on Holdable above.
        DeliveryStatus[] holdable =
        [
            DeliveryStatus.Released,
            DeliveryStatus.Picking,
            DeliveryStatus.Picked,
            DeliveryStatus.Packed,
            DeliveryStatus.Staged,
            DeliveryStatus.PendingApproval
        ];

        return new Dictionary<DeliveryStatus, DeliveryStatus[]>
        {
            [DeliveryStatus.Draft] =
            [
                DeliveryStatus.Released,
                DeliveryStatus.Cancelled
            ],

            // Stock is hard-reserved here — the first transition with a consequence.
            [DeliveryStatus.Released] =
            [
                DeliveryStatus.Picking,
                DeliveryStatus.OnHold,
                DeliveryStatus.Cancelled
            ],

            [DeliveryStatus.Picking] =
            [
                DeliveryStatus.Picked,
                DeliveryStatus.OnHold,
                DeliveryStatus.Cancelled
            ],

            // Short-close becomes possible once something has actually been picked; short-closing
            // a delivery that picked nothing is a cancellation wearing a different name.
            [DeliveryStatus.Picked] =
            [
                DeliveryStatus.Packed,
                DeliveryStatus.ShortClosed,
                DeliveryStatus.OnHold,
                DeliveryStatus.Cancelled
            ],

            [DeliveryStatus.Packed] =
            [
                DeliveryStatus.Staged,
                DeliveryStatus.ShortClosed,
                DeliveryStatus.OnHold,
                DeliveryStatus.Cancelled
            ],

            // PENDING_APPROVAL is conditional: it is entered only when a shipping rule demands it
            // (high declared value, restricted lane, hazardous goods). The direct path to
            // GOODS_ISSUED is the normal one.
            [DeliveryStatus.Staged] =
            [
                DeliveryStatus.PendingApproval,
                DeliveryStatus.GoodsIssued,
                DeliveryStatus.ShortClosed,
                DeliveryStatus.OnHold,
                DeliveryStatus.Cancelled
            ],

            // A rejected approval drops back to STAGED with the reason on the timeline.
            [DeliveryStatus.PendingApproval] =
            [
                DeliveryStatus.GoodsIssued,
                DeliveryStatus.Staged,
                DeliveryStatus.OnHold,
                DeliveryStatus.Cancelled
            ],

            // ── Past this line the stock has left the books. Nothing may be cancelled. ──
            // DELIVERED directly is the self-pickup path (A29 §7.2): the customer collects at the
            // warehouse, so there is no transit to be in.
            [DeliveryStatus.GoodsIssued] =
            [
                DeliveryStatus.InTransit,
                DeliveryStatus.Delivered
            ],

            [DeliveryStatus.InTransit] =
            [
                DeliveryStatus.Delivered,
                DeliveryStatus.PartiallyDelivered
            ],

            [DeliveryStatus.PartiallyDelivered] =
            [
                DeliveryStatus.Delivered,
                DeliveryStatus.ShortClosed
            ],

            [DeliveryStatus.Delivered] =
            [
                DeliveryStatus.Closed
            ],

            [DeliveryStatus.ShortClosed] =
            [
                DeliveryStatus.Closed
            ],

            // Resumes to whichever state the hold interrupted — the caller supplies it from the
            // delivery's stored prior status. Note DRAFT is not reachable: a held delivery has
            // already been released and still holds its reservation.
            [DeliveryStatus.OnHold] = [.. holdable, DeliveryStatus.Cancelled],

            // Terminal.
            [DeliveryStatus.Closed]    = [],
            [DeliveryStatus.Cancelled] = []
        };
    }

    /// <summary>
    /// Whether a delivery in <paramref name="status"/> can be put on hold.
    /// </summary>
    internal bool CanHold(DeliveryStatus status) => CanTransition(status, DeliveryStatus.OnHold);

    /// <summary>
    /// Validates resuming a held delivery back to the status the hold interrupted. Separate from
    /// <see cref="StateMachine{TStatus}.Validate"/> only to make the intent explicit at call
    /// sites — a resume must go back where it came from, never to an arbitrary status.
    /// </summary>
    internal TransitionResult ValidateResume(DeliveryStatus priorStatus) =>
        Validate(DeliveryStatus.OnHold, priorStatus);
}
