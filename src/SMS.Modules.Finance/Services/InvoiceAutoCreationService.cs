using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Finance.Services;

internal interface IInvoiceAutoCreationService
{
    Task CreateFromGrnAsync(Guid grnUuid);
}

// Triggered off GRN approval (see InvoiceAutoCreateGrnEventPublisher). Builds a system-generated
// invoice from the GRN's accepted quantities and PO-derived unit costs so AP has a starting point
// instead of a blank form — SupplierInvoiceNo/DueDate/tax are left for the AP team to correct once
// the supplier's actual paper invoice arrives (PatchAsync).
//
// Calls IInvoiceRepository directly rather than IInvoiceService — this is the actual creation path
// for the overwhelming majority of invoices (manual entry is rare), so the INVOICE_RECEIVED timeline
// event has to be enqueued here too, not just in InvoiceService.CreateAsync, or auto-created invoices
// would silently never get a timeline entry for their own creation.
internal sealed class InvoiceAutoCreationService : IInvoiceAutoCreationService
{
    /// <summary>What an auto-invoice is in when the organization's base currency cannot be found (the old fixed value).</summary>
    internal const string FallbackCurrency = "PKR";

    private readonly WarehouseDbContext _warehouse;
    private readonly FinanceDbContext   _db;
    private readonly IInvoiceRepository _invoices;
    private readonly IBackgroundJobClient _jobs;
    private readonly ITaxCodeLookup?               _taxCodes;
    private readonly IOrganizationCurrencyService? _orgCurrency;
    private readonly ICurrencyCodeLookup?          _currencyCodes;

    /// <param name="taxCodes">S-3: the organization's default purchase tax code is applied when one is set. Optional for hand-built harnesses.</param>
    /// <param name="orgCurrency">With <paramref name="currencyCodes"/>, the organization's base currency — what a PO (which has no currency) is priced in. Optional.</param>
    /// <param name="currencyCodes">See <paramref name="orgCurrency"/>.</param>
    public InvoiceAutoCreationService(
        WarehouseDbContext warehouse, FinanceDbContext db, IInvoiceRepository invoices, IBackgroundJobClient jobs,
        ITaxCodeLookup? taxCodes = null, IOrganizationCurrencyService? orgCurrency = null, ICurrencyCodeLookup? currencyCodes = null)
    {
        _warehouse     = warehouse;
        _db            = db;
        _invoices      = invoices;
        _jobs          = jobs;
        _taxCodes      = taxCodes;
        _orgCurrency   = orgCurrency;
        _currencyCodes = currencyCodes;
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

        // S-3: the organization's default purchase tax code, when it has one — the repository works the
        // tax out from it. Without one the tax stays 0, as before, for AP to correct (PatchAsync).
        // Checked here as the repository would check it, so a misconfigured default leaves the invoice with
        // no code rather than failing (and endlessly retrying) the job.
        var defaultTaxCode = _taxCodes is null ? null : await _taxCodes.GetDefaultAsync(TaxCodeUsage.Purchase);
        if (defaultTaxCode is not null
            && (!defaultTaxCode.IsActive || !TaxCodeUsage.Allows(defaultTaxCode.Usage, TaxCodeUsage.Purchase)))
            defaultTaxCode = null;

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
            // A purchase order carries no currency: its prices are in the organization's own (base)
            // currency. PKR, the old fixed value, only when that cannot be found.
            Currency     = await BaseCurrencyCodeAsync(grn.OrganizationId) ?? FallbackCurrency,
            TaxCodeUuid  = defaultTaxCode?.Uuid,
            TaxAmount    = 0m,
            Notes        = $"Auto-generated from GRN {grn.GrnNumber} on approval.",
            Lines        = lines
        };

        // CreatedBy 0: system-generated, mirrors the "actor tracked in workflow audit log"
        // convention GrnStatusHandler already uses for the stock-posting side of GRN approval.
        var uuid = await _invoices.CreateAsync(req, createdBy: 0);

        var inv = await _invoices.GetByUuidAsync(uuid);
        if (inv is not null)
        {
            var notes = $"Amount: {inv.TotalAmount:F2} {inv.Currency}, Match status: {inv.MatchStatus}.";
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                inv.TraceId,
                new TimelineEvent("INVOICE_RECEIVED", "INVOICE", uuid, inv.InvoiceNumber, DateTime.UtcNow, 0, notes),
                "INVOICE", inv.InvoiceNumber));
        }
    }

    /// <summary>The organization's base currency as an ISO code, or null when it has none or it cannot be found out here.</summary>
    private async Task<string?> BaseCurrencyCodeAsync(Guid organizationId)
    {
        if (_orgCurrency is null || _currencyCodes is null) return null;

        var id = await _orgCurrency.GetBaseCurrencyIdAsync(organizationId);
        if (id is null) return null;

        var code = (await _currencyCodes.GetCodeAsync(id.Value))?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(code) ? null : code;
    }
}
