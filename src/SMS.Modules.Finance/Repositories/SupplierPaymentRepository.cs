using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Repositories;

internal sealed class SupplierPaymentRepository : ISupplierPaymentRepository
{
    private readonly FinanceDbContext       _fin;
    private readonly ISupplierLedgerService _ledger;
    private readonly INotificationService   _notif;

    public SupplierPaymentRepository(FinanceDbContext fin, ISupplierLedgerService ledger, INotificationService notif)
    {
        _fin    = fin;
        _ledger = ledger;
        _notif  = notif;
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateSupplierPaymentRequest req, int createdBy)
    {
        if (req.TotalAmount <= 0)
            throw new BadRequestException("TotalAmount must be greater than zero.");

        ValidateMethodFields(req.PaymentMethod, req.BankAccount, req.ChequeNo, req.ChequeDate);

        var paymentType = string.IsNullOrWhiteSpace(req.PaymentType) ? "STANDARD" : req.PaymentType;
        if (paymentType == "PURCHASE_RETURN_SETTLEMENT" && req.CreditNoteUuid is null)
            throw new BadRequestException("CreditNoteUuid is required for PURCHASE_RETURN_SETTLEMENT payments.");

        if (req.Lines.Count > 0)
        {
            var linesSum = req.Lines.Sum(l => l.AllocatedAmount);
            if (linesSum != req.TotalAmount)
                throw new BadRequestException(
                    $"Sum of payment line allocations ({linesSum:N2}) must equal TotalAmount ({req.TotalAmount:N2}).");
        }

        var now           = DateTime.UtcNow;
        var paymentNumber = await GenerateSupplierPaymentNumberAsync(now.Year);

        var payment = new SupplierPayment
        {
            UUID          = Guid.NewGuid(),
            PaymentNumber = paymentNumber,
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
            var invoice = await _fin.Invoices
                .FirstOrDefaultAsync(i => i.UUID == lineReq.InvoiceUuid && !i.IsDelete)
                ?? throw new NotFoundException("Invoice", lineReq.InvoiceUuid);

            if (invoice.SupplierId != req.SupplierId)
                throw new BadRequestException(
                    $"Invoice {invoice.InvoiceNumber} does not belong to the specified supplier.");

            var outstanding    = await GetOutstandingAmountAsync(invoice);
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

        _fin.SupplierPayments.Add(payment);
        await _fin.SaveChangesAsync();
        return payment.UUID;
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

    public async Task<bool> ApproveAsync(Guid uuid, int approvedBy)
    {
        var p = await _fin.SupplierPayments.FirstOrDefaultAsync(x => x.UUID == uuid);
        if (p is null) return false;

        if (p.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT payments can be approved. Current status: {p.Status}.");

        p.Status       = "APPROVED";
        p.ApprovedBy   = approvedBy;
        p.ApprovedAt   = DateTime.UtcNow;
        p.ModifiedBy   = approvedBy;
        p.ModifiedDate = DateTime.UtcNow;
        await _fin.SaveChangesAsync();
        return true;
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    public async Task<bool> CancelAsync(Guid uuid, int cancelledBy)
    {
        var p = await _fin.SupplierPayments.FirstOrDefaultAsync(x => x.UUID == uuid);
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
    }

    // ── Post ──────────────────────────────────────────────────────────────────

    public async Task<bool> PostAsync(Guid uuid, int postedBy)
    {
        var payment = await _fin.SupplierPayments
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.UUID == uuid);
        if (payment is null) return false;

        if (payment.Status != "APPROVED")
            throw new UnprocessableEntityException(
                $"Only APPROVED payments can be posted. Current status: {payment.Status}.");

        // Track every mutation below WITHOUT saving — PostEntryAsync's own SaveChangesAsync
        // (called last) commits this payment, every allocated invoice, the advance-payment /
        // credit-note side effect, and the new ledger entry all in one atomic write. If it
        // throws, none of the tracked changes below have touched the database either.
        var overpaidInvoices = new List<Invoice>();

        foreach (var line in payment.Lines)
        {
            var invoice = await _fin.Invoices.FirstOrDefaultAsync(i => i.UUID == line.InvoiceUuid)
                ?? throw new NotFoundException("Invoice", line.InvoiceUuid);

            invoice.PaidAmount   += line.AllocatedAmount;
            invoice.PaymentStatus = InvoicePaymentStatus.Derive(invoice.PaidAmount, invoice.TotalAmount);
            invoice.ModifiedDate  = DateTime.UtcNow;

            if (invoice.PaymentStatus == InvoicePaymentStatus.Overpaid)
                overpaidInvoices.Add(invoice);
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

        if (payment.PaymentType == "PURCHASE_RETURN_SETTLEMENT" && payment.CreditNoteUuid.HasValue)
        {
            var cn = await _fin.CreditNotes.FirstOrDefaultAsync(c => c.UUID == payment.CreditNoteUuid.Value)
                ?? throw new NotFoundException("CreditNote", payment.CreditNoteUuid.Value);

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
            narration: $"Payment {payment.PaymentNumber} posted.", createdBy: postedBy);

        // Alerts fire only after the atomic save above has genuinely succeeded.
        foreach (var invoice in overpaidInvoices)
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

    public async Task<bool> BounceAsync(Guid uuid, int bouncedBy)
    {
        var payment = await _fin.SupplierPayments
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.UUID == uuid);
        if (payment is null) return false;

        if (payment.PaymentMethod != "CHEQUE")
            throw new UnprocessableEntityException(
                "Only CHEQUE payments can bounce — no bounce flow exists for other payment methods.");
        if (payment.Status != "POSTED")
            throw new UnprocessableEntityException(
                $"Only POSTED payments can bounce. Current status: {payment.Status}.");

        // Same atomic-write pattern as PostAsync: every invoice rollback below is tracked but
        // unsaved until PostEntryAsync's own SaveChangesAsync (called last) commits this
        // payment, every rolled-back invoice, and the reversing ledger entry together.
        foreach (var line in payment.Lines)
        {
            var invoice = await _fin.Invoices.FirstOrDefaultAsync(i => i.UUID == line.InvoiceUuid)
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
            narration: "Cheque bounced", createdBy: bouncedBy);

        return true;
    }

    // ── Outstanding invoices ──────────────────────────────────────────────────

    public async Task<List<OutstandingInvoiceModel>> GetOutstandingInvoicesAsync(Guid supplierId)
    {
        var invoices = await _fin.Invoices
            .AsNoTracking()
            .Where(i => i.SupplierId == supplierId && !i.IsDelete
                     && i.PaymentStatus != "Paid" && i.PaymentStatus != InvoicePaymentStatus.FullyPaid)
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
                     && i.PaymentStatus != "Paid" && i.PaymentStatus != InvoicePaymentStatus.FullyPaid)
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
            .Where(i => !i.IsDelete && i.PaymentStatus != "Paid" && i.PaymentStatus != InvoicePaymentStatus.FullyPaid)
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
            .Where(i => !i.IsDelete && i.PaymentStatus != "Paid" && i.PaymentStatus != InvoicePaymentStatus.FullyPaid)
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

    private async Task<decimal> GetOutstandingAmountAsync(Invoice invoice)
    {
        var oldPaid = await _fin.Payments
            .Where(p => p.InvoiceId == invoice.Id && !p.IsDelete && p.Status != "Reversed")
            .SumAsync(p => (decimal?)p.AmountPaid) ?? 0m;

        var newAllocated = await _fin.SupplierPaymentLines
            .Where(l => l.InvoiceUuid == invoice.UUID && l.SupplierPayment.Status != "CANCELLED")
            .SumAsync(l => (decimal?)l.AllocatedAmount) ?? 0m;

        return Math.Max(0m, invoice.TotalAmount - oldPaid - newAllocated);
    }

    private async Task<Dictionary<Guid, decimal>> GetOutstandingAmountsAsync(List<Invoice> invoices)
    {
        var invoiceIds    = invoices.Select(i => i.Id).ToList();
        var invoiceUuids  = invoices.Select(i => i.UUID).ToList();

        var oldPayments = await _fin.Payments
            .Where(p => invoiceIds.Contains(p.InvoiceId) && !p.IsDelete && p.Status != "Reversed")
            .GroupBy(p => p.InvoiceId)
            .Select(g => new { InvoiceId = g.Key, Sum = g.Sum(p => p.AmountPaid) })
            .ToDictionaryAsync(x => x.InvoiceId, x => x.Sum);

        var newAllocations = await _fin.SupplierPaymentLines
            .Where(l => invoiceUuids.Contains(l.InvoiceUuid) && l.SupplierPayment.Status != "CANCELLED")
            .GroupBy(l => l.InvoiceUuid)
            .Select(g => new { InvoiceUuid = g.Key, Sum = g.Sum(l => l.AllocatedAmount) })
            .ToDictionaryAsync(x => x.InvoiceUuid, x => x.Sum);

        var result = new Dictionary<Guid, decimal>();
        foreach (var inv in invoices)
        {
            var paid = oldPayments.GetValueOrDefault(inv.Id) + newAllocations.GetValueOrDefault(inv.UUID);
            result[inv.UUID] = Math.Max(0m, inv.TotalAmount - paid);
        }
        return result;
    }

    private async Task<string> GenerateSupplierPaymentNumberAsync(int year)
    {
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = yearStart.AddYears(1);
        var count     = await _fin.SupplierPayments
            .CountAsync(p => p.CreatedDate >= yearStart && p.CreatedDate < yearEnd);
        return $"SPAY-{year}-{(count + 1):D5}";
    }
}
