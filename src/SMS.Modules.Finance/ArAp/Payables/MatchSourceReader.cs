using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.ArAp.Payables;

/// <summary>What a match compares an invoice against: its PO (and GRN) and one input per invoice line.</summary>
internal sealed record MatchSource(
    Guid PoUuid, string PoNumber, Guid? GrnUuid, string? GrnNumber, string MatchType,
    IReadOnlyList<MatchLineInput> Lines);

/// <summary>
/// A48 C9 (tasks 292–294, 296–297) — the "source document lookup": the supplier invoice's purchase order lines (Demand)
/// and goods receipt lines (Warehouse), read the way the existing invoice match already reads them (Finance has always
/// read Demand's and Warehouse's contexts for this — <c>InvoiceRepository.ThreeWayMatch</c>), this organization's own
/// only. Nothing is written.
/// <para>
/// Reference quantity per line (what the invoice quantity must not exceed, BR-MT-001/004):
/// <list type="bullet">
/// <item>Three-way: received and not yet invoiced on the PO line (QtyReceived − QtyInvoiced) — and, when the invoice
/// names a GRN, no more than that GRN accepted for the line. A partial delivery invoiced for what arrived matches
/// (TS-MT-010).</item>
/// <item>Two-way (no GRN required, BR-MT-002): ordered and not yet invoiced (Quantity − QtyInvoiced).</item>
/// </list>
/// An invoice line no PO line of this order stands behind has no reference (always a QuantityMismatch). An invoice
/// without lines is matched as one line "Invoice total": its Subtotal against the GRN's accepted value (or the PO total).
/// </para>
/// </summary>
internal sealed class MatchSourceReader
{
    private readonly DemandDbContext    _demand;
    private readonly WarehouseDbContext _warehouse;

    public MatchSourceReader(DemandDbContext demand, WarehouseDbContext warehouse)
    {
        _demand    = demand;
        _warehouse = warehouse;
    }

    /// <param name="matchType">ThreeWay, TwoWay, or null = three-way when the invoice names a GRN or the PO has receipts.</param>
    public async Task<MatchSource> ReadAsync(Invoice invoice, string? matchType, CancellationToken ct = default)
    {
        if (invoice.PoUuid is not { } poUuid)
            throw new UnprocessableEntityException(
                $"Invoice {invoice.InvoiceNumber} has no purchase order, so there is nothing to match it against.");

        var demandOrg = _demand.TenantContext.OrganizationId;
        var po = await _demand.PurchaseOrders.AsNoTracking().Include(p => p.Lines)
                     .FirstOrDefaultAsync(p => p.UUID == poUuid && !p.IsDelete && p.OrganizationId == demandOrg, ct)
                 ?? throw new NotFoundException("PurchaseOrder", poUuid);

        var warehouseOrg = _warehouse.TenantContext.OrganizationId;
        var grn = invoice.GrnUuid is { } grnUuid
            ? await _warehouse.Grns.AsNoTracking().Include(g => g.Lines)
                  .FirstOrDefaultAsync(g => g.UUID == grnUuid && !g.IsDelete && g.OrganizationId == warehouseOrg, ct)
            : null;

        var received = grn is not null || po.Lines.Any(l => l.QtyReceived > 0m);
        var type = NormalizeType(matchType) ?? (received ? MatchStatuses.ThreeWay : MatchStatuses.TwoWay);
        // REV-23 (D-AP-18): goods received → three-way only; a two-way match would skip the GRN quantity check.
        if (type == MatchStatuses.TwoWay && received)
            throw new BadRequestException($"Purchase order {po.PoNumber} has goods received; its invoices are matched three-way.");
        var threeWay = type == MatchStatuses.ThreeWay;

        var poLines  = po.Lines.ToDictionary(l => l.UUID);
        var accepted = grn?.Lines.GroupBy(l => l.PoLineUuid).ToDictionary(g => g.Key, g => g.Sum(l => l.QtyAccepted));

        var inputs = new List<MatchLineInput>();
        var invoiceLines = invoice.Lines.OrderBy(l => l.LineNo).ToList();

        if (invoiceLines.Count == 0)
        {
            // REV-8 (D-AP-19): what is still to bill — net of what approved invoices already billed (PO lines' QtyInvoiced).
            var grnValue       = grn is null ? 0m : MatchEvaluator.Money(grn.Lines.Sum(l => l.QtyAccepted * (l.UnitCost ?? 0m)));
            var invoicedValue  = MatchEvaluator.Money(po.Lines.Sum(l => l.QtyInvoiced * l.UnitPrice));
            var receivedToBill = MatchEvaluator.Money(po.Lines.Sum(l => Math.Max(0m, l.QtyReceived - l.QtyInvoiced) * l.UnitPrice));
            var expected = threeWay
                ? (grn is not null ? Math.Min(grnValue, receivedToBill) : receivedToBill)
                : Math.Max(0m, po.TotalAmount - invoicedValue);
            inputs.Add(new MatchLineInput(1, "Invoice total", null, null, null,
                1m, expected, threeWay ? 1m : null, expected > 0m ? 1m : null, 1m, invoice.Subtotal));
        }
        else
        {
            // Two lines of one invoice on the same PO line share its reference: each gets what the earlier ones left.
            var used = new Dictionary<Guid, decimal>();
            foreach (var line in invoiceLines)
            {
                if (line.PoLineUuid is not { } poLineUuid || !poLines.TryGetValue(poLineUuid, out var poLine))
                {
                    inputs.Add(new MatchLineInput(line.LineNo, line.ItemDescription, line.UUID, line.PoLineUuid, line.GrnLineUuid,
                        0m, 0m, null, null, line.QtyInvoiced, line.UnitPrice));
                    continue;
                }

                decimal? grnQty;
                decimal reference;
                if (threeWay)
                {
                    reference = Math.Max(0m, poLine.QtyReceived - poLine.QtyInvoiced);
                    if (accepted is not null)
                    {
                        grnQty    = accepted.GetValueOrDefault(poLineUuid);
                        reference = Math.Min(reference, grnQty.Value);
                    }
                    else grnQty = poLine.QtyReceived;
                }
                else
                {
                    grnQty    = null;
                    reference = Math.Max(0m, poLine.Quantity - poLine.QtyInvoiced);
                }

                var already = used.GetValueOrDefault(poLineUuid);
                used[poLineUuid] = already + line.QtyInvoiced;

                inputs.Add(new MatchLineInput(line.LineNo, line.ItemDescription, line.UUID, poLineUuid, line.GrnLineUuid,
                    poLine.Quantity, poLine.UnitPrice, grnQty, Math.Max(0m, reference - already), line.QtyInvoiced, line.UnitPrice));
            }
        }

        return new MatchSource(po.UUID, po.PoNumber, grn?.UUID, grn?.GrnNumber, type, inputs);
    }

    internal static string? NormalizeType(string? matchType) =>
        matchType?.Trim().Replace("-", "").Replace("_", "").ToUpperInvariant() switch
        {
            null or ""                  => null,
            "THREEWAY" or "3WAY" or "3" => MatchStatuses.ThreeWay,
            "TWOWAY" or "2WAY" or "2"   => MatchStatuses.TwoWay,
            _ => throw new BadRequestException("MatchType must be ThreeWay or TwoWay.")
        };
}
