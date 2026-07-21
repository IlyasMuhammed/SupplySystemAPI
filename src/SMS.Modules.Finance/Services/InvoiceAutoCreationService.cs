using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Warehouse.Data;

namespace SMS.Modules.Finance.Services;

internal interface IInvoiceAutoCreationService
{
    Task CreateFromGrnAsync(Guid grnUuid);
}

// Triggered off GRN approval (see InvoiceAutoCreateGrnEventPublisher). Builds a system-generated
// invoice from the GRN's accepted quantities and PO-derived unit costs so AP has a starting point
// instead of a blank form — SupplierInvoiceNo/DueDate/tax are left for the AP team to correct once
// the supplier's actual paper invoice arrives (PatchAsync).
internal sealed class InvoiceAutoCreationService : IInvoiceAutoCreationService
{
    private readonly WarehouseDbContext _warehouse;
    private readonly FinanceDbContext   _db;
    private readonly IInvoiceRepository _invoices;

    public InvoiceAutoCreationService(WarehouseDbContext warehouse, FinanceDbContext db, IInvoiceRepository invoices)
    {
        _warehouse = warehouse;
        _db        = db;
        _invoices  = invoices;
    }

    // Safe to call more than once for the same GRN — a pre-existing invoice for it is a no-op,
    // so retried/duplicate Hangfire jobs can't create duplicate invoices.
    public async Task CreateFromGrnAsync(Guid grnUuid)
    {
        var alreadyExists = await _db.Invoices.AnyAsync(i => i.GrnUuid == grnUuid && !i.IsDelete);
        if (alreadyExists) return;

        var grn = await _warehouse.Grns
            .Include(g => g.Lines)
            .FirstOrDefaultAsync(g => g.UUID == grnUuid && !g.IsDelete);
        if (grn is null || grn.Status != "APPROVED") return;

        var lines = grn.Lines
            .Where(l => l.QtyAccepted > 0)
            .Select(l => new InvoiceLineRequest
            {
                GrnLineUuid     = l.UUID,
                PoLineUuid      = l.PoLineUuid,
                ItemDescription = l.ItemDescription,
                UnitOfMeasure   = l.UnitOfMeasure,
                QtyInvoiced     = l.QtyAccepted,
                UnitPrice       = l.UnitCost ?? 0m
            })
            .ToList();

        // Nothing costed (e.g. unit costs were never entered on the GRN) — a zero-value invoice
        // would fail InvoiceRepository's "total must be greater than zero" check anyway, and isn't
        // useful to AP. Leave this GRN for manual invoice entry instead.
        if (lines.Count == 0 || lines.Sum(l => l.QtyInvoiced * l.UnitPrice) <= 0)
            return;

        var receivedDate = grn.ReceivedAt.Date;

        var req = new CreateInvoiceRequest
        {
            SupplierId   = grn.SupplierId,
            PoUuid       = grn.PoUuid,
            GrnUuid      = grn.UUID,
            InvoiceDate  = receivedDate,
            ReceivedDate = receivedDate,
            // Supplier.PreferredPaymentTerms is a lookup reference, not a day count, and Finance
            // has no route to resolve it here — 30 days is a placeholder the AP team overrides via
            // PatchAsync once the supplier's real invoice/terms are on hand.
            DueDate      = receivedDate.AddDays(30),
            Currency     = "PKR",
            TaxAmount    = 0m,
            Notes        = $"Auto-generated from GRN {grn.GrnNumber} on approval.",
            Lines        = lines
        };

        // CreatedBy 0: system-generated, mirrors the "actor tracked in workflow audit log"
        // convention GrnStatusHandler already uses for the stock-posting side of GRN approval.
        await _invoices.CreateAsync(req, createdBy: 0);
    }
}
