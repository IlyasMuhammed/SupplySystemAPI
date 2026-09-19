namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// What a delivery's source document implies. One row per <see cref="DeliverySourceType"/>.
/// </summary>
/// <param name="PostsGoodsIssue">
/// Whether the delivery is the document that writes the negative stock movement.
/// <para>
/// <b>This flag is the guard against deducting stock twice.</b> MIV already deducts when material
/// is issued to a project, and the SRO dispatch path deducts on its own (see
/// <c>SroRepository</c>, which writes a <c>RETURN_DISPATCH</c> inventory transaction at dispatch).
/// A delivery created from either of those documents is a <em>movement reference</em> — it tracks
/// and proves the physical movement but must not post it again. TRANSFER and MANUAL deliveries
/// have no other document behind them, so they do post.
/// </para>
/// <para>
/// It hangs off the source <em>type</em> rather than the delivery row on purpose: a per-row flag
/// is something a user or a bad import can get wrong, and the cost of getting it wrong is a
/// silently incorrect ledger. As a type-level rule it is not data, so it cannot drift.
/// </para>
/// </param>
/// <param name="DefaultDirection">
/// The direction implied by the source, or <c>null</c> when the source does not imply one.
/// </param>
internal readonly record struct DeliverySourceRules(
    bool PostsGoodsIssue,
    DeliveryDirection? DefaultDirection);

internal static class DeliverySourceTypeInfo
{
    private static readonly IReadOnlyDictionary<DeliverySourceType, DeliverySourceRules> Rules =
        new Dictionary<DeliverySourceType, DeliverySourceRules>
        {
            // Inbound from a supplier — an ASN. Stock is posted by the GRN on receipt, not here.
            [DeliverySourceType.Po] = new(PostsGoodsIssue: false, DeliveryDirection.Inbound),

            // Returning goods to a supplier. SRO dispatch already deducts.
            [DeliverySourceType.Sro] = new(PostsGoodsIssue: false, DeliveryDirection.Outbound),

            // Issuing material to a project site. MIV already deducts on POSTED.
            [DeliverySourceType.Miv] = new(PostsGoodsIssue: false, DeliveryDirection.Outbound),

            // Warehouse to warehouse. Nothing else posts this movement.
            [DeliverySourceType.Transfer] = new(PostsGoodsIssue: true, DeliveryDirection.Transfer),

            // A standalone delivery with no source document. Nothing else posts it, and the
            // direction cannot be inferred — an ad-hoc delivery may go either way, so the
            // caller must state it.
            [DeliverySourceType.Manual] = new(PostsGoodsIssue: true, DefaultDirection: null),

            // Goods to a customer against a confirmed sale order. Confirming the order only
            // reserves stock — nothing has left the books — so the delivery's goods issue is the
            // movement (SALES_SHIP, or SALES_HANDOVER for self-pickup).
            [DeliverySourceType.SaleOrder] = new(PostsGoodsIssue: true, DeliveryDirection.Outbound)
        };

    /// <summary>The rules for a source type. Throws if a source type has no entry.</summary>
    internal static DeliverySourceRules For(DeliverySourceType sourceType) =>
        Rules.TryGetValue(sourceType, out var rules)
            ? rules
            : throw new InvalidOperationException(
                $"No delivery source rules declared for '{sourceType}'. Every DeliverySourceType " +
                "must declare whether it posts goods issue — defaulting that would risk a double " +
                "stock deduction.");

    /// <summary>
    /// Whether a delivery from this source posts the stock movement itself. See
    /// <see cref="DeliverySourceRules.PostsGoodsIssue"/> for why this is a type-level rule.
    /// </summary>
    internal static bool PostsGoodsIssue(DeliverySourceType sourceType) =>
        For(sourceType).PostsGoodsIssue;

    /// <summary>
    /// The direction implied by the source. Returns false for sources that do not imply one, in
    /// which case the caller must supply a direction explicitly.
    /// </summary>
    internal static bool TryGetDefaultDirection(DeliverySourceType sourceType, out DeliveryDirection direction)
    {
        var declared = For(sourceType).DefaultDirection;
        direction = declared ?? default;
        return declared.HasValue;
    }
}
