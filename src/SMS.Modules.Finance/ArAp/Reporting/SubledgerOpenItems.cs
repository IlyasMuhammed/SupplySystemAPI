using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.ArAp.Reporting;

/// <summary>Which sub-ledger a figure belongs to.</summary>
internal enum SubledgerSide { Receivable, Payable }

/// <summary>The document kinds an open item can be (A48 RPT, D-RPT-2).</summary>
internal static class OpenItemTypes
{
    // Receivables — debit items
    public const string SalesInvoice     = "SALES_INVOICE";
    // Receivables — credit items (what the customer has on account)
    public const string CustomerPayment  = "CUSTOMER_PAYMENT";
    public const string SalesCreditNote  = "SALES_CREDIT_NOTE";
    // Payables — debit items (what we owe)
    public const string SupplierInvoice  = "SUPPLIER_INVOICE";
    // Payables — credit items (what the vendor owes back / we hold against them)
    public const string SupplierAdvance  = "SUPPLIER_ADVANCE";
    public const string SupplierCredit   = "SUPPLIER_CREDIT_NOTE";
    public const string SupplierDebit    = "SUPPLIER_DEBIT_NOTE";
    public const string WriteOff         = "WRITE_OFF";
}

/// <summary>
/// One open document of a sub-ledger as of a day: what is still owed on it (<see cref="Open"/>, always positive) in its
/// own currency, and the rate that takes it to the base (A35 lock; 1 when none — the A42 D-12 precedent).
/// <see cref="IsCredit"/> items reduce the partner's balance (unapplied payments, open credit notes, advances).
/// </summary>
internal sealed record OpenItemRow(
    string   Type,
    Guid     DocumentUuid,
    string   DocumentNumber,
    string?  Reference,
    Guid     PartnerId,
    string   CurrencyCode,
    DateTime DocumentDate,
    DateTime DueDate,
    decimal  Original,
    decimal  Open,
    decimal? LockedRate,
    bool     IsCredit,
    string?  Status)
{
    /// <summary>A current rate found for a document with no locked one (contract §0); null = none looked up / found.</summary>
    public decimal? ResolvedRate { get; init; }
    /// <summary>No locked rate, a foreign currency, and no current rate either: counted 1:1.</summary>
    public bool RateMissing { get; init; }
    public decimal Rate => LockedRate ?? ResolvedRate ?? 1m;
    public decimal OpenBase => Math.Round(Open * Rate, 2, MidpointRounding.AwayFromZero);
    public decimal SignedOpen => IsCredit ? -Open : Open;
    public decimal SignedOpenBase => IsCredit ? -OpenBase : OpenBase;
}

/// <summary>
/// A48 RPT (D-1, D-12, D-RPT-1..4) — the one open-item engine behind aging, balances, open items, statements and vendor
/// reconciliation. Every figure comes from the SCM Finance documents (never GL lines), <b>as of the end of a day</b>:
/// <list type="bullet">
/// <item><b>Receivables</b> — the A29 R3 rules exactly: an issued / opening invoice counts from its ledger entry, a
/// payment reduces it from its payment date unless its bounce was booked by then, a POS credit note dated by then takes
/// off what it applied; cancelled = owed only for days before the cancellation; drafts are never receivables. Credits:
/// RECEIVED payments' unallocated part (not bounced by then) and open POS credit notes (amount − applied − refunded).</item>
/// <item><b>Payables</b> — only an approved invoice is a payable (SAP alignment), from its approval; one reversed after
/// the day still counts for it. Paid = posted supplier-payment lines dated by then (a bounce after the day still counts)
/// + legacy single-invoice payments not reversed. Credit / debit notes are already deducted from the invoice total (their
/// timing is not reconstructed — D-RPT-3). Credits: advances' available balance, carried-forward credit / debit notes,
/// approved bad-debt write-offs.</item>
/// </list>
/// Each query names the organization explicitly (<c>IgnoreQueryFilters</c> + <c>OrganizationId == org</c>) so the
/// predicate is index-seekable and a super admin sees only the organization they are working in.
/// </summary>
internal sealed class SubledgerOpenItems
{
    private const string InvoiceReferenceType = "SalesInvoice";
    private const string PaymentReferenceType = "CustomerPayment";

    /// <summary>A debit note that still stands (ApDebitMemoService's live set): not a draft, not written off.</summary>
    private static readonly string[] LiveDebitNoteStatuses = ["ISSUED", "ACKNOWLEDGED", "DISPUTED", "SETTLED"];

    private readonly FinanceDbContext _db;

    public SubledgerOpenItems(FinanceDbContext db) => _db = db;

    public Guid OrganizationId
    {
        get
        {
            var org = _db.TenantContext.OrganizationId;
            if (org == Guid.Empty)
                throw new BadRequestException("Pick an organization first: sub-ledger figures are one organization's books.");
            return org;
        }
    }

    public Task<List<OpenItemRow>> ForSideAsync(
        SubledgerSide side, DateTime asOf, Guid? partnerId, bool includeCredits, CancellationToken ct = default) =>
        side == SubledgerSide.Receivable
            ? ReceivablesAsync(asOf, partnerId, includeCredits, ct)
            : PayablesAsync(asOf, partnerId, includeCredits, ct);

    // ── Receivables ──────────────────────────────────────────────────────────

    public async Task<List<OpenItemRow>> ReceivablesAsync(
        DateTime asOf, Guid? partnerId, bool includeCredits, CancellationToken ct = default)
    {
        var org = OrganizationId;
        var end = asOf.Date.AddDays(1);

        var ledger      = _db.CustomerLedgerEntries.IgnoreQueryFilters().Where(e => e.OrganizationId == org);
        var allocations = _db.PaymentAllocations.IgnoreQueryFilters().Where(a => a.OrganizationId == org);
        var documents   = _db.SalesInvoices.IgnoreQueryFilters().Where(i => i.OrganizationId == org && !i.IsDelete);
        var applications = _db.CustomerCreditApplications.IgnoreQueryFilters().Where(a => a.OrganizationId == org);

        var booked = ledger.Where(e =>
            (e.EntryType == CustomerLedgerEntryTypes.Invoice || e.EntryType == CustomerLedgerEntryTypes.OpeningBal)
            && e.ReferenceType == InvoiceReferenceType && e.EntryDate < end);
        // A bounce is a PAYMENT debit. Few of them exist, so they are read once and passed as a list rather than probed
        // per allocation (the ledger has no index on ReferenceId; the probe doubled the 10k-item aging time — D-RPT-1).
        var bounces = await ledger.AsNoTracking()
            .Where(e => e.EntryType == CustomerLedgerEntryTypes.Payment && e.ReferenceType == PaymentReferenceType
                     && e.DebitAmount > 0m && e.EntryDate < end)
            .Select(e => e.ReferenceId).Distinct().ToListAsync(ct);

        var invoices = documents
            .Where(i => i.Status != SalesInvoiceStatuses.Draft && i.Status != SalesInvoiceStatuses.CreditNote)
            .Where(i => i.Status != SalesInvoiceStatuses.Cancelled || (i.CancelledAt != null && i.CancelledAt >= end))
            .Where(i => booked.Any(b => b.ReferenceId == i.UUID));
        if (partnerId is { } p) invoices = invoices.Where(i => i.PartnerId == p);

        var rows = await invoices.AsNoTracking()
            .Select(i => new
            {
                i.UUID, i.InvoiceNumber, i.SaleOrderNumber, i.PartnerId, i.CurrencyCode, i.InvoiceDate, i.DueDate,
                i.GrandTotal, i.ExchangeRate, i.Status,
                Paid = allocations
                    .Where(a => a.SalesInvoiceId == i.Id
                             && a.CustomerPayment.PaymentDate < end
                             && a.CustomerPayment.Status != CustomerPaymentStatuses.Reversed
                             && !bounces.Contains(a.CustomerPayment.UUID))
                    .Sum(a => (decimal?)a.AllocatedAmount) ?? 0m,
                // D-AR-12: A48 applications to this invoice (live by then), plus each note raised against it applying
                // directly what its own application rows do not account for (A38's direct application).
                Credited = (applications
                        .Where(a => a.InvoiceId == i.Id && a.AppliedAt < end && (a.ReversedAt == null || a.ReversedAt >= end))
                        .Sum(a => (decimal?)a.Amount) ?? 0m)
                    + (documents
                        .Where(c => c.Status == SalesInvoiceStatuses.CreditNote && c.OriginalInvoiceUuid == i.UUID && c.InvoiceDate < end)
                        .Sum(c => (decimal?)c.CreditedAmount) ?? 0m)
                    - (applications
                        .Where(a => a.ReversedAt == null && a.CreditNote.OriginalInvoiceUuid == i.UUID
                                 && a.CreditNote.Status == SalesInvoiceStatuses.CreditNote && a.CreditNote.InvoiceDate < end)
                        .Sum(a => (decimal?)a.Amount) ?? 0m)
            })
            .Where(x => x.GrandTotal - x.Paid - x.Credited > 0m)
            .ToListAsync(ct);

        var result = rows.Select(x => new OpenItemRow(
                OpenItemTypes.SalesInvoice, x.UUID, x.InvoiceNumber, NullIfEmpty(x.SaleOrderNumber), x.PartnerId, x.CurrencyCode,
                x.InvoiceDate, x.DueDate, x.GrandTotal, x.GrandTotal - x.Paid - x.Credited, x.ExchangeRate, false,
                x.Paid + x.Credited > 0m ? "PARTIALLY_PAID" : "OPEN"))
            .ToList();

        if (!includeCredits) return result;

        var payments = _db.CustomerPayments.IgnoreQueryFilters()
            .Where(c => c.OrganizationId == org && c.PaymentDate < end && c.Status != CustomerPaymentStatuses.Reversed
                     && !bounces.Contains(c.UUID));
        if (partnerId is { } pp) payments = payments.Where(c => c.PartnerId == pp);

        var unapplied = await payments.AsNoTracking()
            .Select(c => new
            {
                c.UUID, c.PaymentNumber, c.BankReference, c.ChequeNumber, c.PartnerId, c.CurrencyCode, c.PaymentDate, c.Amount,
                c.ExchangeRate,
                Applied = allocations.Where(a => a.CustomerPaymentId == c.Id).Sum(a => (decimal?)a.AllocatedAmount) ?? 0m
            })
            .Where(x => x.Amount - x.Applied > 0m)
            .ToListAsync(ct);

        result.AddRange(unapplied.Select(x => new OpenItemRow(
            OpenItemTypes.CustomerPayment, x.UUID, x.PaymentNumber, x.BankReference ?? x.ChequeNumber, x.PartnerId, x.CurrencyCode,
            x.PaymentDate, x.PaymentDate, x.Amount, x.Amount - x.Applied, x.ExchangeRate, true,
            x.Applied > 0m ? "PARTIALLY_APPLIED" : "UNAPPLIED")));

        var refunds = _db.CustomerRefunds.IgnoreQueryFilters().Where(r => r.OrganizationId == org && r.RefundDate < end);
        var notes   = documents.Where(c => c.Status == SalesInvoiceStatuses.CreditNote && c.InvoiceDate < end);
        if (partnerId is { } pn) notes = notes.Where(c => c.PartnerId == pn);

        var openNotes = await notes.AsNoTracking()
            .Select(c => new
            {
                c.UUID, c.InvoiceNumber, c.SourceNumber, c.PartnerId, c.CurrencyCode, c.InvoiceDate, c.GrandTotal, c.CreditedAmount,
                c.ExchangeRate,
                Refunded = refunds.Where(r => r.CreditNoteUuid == c.UUID).Sum(r => (decimal?)r.Amount) ?? 0m,
                // CreditedAmount is today's. As of the day: applications made after it were not yet taken off the note
                // (add back); applications made before it and reversed after it still were (take off again — REV-17).
                AppliedLater = (applications.Where(a => a.CreditNoteId == c.Id && a.ReversedAt == null && a.AppliedAt >= end)
                                    .Sum(a => (decimal?)a.Amount) ?? 0m)
                             - (applications.Where(a => a.CreditNoteId == c.Id && a.AppliedAt < end && a.ReversedAt != null && a.ReversedAt >= end)
                                    .Sum(a => (decimal?)a.Amount) ?? 0m)
            })
            .Where(x => x.GrandTotal - x.CreditedAmount + x.AppliedLater - x.Refunded > 0m)
            .ToListAsync(ct);

        result.AddRange(openNotes.Select(x => new OpenItemRow(
            OpenItemTypes.SalesCreditNote, x.UUID, x.InvoiceNumber, x.SourceNumber, x.PartnerId, x.CurrencyCode,
            x.InvoiceDate, x.InvoiceDate, x.GrandTotal, x.GrandTotal - x.CreditedAmount + x.AppliedLater - x.Refunded, x.ExchangeRate, true,
            x.CreditedAmount - x.AppliedLater + x.Refunded > 0m ? "PARTIALLY_APPLIED" : "UNAPPLIED")));

        return result;
    }

    // ── Payables ─────────────────────────────────────────────────────────────

    public async Task<List<OpenItemRow>> PayablesAsync(
        DateTime asOf, Guid? partnerId, bool includeCredits, CancellationToken ct = default)
    {
        var org = OrganizationId;
        var end = asOf.Date.AddDays(1);

        var lines  = _db.SupplierPaymentLines.IgnoreQueryFilters().Where(l => l.OrganizationId == org);
        var legacy = _db.Payments.IgnoreQueryFilters().Where(p => p.OrganizationId == org && !p.IsDelete);
        var noteApps = _db.SupplierNoteApplications.IgnoreQueryFilters().Where(a => a.OrganizationId == org);

        var invoices = _db.Invoices.IgnoreQueryFilters()
            .Where(i => i.OrganizationId == org && !i.IsDelete)
            .Where(i => i.MatchStatus == InvoiceMatchStatus.Approved
                     || (i.MatchStatus == InvoiceMatchStatus.Reversed && i.ReversedAt != null && i.ReversedAt >= end))
            .Where(i => (i.ApprovedAt ?? i.InvoiceDate) < end);
        if (partnerId is { } p) invoices = invoices.Where(i => i.SupplierId == p);

        var rows = await invoices.AsNoTracking()
            .Select(i => new
            {
                i.UUID, i.InvoiceNumber, i.SupplierInvoiceNo, i.SupplierId, i.Currency, i.InvoiceDate, i.DueDate, i.TotalAmount,
                i.ExchangeRate,
                Paid = (lines.Where(l => l.InvoiceUuid == i.UUID && l.SupplierPayment.PaymentDate < end
                                      && (l.SupplierPayment.Status == "POSTED"
                                          || (l.SupplierPayment.Status == "BOUNCED" && l.SupplierPayment.BouncedAt != null
                                              && l.SupplierPayment.BouncedAt >= end)))
                             .Sum(l => (decimal?)l.AllocatedAmount) ?? 0m)
                     + (legacy.Where(x => x.InvoiceId == i.Id && x.Status != "Reversed" && x.PaymentDate < end)
                              .Sum(x => (decimal?)x.AmountPaid) ?? 0m),
                // TotalAmount is today's. As of the day: an A48 note application made after it had not yet reduced it
                // (add back); one made before it and reversed after it still had (take off again — REV-17).
                AppliedLater = (noteApps.Where(a => a.InvoiceId == i.Id && a.ReversedAt == null && a.AppliedAt >= end)
                                    .Sum(a => (decimal?)a.Amount) ?? 0m)
                             - (noteApps.Where(a => a.InvoiceId == i.Id && a.AppliedAt < end && a.ReversedAt != null && a.ReversedAt >= end)
                                    .Sum(a => (decimal?)a.Amount) ?? 0m)
            })
            .Where(x => x.TotalAmount + x.AppliedLater - x.Paid > 0m)
            .ToListAsync(ct);

        var result = rows.Select(x => new OpenItemRow(
                OpenItemTypes.SupplierInvoice, x.UUID, x.InvoiceNumber, NullIfEmpty(x.SupplierInvoiceNo), x.SupplierId, x.Currency,
                x.InvoiceDate, x.DueDate, x.TotalAmount + x.AppliedLater, x.TotalAmount + x.AppliedLater - x.Paid, x.ExchangeRate, false,
                x.Paid > 0m ? "PARTIALLY_PAID" : "OPEN"))
            .ToList();

        if (!includeCredits) return result;

        // Advances: the available balance as it stands (not reconstructed for a past day — D-RPT-3).
        var payments = _db.SupplierPayments.IgnoreQueryFilters().Where(sp => sp.OrganizationId == org);
        var advances = _db.SupplierAdvancePayments.IgnoreQueryFilters()
            .Where(a => a.OrganizationId == org && a.AvailableBalance > 0m && a.CreatedDate < end);
        if (partnerId is { } pa) advances = advances.Where(a => a.SupplierId == pa);

        var openAdvances = await advances.AsNoTracking()
            .Select(a => new
            {
                a.SupplierPaymentUuid, a.SupplierId, a.OriginalAmount, a.AvailableBalance, a.CreatedDate,
                Payment = payments.Where(sp => sp.UUID == a.SupplierPaymentUuid)
                    .Select(sp => new { sp.PaymentNumber, sp.PaymentDate, sp.CurrencyCode, sp.ExchangeRate, sp.BankAccount, sp.ChequeNo })
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        result.AddRange(openAdvances.Select(x => new OpenItemRow(
            OpenItemTypes.SupplierAdvance, x.SupplierPaymentUuid, x.Payment?.PaymentNumber ?? "ADVANCE",
            x.Payment?.ChequeNo ?? x.Payment?.BankAccount, x.SupplierId, x.Payment?.CurrencyCode ?? BaseCurrencyFallback,
            x.Payment?.PaymentDate ?? x.CreatedDate, x.Payment?.PaymentDate ?? x.CreatedDate,
            x.OriginalAmount, x.AvailableBalance, x.Payment?.ExchangeRate, true, "UNAPPLIED")));

        var invoiceCurrency = _db.Invoices.IgnoreQueryFilters().Where(i => i.OrganizationId == org);

        var credits = _db.CreditNotes.IgnoreQueryFilters()
            .Where(c => c.OrganizationId == org && !c.IsDelete && c.ApplicationStatus == "CARRIED_FORWARD"
                     && (c.CarriedForwardAmount ?? c.CreditAmount) > 0m && c.CreditDate < end);
        if (partnerId is { } pc) credits = credits.Where(c => c.SupplierId == pc);

        var openCredits = await credits.AsNoTracking()
            .Select(c => new
            {
                c.UUID, c.CreditNoteNumber, c.SupplierCreditNoteNo, c.SupplierId, c.CreditDate, c.CreditAmount, c.CarriedForwardAmount,
                Doc = invoiceCurrency.Where(i => i.UUID == c.InvoiceUuid).Select(i => new { i.Currency, i.ExchangeRate }).FirstOrDefault()
            })
            .ToListAsync(ct);

        result.AddRange(openCredits.Select(x => new OpenItemRow(
            OpenItemTypes.SupplierCredit, x.UUID, x.CreditNoteNumber, NullIfEmpty(x.SupplierCreditNoteNo), x.SupplierId,
            x.Doc?.Currency ?? BaseCurrencyFallback, x.CreditDate, x.CreditDate, x.CreditAmount, x.CarriedForwardAmount ?? x.CreditAmount,
            x.Doc?.ExchangeRate, true, "CARRIED_FORWARD")));

        var debits = _db.DebitNotes.IgnoreQueryFilters()
            .Where(d => d.OrganizationId == org && !d.IsDelete && d.ApplicationStatus == "CARRIED_FORWARD" && LiveDebitNoteStatuses.Contains(d.Status)
                     && (d.CarriedForwardAmount ?? d.DebitAmount) > 0m && (d.IssuedAt ?? d.CreatedDate) < end);
        if (partnerId is { } pd) debits = debits.Where(d => d.SupplierId == pd);

        var openDebits = await debits.AsNoTracking()
            .Select(d => new
            {
                d.UUID, d.DebitNoteNumber, d.SroNumber, d.SupplierId, Date = d.IssuedAt ?? d.CreatedDate, d.DebitAmount, d.CarriedForwardAmount,
                Doc = invoiceCurrency.Where(i => i.UUID == d.InvoiceUuid).Select(i => new { i.Currency, i.ExchangeRate }).FirstOrDefault()
            })
            .ToListAsync(ct);

        result.AddRange(openDebits.Select(x => new OpenItemRow(
            OpenItemTypes.SupplierDebit, x.UUID, x.DebitNoteNumber, NullIfEmpty(x.SroNumber), x.SupplierId,
            x.Doc?.Currency ?? BaseCurrencyFallback, x.Date, x.Date, x.DebitAmount, x.CarriedForwardAmount ?? x.DebitAmount,
            x.Doc?.ExchangeRate, true, "CARRIED_FORWARD")));

        var writeOffs = _db.DebtWriteOffs.IgnoreQueryFilters()
            .Where(w => w.OrganizationId == org && w.Status == "APPROVED" && w.ApprovedAt != null && w.ApprovedAt < end);
        if (partnerId is { } pw) writeOffs = writeOffs.Where(w => w.SupplierId == pw);

        var approved = await writeOffs.AsNoTracking()
            .Select(w => new { w.UUID, w.SupplierId, w.Amount, w.ApprovedAt, w.Reason })
            .ToListAsync(ct);

        result.AddRange(approved.Select(x => new OpenItemRow(
            OpenItemTypes.WriteOff, x.UUID, $"WO-{x.UUID.ToString()[..8]}", x.Reason, x.SupplierId, BaseCurrencyFallback,
            x.ApprovedAt!.Value, x.ApprovedAt!.Value, x.Amount, x.Amount, 1m, true, "APPLIED")));

        return result;
    }

    /// <summary>
    /// Marker for "the base currency" on payables documents that carry no currency of their own (write-offs; notes whose
    /// invoice is unknown). Replaced by the real purchase base code by the services (<see cref="WithBaseCode"/>).
    /// </summary>
    internal const string BaseCurrencyFallback = "";

    /// <summary>Replaces the <see cref="BaseCurrencyFallback"/> marker with the organization's base code.</summary>
    internal static List<OpenItemRow> WithBaseCode(List<OpenItemRow> rows, string baseCode)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].CurrencyCode.Length == 0) rows[i] = rows[i] with { CurrencyCode = baseCode };
        return rows;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
