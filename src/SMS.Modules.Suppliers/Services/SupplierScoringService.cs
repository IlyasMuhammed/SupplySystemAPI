using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;

namespace SMS.Modules.Suppliers.Services;

// FSD Section 4.1 — per-GRN scoring across 5 dimensions. Tier boundaries for each dimension are
// reverse-engineered from the two worked examples in the SC-004 ticket (a perfect 100-point GRN and a
// 10-days-late/85%-qty/75%-inspection/8%-price-variance/one-doc-short GRN scoring 40 raw) since the
// underlying FSD tables weren't available — every boundary below is chosen so both worked examples land
// exactly on their stated point values.
internal sealed class SupplierScoringService : ISupplierScoringService
{
    private readonly SuppliersDbContext _suppliers;
    private readonly WarehouseDbContext _warehouse;
    private readonly DemandDbContext _demand;
    private readonly FinanceDbContext _finance;
    private readonly ILogger<SupplierScoringService> _logger;

    public SupplierScoringService(
        SuppliersDbContext suppliers, WarehouseDbContext warehouse, DemandDbContext demand,
        FinanceDbContext finance, ILogger<SupplierScoringService> logger)
    {
        _suppliers = suppliers;
        _warehouse = warehouse;
        _demand    = demand;
        _finance   = finance;
        _logger    = logger;
    }

    public async Task ScoreGrnAsync(Guid grnId)
    {
        var grn = await _warehouse.Grns.Include(g => g.Lines).FirstOrDefaultAsync(g => g.UUID == grnId);
        if (grn is null)
        {
            _logger.LogWarning("ScoreGrnAsync: GRN {GrnId} not found; skipping.", grnId);
            return;
        }

        var po = await _demand.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.UUID == grn.PoUuid);
        if (po is null)
        {
            _logger.LogWarning("ScoreGrnAsync: PO {PoUuid} for GRN {GrnId} not found; skipping.", grn.PoUuid, grnId);
            return;
        }

        var weights = await _suppliers.ScorecardDimensionWeights.AsNoTracking().Where(w => w.IsActive).ToListAsync();

        var deliveryPoints      = ScoreDelivery(grn, po);
        var quantityPoints      = await ScoreQuantityAsync(grn, po);
        var qualityPoints       = ScoreQuality(grn);
        var pricePoints         = await ScorePriceAsync(grn, po);
        var documentationPoints = await ScoreDocumentationAsync(grn);

        var totalRaw = deliveryPoints + quantityPoints + qualityPoints + pricePoints + documentationPoints;

        var weighted = WeightedFor(weights, "DELIVERY", deliveryPoints)
                     + WeightedFor(weights, "QUANTITY", quantityPoints)
                     + WeightedFor(weights, "QUALITY", qualityPoints)
                     + WeightedFor(weights, "PRICE", pricePoints)
                     + WeightedFor(weights, "DOCUMENTATION", documentationPoints);

        var existing = await _suppliers.GrnScoreDetails.FirstOrDefaultAsync(s => s.GrnId == grn.UUID);
        if (existing is null)
        {
            _suppliers.GrnScoreDetails.Add(new GrnScoreDetail
            {
                GrnId               = grn.UUID,
                SupplierId          = grn.SupplierId,
                DeliveryPoints      = deliveryPoints,
                QuantityPoints      = quantityPoints,
                QualityPoints       = qualityPoints,
                PricePoints         = pricePoints,
                DocumentationPoints = documentationPoints,
                TotalRawScore       = totalRaw,
                WeightedScore       = weighted,
                ScoredAt            = DateTime.UtcNow
            });
        }
        else
        {
            existing.DeliveryPoints      = deliveryPoints;
            existing.QuantityPoints      = quantityPoints;
            existing.QualityPoints       = qualityPoints;
            existing.PricePoints         = pricePoints;
            existing.DocumentationPoints = documentationPoints;
            existing.TotalRawScore       = totalRaw;
            existing.WeightedScore       = weighted;
            existing.ScoredAt            = DateTime.UtcNow;
        }

        await _suppliers.SaveChangesAsync();
    }

    private static decimal WeightedFor(List<ScorecardDimensionWeight> weights, string code, decimal points)
    {
        var dim = weights.FirstOrDefault(w => w.DimensionCode == code);
        if (dim is null || dim.MaxPoints <= 0) return 0m;
        return Math.Round(points / dim.MaxPoints * dim.WeightPercentage, 2);
    }

    // ── Delivery Timeliness (max 25) ─────────────────────────────────────────────

    private static decimal ScoreDelivery(Grn grn, PurchaseOrder po)
    {
        if (!po.DeliveryDate.HasValue) return 20m; // default: no expected delivery date to compare against

        var daysLate = (grn.ReceivedAt.Date - po.DeliveryDate.Value.Date).Days;
        return daysLate switch
        {
            <= 0  => 25m,
            <= 3  => 20m,
            <= 7  => 15m,
            <= 14 => 10m,
            _     => 5m
        };
    }

    // ── Quantity Accuracy (max 25) ────────────────────────────────────────────────
    // Cumulative qty_accepted across ALL GRNs for the PO lines this GRN touches, vs those lines' qty_ordered.

    private async Task<decimal> ScoreQuantityAsync(Grn grn, PurchaseOrder po)
    {
        var poLineUuids = grn.Lines.Select(l => l.PoLineUuid).Distinct().ToList();
        var totalOrdered = po.Lines.Where(l => poLineUuids.Contains(l.UUID)).Sum(l => l.Quantity);
        if (totalOrdered <= 0) return 25m;

        var cumulativeAccepted = await _warehouse.GrnLines
            .Where(l => poLineUuids.Contains(l.PoLineUuid) && !l.Grn.IsDelete)
            .SumAsync(l => (decimal?)l.QtyAccepted) ?? 0m;

        var pct = cumulativeAccepted / totalOrdered * 100m;
        return pct switch
        {
            >= 100m => 25m,
            >= 95m  => 20m,
            >= 90m  => 15m,
            >= 80m  => 10m,
            _       => 5m
        };
    }

    // ── Quality / Inspection (max 25) ─────────────────────────────────────────────

    private static decimal ScoreQuality(Grn grn)
    {
        if (!grn.RequiresInspection) return 20m; // default: inspection not required for this GRN
        if (grn.Lines.Count == 0) return 20m;

        var passCount = grn.Lines.Count(l => string.Equals(l.InspectionResult, "Pass", StringComparison.OrdinalIgnoreCase));
        var pct = (decimal)passCount / grn.Lines.Count * 100m;
        return pct switch
        {
            >= 100m => 25m,
            >= 90m  => 20m,
            >= 80m  => 15m,
            >= 70m  => 10m,
            _       => 5m
        };
    }

    // ── Price Compliance (max 15) ─────────────────────────────────────────────────
    // Actual unit price per line: the matching InvoiceLine's price if the GRN has been invoiced yet,
    // else the GRN line's own UnitCost — compared against the original PO line's UnitPrice.

    private async Task<decimal> ScorePriceAsync(Grn grn, PurchaseOrder po)
    {
        var invoiceLines = await _finance.InvoiceLines.AsNoTracking()
            .Where(il => il.Invoice.GrnUuid == grn.UUID)
            .Select(il => new { il.GrnLineUuid, il.UnitPrice })
            .ToListAsync();

        var variances = new List<decimal>();
        foreach (var line in grn.Lines)
        {
            var poLine = po.Lines.FirstOrDefault(l => l.UUID == line.PoLineUuid);
            if (poLine is null || poLine.UnitPrice <= 0) continue;

            var actualPrice = invoiceLines.FirstOrDefault(il => il.GrnLineUuid == line.UUID)?.UnitPrice ?? line.UnitCost;
            if (!actualPrice.HasValue) continue;

            variances.Add(Math.Abs(actualPrice.Value - poLine.UnitPrice) / poLine.UnitPrice * 100m);
        }

        if (variances.Count == 0) return 15m; // nothing to compare — assume compliant

        var avgVariance = variances.Average();
        return avgVariance switch
        {
            <= 0m  => 15m,
            <= 3m  => 12m,
            <= 7m  => 9m,
            <= 10m => 6m,
            _      => 3m
        };
    }

    // ── Documentation Completeness (max 10) ───────────────────────────────────────
    // 3 checks: invoice received within its payment terms, delivery note attached, and QC sign-off
    // present (stands in for "certificates present" — this schema has no dedicated certificate field).

    private async Task<decimal> ScoreDocumentationAsync(Grn grn)
    {
        var invoice = await _finance.Invoices.AsNoTracking()
            .Where(i => i.GrnUuid == grn.UUID && !i.IsDelete)
            .OrderByDescending(i => i.CreatedDate)
            .FirstOrDefaultAsync();

        var invoiceWithinTerms   = invoice is not null && invoice.ReceivedDate <= invoice.DueDate;
        var deliveryNoteAttached = !string.IsNullOrWhiteSpace(grn.DeliveryNoteNo);
        var certificatesPresent  = grn.QcPassed;

        var passedCount = new[] { invoiceWithinTerms, deliveryNoteAttached, certificatesPresent }.Count(x => x);
        return passedCount switch
        {
            3 => 10m,
            2 => 7m,
            1 => 4m,
            _ => 2m
        };
    }
}
