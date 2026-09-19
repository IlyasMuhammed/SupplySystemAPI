using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Settlement;

/// <summary>
/// Accepting a carrier's bill, or querying it.
/// <para>
/// <b>Approving is the act that settles a liability.</b> It records what the company accepts it
/// owes — which is not always what was billed — and closes the accruals the bill covers, taking the
/// estimate off the books now that a real figure has replaced it.
/// </para>
/// <para>
/// <b>Where it stops.</b> Whether an approved bill then posts into Finance as a supplier invoice,
/// or is paid from here, is decision <b>G10</b>. Nothing in this service presumes an answer: it
/// records the approval and says plainly that payment is out of its hands.
/// </para>
/// </summary>
public interface IInvoiceSettlementService
{
    /// <summary>Where a bill stands, and what has been accepted.</summary>
    Task<CarrierInvoiceSettlementModel?> GetAsync(Guid invoiceUuid, CancellationToken ct = default);

    /// <summary>Accepts a bill for payment and closes the accruals it covers.</summary>
    Task<CarrierInvoiceSettlementModel?> ApproveAsync(
        Guid invoiceUuid, ApproveCarrierInvoiceRequest req, int userId, CancellationToken ct = default);

    /// <summary>Raises a query with the carrier. The accruals stay open — it is still owed.</summary>
    Task<CarrierInvoiceSettlementModel?> RaiseDisputeAsync(
        Guid invoiceUuid, RaiseDisputeRequest req, int userId, CancellationToken ct = default);

    /// <summary>Records how the query ended, and approves the bill at whatever was settled.</summary>
    Task<CarrierInvoiceSettlementModel?> ResolveDisputeAsync(
        Guid invoiceUuid, ResolveDisputeRequest req, int userId, CancellationToken ct = default);
}

internal sealed class InvoiceSettlementService : IInvoiceSettlementService
{
    private static readonly string Cancelled = LogisticsCode.Of(CarrierInvoiceStatus.Cancelled);
    private static readonly string Approved  = LogisticsCode.Of(CarrierInvoiceStatus.Approved);
    private static readonly string Disputed  = LogisticsCode.Of(CarrierInvoiceStatus.Disputed);

    private static readonly string Unmatched = LogisticsCode.Of(InvoiceLineMatchStatus.Unmatched);
    private static readonly string Ambiguous = LogisticsCode.Of(InvoiceLineMatchStatus.Ambiguous);

    /// <summary>What a posted freight payable is labelled as, and the key its idempotency hangs off.</summary>
    internal const string PostingSourceType = "CARRIER_INVOICE";

    /// <summary>
    /// Days from approval to the payable's due date when the carrier's bill did not state one.
    /// The same placeholder Finance's own GRN path uses, for the same reason: payment terms live on
    /// the supplier as a lookup reference this module cannot resolve, so AP corrects it.
    /// </summary>
    internal const int DefaultPaymentTermDays = 30;

    private readonly LogisticsDbContext     _db;
    private readonly ISupplierInvoicePoster _payables;

    public InvoiceSettlementService(LogisticsDbContext db, ISupplierInvoicePoster payables)
    {
        _db       = db;
        _payables = payables;
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    public async Task<CarrierInvoiceSettlementModel?> GetAsync(
        Guid invoiceUuid, CancellationToken ct = default)
    {
        var invoice = await _db.CarrierInvoices.AsNoTracking()
            .Include(i => i.Carrier)
            .FirstOrDefaultAsync(i => i.UUID == invoiceUuid && !i.IsDelete, ct);

        if (invoice is null) return null;

        var closed = await _db.FreightAccruals.AsNoTracking()
            .CountAsync(a => a.CarrierInvoiceId == invoice.Id && !a.IsDelete, ct);

        return ToModel(invoice, closed);
    }

    // ── Approving ─────────────────────────────────────────────────────────────

    public async Task<CarrierInvoiceSettlementModel?> ApproveAsync(
        Guid invoiceUuid, ApproveCarrierInvoiceRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var invoice = await LoadAsync(invoiceUuid, ct);
        if (invoice is null) return null;

        EnsureApprovable(invoice);

        var amount = req.Amount ?? invoice.TotalAmount;

        Validate(invoice, amount, req.Note, out var note);

        var closed = await ApproveInternalAsync(invoice, amount, note, userId, ct);

        await _db.SaveChangesAsync(ct);

        return ToModel(invoice, closed);
    }

    /// <summary>
    /// The rules that stop a bill being accepted on trust.
    /// </summary>
    private void EnsureApprovable(CarrierInvoice invoice)
    {
        if (invoice.Status == Cancelled)
            throw new ConflictException(
                $"Invoice '{invoice.InvoiceNumber}' was withdrawn. There is nothing to approve.");

        if (invoice.Status == Approved)
            throw new ConflictException(
                $"Invoice '{invoice.InvoiceNumber}' has already been approved for "
              + $"{invoice.Currency} {invoice.ApprovedAmount:N2}. Raise a credit claim with the "
              + "carrier rather than approving it twice.");

        // Approving a bill with lines nobody could explain is approving charges nobody checked —
        // which is the whole failure the matching queue exists to prevent.
        var loose = invoice.Lines.Count(l => l.MatchStatus == Unmatched || l.MatchStatus == Ambiguous);

        if (loose > 0)
            throw new ConflictException(
                $"{loose} line(s) on this bill are not tied to a movement. Approving it would accept "
              + "charges nobody has checked — match them or set them aside first.");
    }

    private static void Validate(
        CarrierInvoice invoice, decimal amount, string? requestedNote, out string? note)
    {
        if (amount <= 0m)
            throw new BadRequestException("Approving a bill for nothing is not approving it.");

        // Above the bill is not approval, it is invention — and it is the carrier's number that
        // would be queried if the two ever disagreed.
        if (amount > invoice.TotalAmount)
            throw new BadRequestException(
                $"This bill is for {invoice.Currency} {invoice.TotalAmount:N2} and "
              + $"{invoice.Currency} {amount:N2} is being approved. A bill cannot be approved for "
              + "more than the carrier asked for.");

        note = string.IsNullOrWhiteSpace(requestedNote) ? null : requestedNote.Trim();

        if (amount < invoice.TotalAmount && note is null)
            throw new BadRequestException(
                $"Only {invoice.Currency} {amount:N2} of a {invoice.Currency} "
              + $"{invoice.TotalAmount:N2} bill is being accepted. Say why — the carrier will ask, "
              + "and the difference is what it will ask about.");
    }

    /// <summary>
    /// Stamps the approval and closes the accruals this bill settles. Closing is what takes the
    /// estimate off the books: until now the liability was carried at what we guessed, and from
    /// here it is carried at what the carrier billed and we accepted.
    /// </summary>
    private async Task<int> ApproveInternalAsync(
        CarrierInvoice invoice, decimal amount, string? note, int userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        invoice.Status         = Approved;
        invoice.ApprovedAmount = amount;
        invoice.ApprovedAt     = now;
        invoice.ApprovedBy     = userId;
        invoice.ApprovalNote   = note;
        invoice.ModifiedBy     = userId;
        invoice.ModifiedDate   = now;

        var accruals = await _db.FreightAccruals
            .Where(a => a.CarrierInvoiceId == invoice.Id && !a.IsDelete)
            .ToListAsync(ct);

        foreach (var accrual in accruals)
        {
            accrual.Status        = LogisticsCode.Of(FreightAccrualStatus.Closed);
            accrual.ReleasedAt    = now;
            accrual.ReleaseReason = $"Settled by invoice {invoice.InvoiceNumber}.";
            accrual.ModifiedBy    = userId;
            accrual.ModifiedDate  = now;
        }

        await PostToFinanceAsync(invoice, amount, userId, now, ct);

        return accruals.Count;
    }

    /// <summary>
    /// Raises the payable in Finance (decision G10). What was, until this existed, the sentence
    /// "paying it is outside this module — nothing here has sent it anywhere".
    /// <para>
    /// <b>Posted at what was approved, never at what was billed.</b> Those differ whenever a query
    /// ended in a credit, and the approved figure is the one the company accepts it owes.
    /// </para>
    /// </summary>
    private async Task PostToFinanceAsync(
        CarrierInvoice invoice, decimal amount, int userId, DateTime now, CancellationToken ct)
    {
        var supplierId = invoice.Carrier?.SupplierId
            ?? throw new ConflictException(
                $"'{invoice.CarrierName ?? invoice.Carrier?.Name ?? "This carrier"}' is not linked "
              + "to a supplier, so an approved bill would reach no payables ledger and nobody would "
              + "be paid. Set the carrier's supplier first — it is a one-off for each carrier.");

        // Tax is not broken out on a carrier bill in this module, so the whole approved figure is
        // the subtotal. Stating a tax of zero we have not been told about would be worse.
        var posting = new SupplierInvoicePosting(
            SupplierId:        supplierId,
            SupplierInvoiceNo: invoice.InvoiceNumber,
            InvoiceDate:       invoice.InvoiceDate,
            DueDate:           invoice.DueDate ?? now.Date.AddDays(DefaultPaymentTermDays),
            Currency:          invoice.Currency,
            Subtotal:          amount,
            TaxAmount:         0m,
            SourceType:        PostingSourceType,
            SourceUuid:        invoice.UUID,
            Notes: amount < invoice.TotalAmount
                ? $"Freight. Carrier billed {invoice.Currency} {invoice.TotalAmount:N2}; "
                + $"{invoice.Currency} {amount:N2} accepted. {invoice.ApprovalNote}"
                : $"Freight, per carrier invoice {invoice.InvoiceNumber}.");

        var result = await _payables.PostAsync(posting, userId, ct);

        invoice.PostedInvoiceUuid   = result.InvoiceUuid;
        invoice.PostedInvoiceNumber = result.InvoiceNumber;
        invoice.PostedAt            = now;
    }

    // ── Querying ──────────────────────────────────────────────────────────────

    public async Task<CarrierInvoiceSettlementModel?> RaiseDisputeAsync(
        Guid invoiceUuid, RaiseDisputeRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var invoice = await LoadAsync(invoiceUuid, ct);
        if (invoice is null) return null;

        if (invoice.Status == Cancelled)
            throw new ConflictException(
                $"Invoice '{invoice.InvoiceNumber}' was withdrawn. There is nothing to query.");

        if (invoice.Status == Approved)
            throw new ConflictException(
                $"Invoice '{invoice.InvoiceNumber}' has already been approved. Raise a credit claim "
              + "with the carrier rather than re-opening it here.");

        var reason = Trim(req.Reason)
            ?? throw new BadRequestException(
                "Say what is being queried. A dispute nobody can restate is a dispute nobody wins.");

        if (req.ExpectedCreditAmount is { } credit)
        {
            if (credit <= 0m)
                throw new BadRequestException("An expected credit of nothing is not a credit.");

            if (credit > invoice.TotalAmount)
                throw new BadRequestException(
                    $"A credit of {invoice.Currency} {credit:N2} is more than the "
                  + $"{invoice.Currency} {invoice.TotalAmount:N2} that was billed.");
        }

        var now = DateTime.UtcNow;

        invoice.Status               = Disputed;
        invoice.DisputeRaisedAt      = now;
        invoice.DisputeReason        = reason;
        invoice.DisputeReference     = Trim(req.Reference);
        invoice.ExpectedCreditAmount = req.ExpectedCreditAmount;
        // Cleared, so re-querying a bill that was queried before does not read as already settled.
        invoice.DisputeResolvedAt    = null;
        invoice.DisputeOutcome       = null;
        invoice.DisputeResolutionNote = null;
        invoice.ModifiedBy           = userId;
        invoice.ModifiedDate         = now;

        await _db.SaveChangesAsync(ct);

        // The accruals stay open on purpose: a queried charge is still owed until somebody decides
        // it is not, and closing it here would take the liability off a month early.
        return ToModel(invoice, 0);
    }

    public async Task<CarrierInvoiceSettlementModel?> ResolveDisputeAsync(
        Guid invoiceUuid, ResolveDisputeRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var invoice = await LoadAsync(invoiceUuid, ct);
        if (invoice is null) return null;

        if (invoice.Status != Disputed)
            throw new ConflictException(
                $"Invoice '{invoice.InvoiceNumber}' is {invoice.Status}, not {Disputed}. Only a "
              + "query that was raised can be closed.");

        if (!LogisticsCode.TryParse<DisputeOutcome>(req.Outcome, out var outcome))
            throw new BadRequestException(
                $"'{req.Outcome}' is not an outcome. Valid values: "
              + $"{string.Join(", ", LogisticsCode.Codes<DisputeOutcome>())}.");

        // A credit changes what is owed, so it has to say to what. The other two outcomes mean the
        // bill stands as issued, and inventing a different figure for them would be a fiction.
        var amount = outcome == DisputeOutcome.CreditReceived
            ? req.ApprovedAmount ?? throw new BadRequestException(
                  "Say what the carrier credited it to. A credit that does not change the figure "
                + "is not a credit.")
            : invoice.TotalAmount;

        var note = Trim(req.Note);

        if (outcome == DisputeOutcome.WrittenOff && note is null)
            throw new BadRequestException(
                "Say why the difference is being let go. A query abandoned without a reason looks "
              + "identical to one nobody followed up.");

        Validate(invoice, amount, note ?? $"Query closed: {LogisticsCode.Of(outcome)}.", out var approvalNote);

        var now = DateTime.UtcNow;

        invoice.DisputeResolvedAt     = now;
        invoice.DisputeOutcome        = LogisticsCode.Of(outcome);
        invoice.DisputeResolutionNote = note;

        var closed = await ApproveInternalAsync(invoice, amount, approvalNote, userId, ct);

        await _db.SaveChangesAsync(ct);

        return ToModel(invoice, closed);
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private Task<CarrierInvoice?> LoadAsync(Guid uuid, CancellationToken ct) =>
        _db.CarrierInvoices
            .Include(i => i.Carrier)
            .Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.UUID == uuid && !i.IsDelete, ct);

    private static CarrierInvoiceSettlementModel ToModel(CarrierInvoice i, int accrualsClosed)
    {
        var model = new CarrierInvoiceSettlementModel
        {
            InvoiceUuid           = i.UUID,
            InvoiceNumber         = i.InvoiceNumber,
            CarrierName           = i.CarrierName ?? i.Carrier?.Name ?? string.Empty,
            Currency              = i.Currency,
            Status                = i.Status,
            BilledAmount          = i.TotalAmount,
            ApprovedAmount        = i.ApprovedAmount,
            NotAccepted           = i.ApprovedAmount is { } approved ? i.TotalAmount - approved : null,
            ApprovedAt            = i.ApprovedAt,
            ApprovalNote          = i.ApprovalNote,
            DisputeRaisedAt       = i.DisputeRaisedAt,
            DisputeReason         = i.DisputeReason,
            DisputeReference      = i.DisputeReference,
            ExpectedCreditAmount  = i.ExpectedCreditAmount,
            DisputeResolvedAt     = i.DisputeResolvedAt,
            DisputeOutcome        = i.DisputeOutcome,
            DisputeResolutionNote = i.DisputeResolutionNote,
            AccrualsClosed        = accrualsClosed,
            PostedInvoiceUuid     = i.PostedInvoiceUuid,
            PostedInvoiceNumber   = i.PostedInvoiceNumber,
            PostedAt              = i.PostedAt
        };

        // Where the money went, said explicitly rather than implied by silence — the same reason
        // the old "nothing here has sent it anywhere" warning existed before G10 answered it.
        if (i.Status == Approved && i.PostedInvoiceNumber is { } payable)
            model.Warnings.Add(
                $"Approved for {i.Currency} {i.ApprovedAmount:N2} and raised in Finance as "
              + $"{payable}. It is paid there, on the ordinary payment run — not from here.");

        // An approval that predates G10, or one whose posting was reversed in Finance.
        if (i.Status == Approved && i.PostedInvoiceNumber is null)
            model.Warnings.Add(
                $"Approved for {i.Currency} {i.ApprovedAmount:N2}, and no payable was raised for it. "
              + "Nobody will be paid until one is.");

        if (i.Status == Disputed)
            model.Warnings.Add(
                "This bill is queried and still owed. The accruals behind it stay open until the "
              + "query is closed one way or the other.");

        return model;
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
