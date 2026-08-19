using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Events;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Warehouse.Services;

internal sealed class GrnStatusHandler : IDocumentStatusHandler
{
    public string InterfaceCode => "GRN";

    private readonly WarehouseDbContext             _wh;
    private readonly DemandDbContext                _demand;
    private readonly InventoryDbContext             _inv;
    private readonly IGrnStockPoster                _stockPoster;
    private readonly IEnumerable<IGrnEventPublisher> _eventPublishers;
    private readonly ILogger<GrnStatusHandler>       _logger;

    public GrnStatusHandler(
        WarehouseDbContext wh,
        DemandDbContext demand,
        InventoryDbContext inv,
        IGrnStockPoster stockPoster,
        IEnumerable<IGrnEventPublisher> eventPublishers,
        ILogger<GrnStatusHandler> logger)
    {
        _wh              = wh;
        _demand          = demand;
        _inv             = inv;
        _stockPoster     = stockPoster;
        _eventPublishers = eventPublishers;
        _logger          = logger;
    }

    public async Task<string?> GetStatusAsync(Guid documentId)
    {
        var grn = await _wh.Grns
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.UUID == documentId && !g.IsDelete);
        return grn?.Status;
    }

    public async Task UpdateStatusAsync(Guid documentId, string newStatus)
    {
        var grn = await _wh.Grns
            .Include(g => g.Lines)
            .FirstOrDefaultAsync(g => g.UUID == documentId && !g.IsDelete);
        if (grn is null) return;

        switch (newStatus)
        {
            case "PENDING":
                // GRN approval workflow started (QC already complete at this point)
                grn.Status = "PENDING_APPROVAL";
                break;

            case "APPROVED":
                await ApproveGrnAsync(grn);
                return; // ApproveGrnAsync saves

            case "REJECTED":
                grn.Status = "REJECTED";
                break;

            case "CANCELLED":
            case "CLOSED":
                grn.Status = "DRAFT";
                break;

            default:
                return;
        }

        grn.ModifiedDate = DateTime.UtcNow;
        await _wh.SaveChangesAsync();
    }

    // ── Full approval: stock post + PO update + status change ─────────────────

    private async Task ApproveGrnAsync(Domain.Grn grn)
    {
        if (!grn.WarehouseUuid.HasValue)
            throw new UnprocessableEntityException(
                "Cannot approve GRN: no receiving warehouse is assigned. " +
                "Edit the GRN to set a warehouse before approving.");

        // Inspection completeness is enforced by the GRN_QC workflow gate,
        // not at approval time — so we do not block here.

        // Post stock — if this throws, GRN remains PENDING_APPROVAL
        await _stockPoster.PostToInventoryAsync(grn, 0 /* actor tracked in workflow audit log */);

        // PV-004 — the accepted unit price on each received line becomes the variant's new
        // last-purchase-price, used elsewhere (reorder pickers, cost rollups) as the most
        // recent known cost. Only lines with both a resolved variant and a recorded cost apply.
        var variantUuids = grn.Lines
            .Where(l => l.VariantUuid.HasValue && l.UnitCost.HasValue)
            .Select(l => l.VariantUuid!.Value)
            .Distinct()
            .ToList();
        if (variantUuids.Count > 0)
        {
            var variants = await _inv.ProductVariants
                .Where(v => variantUuids.Contains(v.Uuid))
                .ToListAsync();
            foreach (var grnLine in grn.Lines.Where(l => l.VariantUuid.HasValue && l.UnitCost.HasValue))
            {
                var variant = variants.FirstOrDefault(v => v.Uuid == grnLine.VariantUuid!.Value);
                if (variant is not null)
                    variant.LastPurchasePrice = grnLine.UnitCost!.Value;
            }
            await _inv.SaveChangesAsync();
        }

        // Update PO line cumulative quantities and PO status
        var po = await _demand.PurchaseOrders
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.UUID == grn.PoUuid && !p.IsDelete);

        if (po is not null)
        {
            foreach (var grnLine in grn.Lines)
            {
                var poLine = po.Lines.FirstOrDefault(l => l.UUID == grnLine.PoLineUuid);
                if (poLine is not null)
                    poLine.QtyReceived += grnLine.QtyReceived;
            }

            po.Status       = po.Lines.All(l => l.QtyReceived >= l.Quantity) ? "RECEIVED" : "PARTIALLY_RECEIVED";
            po.ModifiedDate = DateTime.UtcNow;
            await _demand.SaveChangesAsync();
        }

        var now          = DateTime.UtcNow;
        grn.Status       = "APPROVED";
        grn.ApprovedAt   = now;
        grn.ModifiedDate = now;
        await _wh.SaveChangesAsync();

        // Auto-resolve any SROs awaiting replacement delivery for this PO
        var replacementSros = await _wh.SupplierReturnOrders
            .Where(s => s.OriginalPoUuid == grn.PoUuid
                     && s.Status == "AWAITING_REPLACEMENT"
                     && s.IsActive)
            .ToListAsync();

        foreach (var sro in replacementSros)
        {
            sro.Status         = "RESOLVED_REPLACEMENT";
            sro.ResolutionType = "REPLACEMENT";
            sro.ResolvedAt     = now;
            sro.ModifiedDate   = now;
        }

        if (replacementSros.Count > 0)
            await _wh.SaveChangesAsync();

        var evt = new GrnApprovedEvent
        {
            GrnUuid    = grn.UUID,
            GrnNumber  = grn.GrnNumber,
            PoUuid     = grn.PoUuid,
            ApprovedBy = 0,
            ApprovedAt = now
        };

        // Fan out to every registered publisher (scorecard scoring, invoice auto-creation, ...).
        // The approval already committed above — one publisher failing must not surface as a
        // failure of the approval, and must not stop the others from running.
        foreach (var publisher in _eventPublishers)
        {
            try
            {
                await publisher.PublishGrnApprovedAsync(evt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "GrnEventPublisher {Publisher} failed for GRN {GrnUuid} approval.",
                    publisher.GetType().Name, grn.UUID);
            }
        }
    }
}
