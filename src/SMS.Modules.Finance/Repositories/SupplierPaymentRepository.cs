using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Services;

namespace SMS.Modules.Finance.Repositories;

/// <summary>
/// Supplier payments (SFM-004): drafted against approved invoices, approved, posted (PaidAmount and a ledger
/// credit), cancelled, or — a cheque — bounced.
/// <para>
/// <b>One change at a time.</b> Neither the invoices nor the payments have a concurrency token (adding one is a model
/// change), so every change here runs in one transaction (<see cref="InvoiceRowLocks.InTransactionAsync{T}"/>) and
/// takes its locks first, in the one platform-wide order: the invoices' rows (ascending Id,
/// <see cref="InvoiceRowLocks.LockAsync"/>), then the payment's own row, then — a purchase-return settlement — its
/// credit note's. Only then does it read what it checks, as committed now. Without that, two postings of 500 on one
/// invoice of 1000 both read PaidAmount 0 and both wrote 500; a cancellation could be written over a posting already
/// in the ledger; and a payment could be drafted on an invoice being reversed.
/// </para>
/// <para>
/// <b>Only an approved invoice is a payable</b>: only it is on the supplier ledger. A Pending, Matched or Variance
/// invoice cannot be paid (400 when drafting, 409 when approving or posting a payment that names one), and is not
/// outstanding, aged or payable in the reports. Cancelling and bouncing still work whatever the invoice's status.
/// </para>
/// <para>
/// Every change reaches only this organization's own payments and invoices — an explicit filter, so a super admin
/// (whom the tenant filter lets read every organization) cannot change another organization's.
/// </para>
/// </summary>
internal sealed class SupplierPaymentRepository : ISupplierPaymentRepository
{
    internal const string PurchaseReturnSettlement = "PURCHASE_RETURN_SETTLEMENT";

    private readonly FinanceDbContext       _fin;
    private readonly ISupplierLedgerService _ledger;
    private readonly INotificationService   _notif;
    private readonly IAttachmentService?    _attachments;

    public SupplierPaymentRepository(
        FinanceDbContext fin, ISupplierLedgerService ledger, INotificationService notif,
        IAttachmentService? attachments = null)
    {
        _fin         = fin;
        _ledger      = ledger;
        _notif       = notif;
        _attachments = attachments;
    }

    /// <summary>This organization's own payments — the only ones a change may touch.</summary>
    private IQueryable<SupplierPayment> OwnPayments
    {
        get
        {
            var organizationId = _fin.TenantContext.OrganizationId;
            return _fin.SupplierPayments.Where(p => p.OrganizationId == organizationId);
        }
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateSupplierPaymentRequest req, int createdBy)
    {
        if (req.TotalAmount <= 0)
            throw new BadRequestException("TotalAmount must be greater than zero.");

        ValidateMethodFields(req.PaymentMethod, req.BankAccount, req.ChequeNo, req.ChequeDate);

        var paymentType = string.IsNullOrWhiteSpace(req.PaymentType) ? "STANDARD" : req.PaymentType;
        if (paymentType == PurchaseReturnSettlement && req.CreditNoteUuid is null)
            throw new BadRequestException("CreditNoteUuid is required for PURCHASE_RETURN_SETTLEMENT payments.");

        if (req.Lines.Count > 0)
        {
            var linesSum = req.Lines.Sum(l => l.AllocatedAmount);
            if (linesSum != req.TotalAmount)
                throw new BadRequestException(
                    $"Sum of payment line allocations ({linesSum:N2}) must equal TotalAmount ({req.TotalAmount:N2}).");
        }

        return await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            // The invoices first: a reversal (or another payment) of one of them waits for this draft, or this
            // draft for it — and then sees what it did.
            await InvoiceRowLocks.LockAsync(_fin, req.Lines.Select(l => l.InvoiceUuid));

            if (paymentType == PurchaseReturnSettlement)
            {
                var note = await OwnCreditNotes().FirstOrDefaultAsync(c => c.UUID == req.CreditNoteUuid!.Value)
                    ?? throw new NotFoundException("CreditNote", req.CreditNoteUuid!.Value);
                if (SettlementProblem(note, req.SupplierId, req.TotalAmount) is { } problem)
                    throw new BadRequestException(problem);
            }

            var now = DateTime.UtcNow;
            var payment = new SupplierPayment
            {
                UUID          = req.PaymentUuid is { } pid && pid != Guid.Empty ? pid : Guid.NewGuid(),
                SupplierId    = req.SupplierId,
                SupplierName  = req.SupplierName,
                PaymentDate   = req.PaymentDate,
                PaymentMethod = req.PaymentMethod,
                TotalAmount   = req.TotalAmount,
                BankAccount   = req.BankAccount?.Trim(),
                ChequeNo      = req.ChequeNo?.Trim(),
                ChequeDate    = req.ChequeDate,
                Status        = "DRAFT",
                Notes         = req.Notes?.Trim(),
                PaymentType   = paymentType,
                CreditNoteUuid = req.CreditNoteUuid,
                CreatedBy     = createdBy,
                CreatedDate   = now
            };

            // Tracks amounts already reserved against each invoice by earlier lines in THIS same
            // request, so two lines targeting the same invoice can't jointly over-allocate it.
            var reservedInThisRequest = new Dictionary<Guid, decimal>();

            foreach (var lineReq in req.Lines)
            {
                // Read after the lock, so as committed now; another organization's invoice is not found.
                var invoice = await InvoiceRowLocks.Own(_fin).FirstOrDefaultAsync(i => i.UUID == lineReq.InvoiceUuid)
                    ?? throw new NotFoundException("Invoice", lineReq.InvoiceUuid);

                if (invoice.SupplierId != req.SupplierId)
                    throw new BadRequestException(
                        $"Invoice {invoice.InvoiceNumber} does not belong to the specified supplier.");

                // Only an approved invoice is owed; a reversed or rejected one never again (S-7).
                if (NotPayable(invoice) is { } why)
                    throw new BadRequestException(why);

                var outstanding    = Math.Max(0m, await InvoiceSettlement.AvailableAsync(_fin, invoice));
                var reservedSoFar  = reservedInThisRequest.GetValueOrDefault(invoice.UUID);
                var effectiveLimit = outstanding - reservedSoFar;

                if (lineReq.AllocatedAmount > effectiveLimit)
                    throw new BadRequestException(
                        $"Allocated amount ({lineReq.AllocatedAmount:N2}) for invoice {invoice.InvoiceNumber} " +
                        $"exceeds its outstanding amount ({effectiveLimit:N2}).");

                reservedInThisRequest[invoice.UUID] = reservedSoFar + lineReq.AllocatedAmount;

                payment.Lines.Add(new SupplierPaymentLine
                {
                    UUID                        = Guid.NewGuid(),
                    InvoiceUuid                 = invoice.UUID,
                    InvoiceNumber               = invoice.InvoiceNumber,
                    AllocatedAmount             = lineReq.AllocatedAmount,
                    OutstandingBeforeAllocation = outstanding,
                    Notes                       = lineReq.Notes?.Trim()
                });
            }

            // Last: the numbering lock is held to the commit, and nothing waits for an invoice while holding it.
            payment.PaymentNumber = await NextPaymentNumberAsync(now.Year);

            _fin.SupplierPayments.Add(payment);
            await _fin.SaveChangesAsync();
            return payment.UUID;
        });
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<SupplierPaymentListItemModel>> GetListAsync(SupplierPaymentFilter filter)
    {
        var q = _fin.SupplierPayments.AsQueryable();

        if (filter.SupplierId.HasValue)              q = q.Where(x => x.SupplierId == filter.SupplierId.Value);
        if (!string.IsNullOrWhiteSpace(filter.Status)) q = q.Where(x => x.Status == filter.Status);
        if (!string.IsNullOrWhiteSpace(filter.Method)) q = q.Where(x => x.PaymentMethod == filter.Method);
        if (filter.DateFrom.HasValue)                 q = q.Where(x => x.PaymentDate >= filter.DateFrom.Value);
        if (filter.DateTo.HasValue)                   q = q.Where(x => x.PaymentDate <= filter.DateTo.Value);
        if (filter.InvoiceUuid.HasValue)              q = q.Where(x => x.Lines.Any(l => l.InvoiceUuid == filter.InvoiceUuid.Value));

        var total    = await q.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var data = await q
            .OrderByDescending(x => x.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new SupplierPaymentListItemModel
            {
                UUID          = x.UUID,
                PaymentNumber = x.PaymentNumber,
                SupplierId    = x.SupplierId,
                SupplierName  = x.SupplierName,
                PaymentDate   = x.PaymentDate,
                PaymentMethod = x.PaymentMethod,
                TotalAmount   = x.TotalAmount,
                Status        = x.Status,
                PaymentType   = x.PaymentType,
                LineCount     = x.Lines.Count
            })
            .ToListAsync();

        if (_attachments is not null && data.Count > 0)
        {
            var counts = await _attachments.GetCountsByDocumentIdsAsync("SUPPLIER_PAYMENT", data.Select(d => d.UUID));
            foreach (var item in data)
                item.AttachmentCount = counts.GetValueOrDefault(item.UUID);
        }

        return new PaginatedResponse<SupplierPaymentListItemModel>
        {
            Data         = data,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    // ── Get by ID ─────────────────────────────────────────────────────────────

    public async Task<SupplierPaymentDetailModel?> GetByUuidAsync(Guid uuid)
    {
        var p = await _fin.SupplierPayments
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.UUID == uuid);

        if (p is null) return null;

        return new SupplierPaymentDetailModel
        {
            UUID          = p.UUID,
            TraceId       = p.TraceId,
            PaymentNumber = p.PaymentNumber,
            SupplierId    = p.SupplierId,
            SupplierName  = p.SupplierName,
            PaymentDate   = p.PaymentDate,
            PaymentMethod = p.PaymentMethod,
            TotalAmount   = p.TotalAmount,
            BankAccount   = p.BankAccount,
            ChequeNo      = p.ChequeNo,
            ChequeDate    = p.ChequeDate,
            Status        = p.Status,
            Notes         = p.Notes,
            CreatedBy     = p.CreatedBy,
            CreatedDate   = p.CreatedDate,
            ApprovedBy    = p.ApprovedBy,
            ApprovedAt    = p.ApprovedAt,
            PostedAt      = p.PostedAt,
            BouncedAt     = p.BouncedAt,
            PaymentType   = p.PaymentType,
            CreditNoteUuid = p.CreditNoteUuid,
            Lines = p.Lines.Select(l => new SupplierPaymentLineModel
            {
                Uuid                        = l.UUID,
                InvoiceUuid                 = l.InvoiceUuid,
                InvoiceNumber               = l.InvoiceNumber,
                AllocatedAmount             = l.AllocatedAmount,
                OutstandingBeforeAllocation = l.OutstandingBeforeAllocation,
                Notes                       = l.Notes
            }).ToList()
        };
    }

    // ── Approve ───────────────────────────────────────────────────────────────

    /// <summary>
    /// DRAFT → APPROVED. Refused (409) when one of its invoices is not (or no longer) approved: approving it would
    /// schedule a payment of something that is not a payable. False when this organization has no such payment.
    /// </summary>
    public async Task<bool> ApproveAsync(Guid uuid, int approvedBy) =>
        await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            var p = await LockPaymentAsync(uuid);
            if (p is null) return false;

            if (p.Status != "DRAFT")
                throw new UnprocessableEntityException(
                    $"Only DRAFT payments can be approved. Current status: {p.Status}.");

            await PayableInvoicesAsync(p);

            p.Status       = "APPROVED";
            p.ApprovedBy   = approvedBy;
            p.ApprovedAt   = DateTime.UtcNow;
            p.ModifiedBy   = approvedBy;
            p.ModifiedDate = DateTime.UtcNow;
            await _fin.SaveChangesAsync();
            return true;
        });

    // ── Cancel ────────────────────────────────────────────────────────────────

    /// <summary>
    /// DRAFT or APPROVED → CANCELLED, whatever its invoices' status. Under the same locks as a posting, so the two
    /// cannot interleave: a cancellation never lands on a payment that has just been posted, nor reports success for
    /// one a posting then goes on to post.
    /// </summary>
    public async Task<bool> CancelAsync(Guid uuid, int cancelledBy) =>
        await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            var p = await LockPaymentAsync(uuid);
            if (p is null) return false;

            if (p.Status == "POSTED")
                throw new UnprocessableEntityException("Cannot cancel a POSTED payment.");
            if (p.Status == "CANCELLED")
                throw new UnprocessableEntityException("Payment is already cancelled.");

            p.Status       = "CANCELLED";
            p.ModifiedBy   = cancelledBy;
            p.ModifiedDate = DateTime.UtcNow;
            await _fin.SaveChangesAsync();
            return true;
        });

    // ── Post ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// APPROVED → POSTED: each invoice's PaidAmount goes up by its line — on the invoice as committed now, under its
    /// lock — and the supplier ledger is credited the total, in one transaction. Refused (409) when one of its
    /// invoices is not (or no longer) approved.
    /// </summary>
    public async Task<bool> PostAsync(Guid uuid, int postedBy)
    {
        var overpaid = await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            var payment = await LockPaymentAsync(uuid);
            if (payment is null) return null;

            if (payment.Status != "APPROVED")
                throw new UnprocessableEntityException(
                    $"Only APPROVED payments can be posted. Current status: {payment.Status}.");

            // An invoice that is not approved — never was, or was reversed or rejected after this payment was
            // drafted — is owed nothing: no money moves to it.
            var invoices = await PayableInvoicesAsync(payment);

            // Track every mutation below WITHOUT saving — PostEntryAsync's own SaveChangesAsync
            // (called last) commits this payment, every allocated invoice, the advance-payment /
            // credit-note side effect, and the new ledger entry all in one atomic write. If it
            // throws, none of the tracked changes below have touched the database either.
            foreach (var line in payment.Lines)
            {
                var invoice = invoices[line.InvoiceUuid];
                invoice.PaidAmount   += line.AllocatedAmount;
                invoice.PaymentStatus = InvoicePaymentStatus.Derive(invoice.PaidAmount, invoice.TotalAmount);
                invoice.ModifiedDate  = DateTime.UtcNow;
            }

            if (payment.PaymentType == "ADVANCE_PAYMENT")
            {
                _fin.SupplierAdvancePayments.Add(new SupplierAdvancePayment
                {
                    UUID                = Guid.NewGuid(),
                    SupplierId          = payment.SupplierId,
                    SupplierPaymentUuid = payment.UUID,
                    OriginalAmount      = payment.TotalAmount,
                    AvailableBalance    = payment.TotalAmount,
                    CreatedDate         = DateTime.UtcNow
                });
            }

            if (payment.PaymentType == PurchaseReturnSettlement && payment.CreditNoteUuid is { } noteUuid)
            {
                // The note's own row last, and then its credit as committed now: two settlements drawing on one
                // note cannot both take what only one of them may.
                var cn = await LockCreditNoteAsync(noteUuid)
                    ?? throw new NotFoundException("CreditNote", noteUuid);
                if (SettlementProblem(cn, payment.SupplierId, payment.TotalAmount) is { } problem)
                    throw new ConflictException(problem);

                var remainingCredit = cn.CarriedForwardAmount ?? cn.CreditAmount;
                cn.CarriedForwardAmount = remainingCredit - payment.TotalAmount;
                cn.ModifiedDate         = DateTime.UtcNow;
            }

            payment.Status       = "POSTED";
            payment.PostedAt     = DateTime.UtcNow;
            payment.ModifiedBy   = postedBy;
            payment.ModifiedDate = DateTime.UtcNow;

            await _ledger.PostEntryAsync(
                payment.SupplierId, "PAYMENT_POSTED", "SupplierPayment", payment.UUID, payment.PaymentNumber,
                debitAmount: 0m, creditAmount: payment.TotalAmount,
                narration: $"Payment {payment.PaymentNumber} posted.", createdBy: postedBy,
                supplierName: payment.SupplierName);

            return invoices.Values.Where(i => i.PaymentStatus == InvoicePaymentStatus.Overpaid).ToList();
        });

        if (overpaid is null) return false;

        // Alerts fire only after the atomic save above has genuinely succeeded (and committed).
        foreach (var invoice in overpaid)
        {
            await _notif.TryCreateAsync(new NotificationRequest(
                UserId: invoice.CreatedBy, Type: "INVOICE_OVERPAID", Title: "Invoice Overpaid",
                Message: $"Invoice {invoice.InvoiceNumber} is now overpaid — paid {invoice.PaidAmount:N2} against a total of {invoice.TotalAmount:N2}.",
                Category: "Finance", EntityType: "Invoice", EntityUuid: invoice.UUID.ToString(),
                NavigationUrl: $"/portal/pages/finance/invoices/{invoice.UUID}",
                CreatedBy: postedBy));
        }

        return true;
    }

    // ── Bounce (CHEQUE only) ──────────────────────────────────────────────────

    /// <summary>
    /// A posted cheque that bounced: each invoice's PaidAmount goes back down by its line (as committed now, under
    /// its lock), and the supplier ledger is debited back — whatever the invoices' status.
    /// </summary>
    public async Task<bool> BounceAsync(Guid uuid, int bouncedBy) =>
        await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            var payment = await LockPaymentAsync(uuid);
            if (payment is null) return false;

            if (payment.PaymentMethod != "CHEQUE")
                throw new UnprocessableEntityException(
                    "Only CHEQUE payments can bounce — no bounce flow exists for other payment methods.");
            if (payment.Status != "POSTED")
                throw new UnprocessableEntityException(
                    $"Only POSTED payments can bounce. Current status: {payment.Status}.");

            var uuids    = payment.Lines.Select(l => l.InvoiceUuid).Distinct().ToList();
            var invoices = await InvoiceRowLocks.Own(_fin).Where(i => uuids.Contains(i.UUID)).ToDictionaryAsync(i => i.UUID);

            // Same atomic-write pattern as PostAsync: every invoice rollback below is tracked but
            // unsaved until PostEntryAsync's own SaveChangesAsync (called last) commits this
            // payment, every rolled-back invoice, and the reversing ledger entry together.
            foreach (var line in payment.Lines)
            {
                var invoice = invoices.GetValueOrDefault(line.InvoiceUuid)
                    ?? throw new NotFoundException("Invoice", line.InvoiceUuid);

                invoice.PaidAmount   -= line.AllocatedAmount;
                invoice.PaymentStatus = InvoicePaymentStatus.Derive(invoice.PaidAmount, invoice.TotalAmount);
                invoice.ModifiedDate  = DateTime.UtcNow;
            }

            payment.Status       = "BOUNCED";
            payment.BouncedAt    = DateTime.UtcNow;
            payment.ModifiedBy   = bouncedBy;
            payment.ModifiedDate = DateTime.UtcNow;

            await _ledger.PostEntryAsync(
                payment.SupplierId, "PAYMENT_BOUNCED", "SupplierPayment", payment.UUID, payment.PaymentNumber,
                debitAmount: payment.TotalAmount, creditAmount: 0m,
                narration: "Cheque bounced", createdBy: bouncedBy, supplierName: payment.SupplierName);

            return true;
        });

    // ── Locks ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes the locks a change to this payment needs, in the platform-wide order — its invoices' rows (ascending
    /// Id), then its own row — and returns it, with its lines, as committed now. Null when this organization has no
    /// such payment (another organization's is not found, a super admin's request included). Must run inside
    /// <see cref="InvoiceRowLocks.InTransactionAsync{T}"/>; on the in-memory provider it only reads.
    /// </summary>
    private async Task<SupplierPayment?> LockPaymentAsync(Guid uuid)
    {
        var organizationId = _fin.TenantContext.OrganizationId;

        // A payment's lines never change once it is drafted, so which invoices to lock can be read first.
        var invoiceUuids = await _fin.SupplierPaymentLines
            .Where(l => l.SupplierPayment.UUID == uuid && l.SupplierPayment.OrganizationId == organizationId)
            .Select(l => l.InvoiceUuid)
            .ToListAsync();
        await InvoiceRowLocks.LockAsync(_fin, invoiceUuids);

        if (_fin.Database.IsRelational()
            && await OwnPayments.Where(p => p.UUID == uuid).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, p => p.Status)) == 0)
            return null;

        // Forget any copy this context was tracking, so the checks below see what is committed now.
        foreach (var stale in _fin.ChangeTracker.Entries<SupplierPayment>().Where(e => e.Entity.UUID == uuid).ToList())
        {
            var id = stale.Entity.Id;
            foreach (var line in _fin.ChangeTracker.Entries<SupplierPaymentLine>().Where(e => e.Entity.SupplierPaymentId == id).ToList())
                line.State = EntityState.Detached;
            stale.State = EntityState.Detached;
        }

        return await OwnPayments.Include(p => p.Lines).FirstOrDefaultAsync(p => p.UUID == uuid);
    }

    /// <summary>This organization's own credit notes, not deleted.</summary>
    private IQueryable<CreditNote> OwnCreditNotes()
    {
        var organizationId = _fin.TenantContext.OrganizationId;
        return _fin.CreditNotes.Where(c => c.OrganizationId == organizationId && c.IsActive && !c.IsDelete);
    }

    /// <summary>The credit note's row, locked (after the payment's), then read as committed now. Null when it is not this organization's.</summary>
    private async Task<CreditNote?> LockCreditNoteAsync(Guid uuid)
    {
        if (_fin.Database.IsRelational()
            && await OwnCreditNotes().Where(c => c.UUID == uuid)
                   .ExecuteUpdateAsync(s => s.SetProperty(c => c.ApplicationStatus, c => c.ApplicationStatus)) == 0)
            return null;

        foreach (var stale in _fin.ChangeTracker.Entries<CreditNote>().Where(e => e.Entity.UUID == uuid).ToList())
            stale.State = EntityState.Detached;

        return await OwnCreditNotes().FirstOrDefaultAsync(c => c.UUID == uuid);
    }

    /// <summary>
    /// The payment's invoices, read under their locks — each this organization's own and approved, or the payment is
    /// refused (409): a payable is an approved invoice.
    /// </summary>
    private async Task<Dictionary<Guid, Invoice>> PayableInvoicesAsync(SupplierPayment payment)
    {
        var uuids    = payment.Lines.Select(l => l.InvoiceUuid).Distinct().ToList();
        var invoices = await InvoiceRowLocks.Own(_fin).Where(i => uuids.Contains(i.UUID)).ToDictionaryAsync(i => i.UUID);

        foreach (var uuid in uuids)
        {
            var invoice = invoices.GetValueOrDefault(uuid) ?? throw new NotFoundException("Invoice", uuid);
            if (NotPayable(invoice) is { } why)
                throw new ConflictException(IsVoid(invoice)
                    ? $"{why} Cancel this payment and record one without it."
                    : why);
        }

        return invoices;
    }

    /// <summary>
    /// A purchase-return settlement draws on a carried-forward credit note of the same supplier, for no more than it
    /// has left. Null when it may; otherwise why not.
    /// </summary>
    private static string? SettlementProblem(CreditNote note, Guid supplierId, decimal amount)
    {
        if (note.SupplierId != supplierId)
            return $"Credit note {note.CreditNoteNumber} is another supplier's; a settlement can draw only on this supplier's own credit.";
        if (note.ApplicationStatus != "CARRIED_FORWARD")
            return $"Credit note {note.CreditNoteNumber} is {note.ApplicationStatus}; only a carried-forward credit note can settle a payment.";

        var remaining = note.CarriedForwardAmount ?? note.CreditAmount;
        return amount > remaining
            ? $"The settlement ({amount:N2}) is more than credit note {note.CreditNoteNumber} has left ({remaining:N2})."
            : null;
    }

    // ── Outstanding invoices ──────────────────────────────────────────────────

    /// <summary>A reversed (S-7: its ledger debit was cancelled by INVOICE_REVERSED) or rejected (never booked) invoice.</summary>
    private static bool IsVoid(Invoice invoice) =>
        InvoiceMatchStatus.Is(invoice.MatchStatus, InvoiceMatchStatus.Reversed)
     || InvoiceMatchStatus.Is(invoice.MatchStatus, InvoiceMatchStatus.Rejected);

    /// <summary>
    /// Why nothing can be paid on the invoice, or null when it is a payable: only an approved invoice is — only it is
    /// on the supplier ledger. A reversed or rejected one never owes anything again; a Pending, Matched or Variance
    /// one has to be approved first.
    /// </summary>
    private static string? NotPayable(Invoice invoice) =>
        InvoiceMatchStatus.Is(invoice.MatchStatus, InvoiceMatchStatus.Approved) ? null
      : IsVoid(invoice) ? $"Invoice {invoice.InvoiceNumber} has been {invoice.MatchStatus.Trim().ToLowerInvariant()}, so nothing is owed on it."
      : $"Invoice {invoice.InvoiceNumber} is not approved yet; approve it before paying.";

    public async Task<List<OutstandingInvoiceModel>> GetOutstandingInvoicesAsync(Guid supplierId)
    {
        // Only an approved invoice is a payable: a Pending, Matched or Variance one is not on the ledger yet, a
        // reversed one's debit was cancelled (S-7) and a rejected one was never booked.
        var invoices = await _fin.Invoices
            .AsNoTracking()
            .Where(i => i.SupplierId == supplierId && !i.IsDelete
                     && i.PaymentStatus != "Paid" && i.PaymentStatus != InvoicePaymentStatus.FullyPaid
                     && i.MatchStatus == InvoiceMatchStatus.Approved)
            .ToListAsync();

        if (invoices.Count == 0) return [];

        var outstandingMap = await GetOutstandingAmountsAsync(invoices);

        return invoices
            .Select(i => new OutstandingInvoiceModel
            {
                InvoiceUuid       = i.UUID,
                InvoiceNumber     = i.InvoiceNumber,
                TotalAmount       = i.TotalAmount,
                OutstandingAmount = outstandingMap[i.UUID],
                PaymentStatus     = i.PaymentStatus,
                DueDate           = i.DueDate
            })
            .Where(m => m.OutstandingAmount > 0)
            .OrderBy(m => m.DueDate)
            .ToList();
    }

    // ── Aging (per-supplier) ──────────────────────────────────────────────────

    public async Task<SupplierAgingModel> GetSupplierAgingAsync(Guid supplierId)
    {
        var invoices = await _fin.Invoices
            .AsNoTracking()
            .Where(i => i.SupplierId == supplierId && !i.IsDelete
                     && i.PaymentStatus != "Paid" && i.PaymentStatus != InvoicePaymentStatus.FullyPaid
                     && i.MatchStatus == InvoiceMatchStatus.Approved)
            .ToListAsync();

        var supplierName = invoices.Count > 0
            ? invoices[0].SupplierName
            : await _fin.Invoices.AsNoTracking().Where(i => i.SupplierId == supplierId).Select(i => i.SupplierName).FirstOrDefaultAsync() ?? string.Empty;

        var bucketed = AgingBucketCalculator.BucketOrder.ToDictionary(b => b, b => new AgingBucket { BucketName = b });
        PopulateBuckets(invoices, bucketed);

        var buckets = AgingBucketCalculator.BucketOrder.Select(b => bucketed[b]).ToList();

        return new SupplierAgingModel
        {
            SupplierId   = supplierId,
            SupplierName = supplierName,
            Buckets      = buckets,
            GrandTotal   = buckets.Sum(b => b.Total)
        };
    }

    // ── Aging (cross-supplier) ────────────────────────────────────────────────

    public async Task<CrossSupplierAgingReport> GetCrossSupplierAgingAsync()
    {
        var invoices = await _fin.Invoices
            .AsNoTracking()
            .Where(i => !i.IsDelete && i.PaymentStatus != "Paid" && i.PaymentStatus != InvoicePaymentStatus.FullyPaid
                     && i.MatchStatus == InvoiceMatchStatus.Approved)
            .ToListAsync();

        if (invoices.Count == 0)
            return new CrossSupplierAgingReport();

        var today = DateTime.UtcNow.Date;
        var rows  = new Dictionary<Guid, SupplierAgingSummaryRow>();

        foreach (var inv in invoices)
        {
            // SFM-006: aging uses invoice_amount - paid_amount directly, not the broader
            // "outstanding for future allocation" figure used to validate new payment lines
            // (which also reserves against not-yet-posted DRAFT/APPROVED payments).
            var outstanding = Math.Max(0m, inv.TotalAmount - inv.PaidAmount);
            if (outstanding <= 0) continue;

            if (!rows.TryGetValue(inv.SupplierId, out var row))
            {
                row = new SupplierAgingSummaryRow { SupplierId = inv.SupplierId, SupplierName = inv.SupplierName };
                rows[inv.SupplierId] = row;
            }

            var bucket = AgingBucketCalculator.BucketFor(AgingBucketCalculator.DaysOverdue(inv.DueDate, today));
            switch (bucket)
            {
                case AgingBucketCalculator.Current:     row.Current       += outstanding; break;
                case AgingBucketCalculator.Days31To60:  row.Bucket31To60  += outstanding; break;
                case AgingBucketCalculator.Days61To90:  row.Bucket61To90  += outstanding; break;
                case AgingBucketCalculator.Days91To120: row.Bucket91To120 += outstanding; break;
                default:                                row.Bucket120Plus += outstanding; break;
            }
            row.GrandTotal += outstanding;
        }

        var supplierRows = rows.Values.OrderByDescending(r => r.GrandTotal).ToList();

        var grandTotalRow = new SupplierAgingSummaryRow
        {
            SupplierName  = "Grand Total",
            Current       = supplierRows.Sum(r => r.Current),
            Bucket31To60  = supplierRows.Sum(r => r.Bucket31To60),
            Bucket61To90  = supplierRows.Sum(r => r.Bucket61To90),
            Bucket91To120 = supplierRows.Sum(r => r.Bucket91To120),
            Bucket120Plus = supplierRows.Sum(r => r.Bucket120Plus),
            GrandTotal    = supplierRows.Sum(r => r.GrandTotal)
        };

        return new CrossSupplierAgingReport { Suppliers = supplierRows, GrandTotalRow = grandTotalRow };
    }

    private static void PopulateBuckets(List<Invoice> invoices, Dictionary<string, AgingBucket> bucketed)
    {
        var today = DateTime.UtcNow.Date;
        foreach (var inv in invoices)
        {
            // SFM-006: aging uses invoice_amount - paid_amount directly (see GetCrossSupplierAgingAsync).
            var outstanding = Math.Max(0m, inv.TotalAmount - inv.PaidAmount);
            if (outstanding <= 0) continue; // fully covered despite status not yet flipped to FULLY_PAID

            var days       = AgingBucketCalculator.DaysOverdue(inv.DueDate, today);
            var bucketName = AgingBucketCalculator.BucketFor(days);

            bucketed[bucketName].Total += outstanding;
            bucketed[bucketName].Invoices.Add(new AgingInvoiceItem
            {
                InvoiceUuid       = inv.UUID,
                InvoiceNumber     = inv.InvoiceNumber,
                DueDate           = inv.DueDate,
                DaysOverdue       = Math.Max(0, days),
                OutstandingAmount = outstanding
            });
        }
    }

    // ── Reports (SFM-007) ─────────────────────────────────────────────────────
    // Every query below is a pure AsNoTracking() read that projects straight into DTOs —
    // no entity is ever tracked, so nothing here can accidentally stage a write.

    public async Task<PaginatedResponse<PaymentRegisterItem>> GetPaymentRegisterAsync(PaymentRegisterFilter filter)
    {
        var q = _fin.SupplierPayments.AsNoTracking().AsQueryable();

        if (filter.SupplierId.HasValue)                     q = q.Where(p => p.SupplierId == filter.SupplierId.Value);
        if (!string.IsNullOrWhiteSpace(filter.Status))       q = q.Where(p => p.Status == filter.Status);
        if (!string.IsNullOrWhiteSpace(filter.Method))       q = q.Where(p => p.PaymentMethod == filter.Method);
        if (!string.IsNullOrWhiteSpace(filter.BankReference))
            q = q.Where(p => (p.BankAccount != null && p.BankAccount.Contains(filter.BankReference))
                           || (p.ChequeNo   != null && p.ChequeNo.Contains(filter.BankReference)));
        if (filter.DateFrom.HasValue)                        q = q.Where(p => p.PaymentDate >= filter.DateFrom.Value);
        if (filter.DateTo.HasValue)                          q = q.Where(p => p.PaymentDate <= filter.DateTo.Value);

        var total    = await q.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var data = await q
            .OrderByDescending(p => p.PaymentDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PaymentRegisterItem
            {
                Uuid          = p.UUID,
                PaymentNumber = p.PaymentNumber,
                SupplierId    = p.SupplierId,
                SupplierName  = p.SupplierName,
                PaymentDate   = p.PaymentDate,
                PaymentMethod = p.PaymentMethod,
                Status        = p.Status,
                TotalAmount   = p.TotalAmount,
                BankAccount   = p.BankAccount,
                ChequeNo      = p.ChequeNo
            })
            .ToListAsync();

        return new PaginatedResponse<PaymentRegisterItem>
        {
            Data         = data,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<PaginatedResponse<OutstandingPayablesSupplierGroup>> GetOutstandingPayablesAsync(OutstandingPayablesFilter filter)
    {
        var invoices = await _fin.Invoices
            .AsNoTracking()
            .Where(i => !i.IsDelete && i.PaymentStatus != "Paid" && i.PaymentStatus != InvoicePaymentStatus.FullyPaid
                     && i.MatchStatus == InvoiceMatchStatus.Approved)
            .ToListAsync();

        var today = DateTime.UtcNow.Date;

        var groups = invoices
            .GroupBy(i => new { i.SupplierId, i.SupplierName })
            .Select(g =>
            {
                var group = new OutstandingPayablesSupplierGroup
                {
                    SupplierId   = g.Key.SupplierId,
                    SupplierName = g.Key.SupplierName,
                    Invoices = g
                        .Select(i =>
                        {
                            var outstanding = Math.Max(0m, i.TotalAmount - i.PaidAmount);
                            var days        = AgingBucketCalculator.DaysOverdue(i.DueDate, today);
                            return new OutstandingPayableInvoiceItem
                            {
                                InvoiceUuid       = i.UUID,
                                InvoiceNumber     = i.InvoiceNumber,
                                TotalAmount       = i.TotalAmount,
                                OutstandingAmount = outstanding,
                                DueDate           = i.DueDate,
                                DaysOverdue       = Math.Max(0, days),
                                PaymentStatus     = i.PaymentStatus
                            };
                        })
                        .Where(x => x.OutstandingAmount > 0)
                        .OrderByDescending(x => x.DaysOverdue)
                        .ToList()
                };
                group.TotalOutstanding = group.Invoices.Sum(x => x.OutstandingAmount);
                return group;
            })
            .Where(g => g.Invoices.Count > 0)
            .OrderByDescending(g => g.TotalOutstanding)
            .ToList();

        var total    = groups.Count;
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var pageData = groups.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new PaginatedResponse<OutstandingPayablesSupplierGroup>
        {
            Data         = pageData,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<PaymentMethodBreakdownReport> GetPaymentMethodBreakdownAsync(PaymentMethodBreakdownFilter filter)
    {
        // Only POSTED payments represent money that has actually moved — DRAFT/APPROVED
        // payments haven't been executed yet, and CANCELLED/BOUNCED ones never settled.
        var q = _fin.SupplierPayments.AsNoTracking().Where(p => p.Status == "POSTED").AsQueryable();

        if (filter.DateFrom.HasValue) q = q.Where(p => p.PaymentDate >= filter.DateFrom.Value);
        if (filter.DateTo.HasValue)   q = q.Where(p => p.PaymentDate <= filter.DateTo.Value);

        var payments = await q.ToListAsync();

        var byMethod = payments
            .GroupBy(p => p.PaymentMethod)
            .Select(g => new PaymentMethodBreakdownItem { Method = g.Key, Count = g.Count(), TotalAmount = g.Sum(p => p.TotalAmount) })
            .OrderByDescending(x => x.TotalAmount)
            .ToList();

        return new PaymentMethodBreakdownReport
        {
            Methods    = byMethod,
            GrandTotal = payments.Sum(p => p.TotalAmount),
            TotalCount = payments.Count
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ValidateMethodFields(string method, string? bankAccount, string? chequeNo, DateTime? chequeDate)
    {
        switch (method)
        {
            case "BANK_TRANSFER":
            case "ONLINE_WIRE":
                if (string.IsNullOrWhiteSpace(bankAccount))
                    throw new BadRequestException($"BankAccount is required for {method} payments.");
                break;
            case "CHEQUE":
                if (string.IsNullOrWhiteSpace(chequeNo))
                    throw new BadRequestException("ChequeNo is required for CHEQUE payments.");
                if (chequeDate is null)
                    throw new BadRequestException("ChequeDate is required for CHEQUE payments.");
                break;
            case "CASH":
                break;
            default:
                throw new BadRequestException($"Invalid payment method: {method}.");
        }
    }

    /// <summary>
    /// What each invoice still has room for (<see cref="InvoiceSettlement.AvailableAsync"/>, never below zero), for
    /// many invoices at once.
    /// </summary>
    private async Task<Dictionary<Guid, decimal>> GetOutstandingAmountsAsync(List<Invoice> invoices)
    {
        var invoiceIds    = invoices.Select(i => i.Id).ToList();
        var invoiceUuids  = invoices.Select(i => i.UUID).ToList();

        var oldPayments = await _fin.Payments
            .Where(p => invoiceIds.Contains(p.InvoiceId) && !p.IsDelete && p.Status != "Reversed")
            .GroupBy(p => p.InvoiceId)
            .Select(g => new { InvoiceId = g.Key, Sum = g.Sum(p => p.AmountPaid) })
            .ToDictionaryAsync(x => x.InvoiceId, x => x.Sum);

        var unposted = await _fin.SupplierPaymentLines
            .Where(l => invoiceUuids.Contains(l.InvoiceUuid)
                     && (l.SupplierPayment.Status == "DRAFT" || l.SupplierPayment.Status == "APPROVED"))
            .GroupBy(l => l.InvoiceUuid)
            .Select(g => new { InvoiceUuid = g.Key, Sum = g.Sum(l => l.AllocatedAmount) })
            .ToDictionaryAsync(x => x.InvoiceUuid, x => x.Sum);

        var result = new Dictionary<Guid, decimal>();
        foreach (var inv in invoices)
        {
            var taken = inv.PaidAmount + oldPayments.GetValueOrDefault(inv.Id) + unposted.GetValueOrDefault(inv.UUID);
            result[inv.UUID] = Math.Max(0m, inv.TotalAmount - taken);
        }
        return result;
    }

    /// <summary>SPAY-YYYY-NNNNN: this organization's next free number this year, under the numbering lock.</summary>
    private async Task<string> NextPaymentNumberAsync(int year)
    {
        var organizationId = _fin.TenantContext.OrganizationId;
        var yearStart      = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd        = yearStart.AddYears(1);
        var own            = _fin.SupplierPayments.IgnoreQueryFilters().Where(p => p.OrganizationId == organizationId);

        await DocumentNumbers.SerializeAsync(_fin, "finance.supplier-payment-number", organizationId);
        var count = await own.CountAsync(p => p.CreatedDate >= yearStart && p.CreatedDate < yearEnd);
        return await DocumentNumbers.NextFreeAsync(count, n => $"SPAY-{year}-{n:D5}",
            number => own.AnyAsync(p => p.PaymentNumber == number));
    }
}

/// <summary>
/// What is still owed on a supplier invoice, for anything that would pay it or reduce it.
/// </summary>
internal static class InvoiceSettlement
{
    /// <summary>
    /// TotalAmount − PaidAmount (posted supplier payments) − the lines of supplier payments not yet posted (DRAFT or
    /// APPROVED: not cancelled, not bounced) − legacy payments not reversed. A credit or debit note may take the
    /// invoice down by no more than this, so it never ends below what is already paid or promised; a new payment line
    /// may allocate no more than this. It can be negative for old data that was over-allocated.
    /// </summary>
    internal static async Task<decimal> AvailableAsync(FinanceDbContext db, Invoice invoice)
    {
        var unposted = await db.SupplierPaymentLines
            .Where(l => l.InvoiceUuid == invoice.UUID
                     && (l.SupplierPayment.Status == "DRAFT" || l.SupplierPayment.Status == "APPROVED"))
            .SumAsync(l => (decimal?)l.AllocatedAmount) ?? 0m;

        var legacy = await db.Payments
            .Where(p => p.InvoiceId == invoice.Id && !p.IsDelete && p.Status != "Reversed")
            .SumAsync(p => (decimal?)p.AmountPaid) ?? 0m;

        return invoice.TotalAmount - invoice.PaidAmount - unposted - legacy;
    }
}

/// <summary>
/// The "count + 1" numbers of Finance's documents (SPAY-, CN-, DN-) are unique per organization (a unique index). Two
/// creations at once both counted the same rows and took the same number; the second then failed on the index — and,
/// for a note, the ledger's own retry loop retried that same duplicate until the request failed. Each kind's
/// numbering in an organization is now serialized: an exclusive application lock owned by the transaction
/// (<c>sp_getapplock</c>), so the next creation counts once the previous one has committed. It is taken last in a
/// transaction — after any invoice lock — and held to the commit.
/// </summary>
internal static class DocumentNumbers
{
    /// <summary>Waits for, then holds until commit, the numbering lock of this kind of document in this organization. A no-op outside a SQL Server transaction (the in-memory provider).</summary>
    internal static async Task SerializeAsync(DbContext db, string kind, Guid organizationId)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is null) return;

        var resource = $"{kind}:{organizationId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 20000;
            IF @result < 0 THROW 51000, 'Another document is being numbered; try again.', 1;
            """);
    }

    /// <summary>count + 1, or the first number after it that is not taken (a gap, or old data numbered otherwise).</summary>
    internal static async Task<string> NextFreeAsync(int count, Func<int, string> format, Func<string, Task<bool>> taken)
    {
        var n = count + 1;
        while (await taken(format(n))) n++;
        return format(n);
    }
}
