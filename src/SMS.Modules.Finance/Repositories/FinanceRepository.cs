using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Repositories;

internal sealed class InvoiceRepository : IInvoiceRepository
{
    private readonly FinanceDbContext      _db;
    private readonly DemandDbContext       _demand;
    private readonly WarehouseDbContext    _warehouse;
    private readonly ISupplierLedgerService _ledger;

    public InvoiceRepository(
        FinanceDbContext db, DemandDbContext demand, WarehouseDbContext warehouse, ISupplierLedgerService ledger)
    {
        _db        = db;
        _demand    = demand;
        _warehouse = warehouse;
        _ledger    = ledger;
    }

    public async Task<Guid> CreateAsync(CreateInvoiceRequest req, int createdBy)
    {
        var po = await _demand.PurchaseOrders
            .FirstOrDefaultAsync(p => p.UUID == req.PoUuid && !p.IsDelete)
            ?? throw new NotFoundException("PurchaseOrder", req.PoUuid);

        // When lines are provided, compute subtotal from them
        var subtotal = req.Lines?.Count > 0
            ? req.Lines.Sum(l => l.QtyInvoiced * l.UnitPrice)
            : req.Subtotal;

        var totalAmount = subtotal + req.TaxAmount;

        if (totalAmount <= 0)
            throw new BadRequestException("Invoice total must be greater than zero.");

        // 3-way matching values
        var matchedPoValue  = po.TotalAmount;
        var matchedGrnValue = 0m;

        if (req.GrnUuid.HasValue)
        {
            var grn = await _warehouse.Grns
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.UUID == req.GrnUuid && !g.IsDelete);

            if (grn is not null)
                matchedGrnValue = grn.Lines.Sum(l => l.QtyAccepted * (l.UnitCost ?? 0));
        }

        var variance    = totalAmount - matchedPoValue;
        var matchStatus = DetermineMatchStatus(totalAmount, matchedPoValue, matchedGrnValue, req.GrnUuid.HasValue);

        var now           = DateTime.UtcNow;
        var invoiceNumber = await GenerateInvoiceNumberAsync(now.Year);

        var e = new Invoice
        {
            UUID              = Guid.NewGuid(),
            TraceId           = po.TraceId,
            InvoiceNumber     = invoiceNumber,
            SupplierInvoiceNo = req.SupplierInvoiceNo?.Trim(),
            SupplierId        = req.SupplierId,
            SupplierName      = po.SupplierName,
            PoUuid            = po.UUID,
            PoNumber          = po.PoNumber,
            GrnUuid           = req.GrnUuid,
            InvoiceDate       = req.InvoiceDate,
            ReceivedDate      = req.ReceivedDate,
            DueDate           = req.DueDate,
            Currency          = req.Currency,
            Subtotal          = subtotal,
            TaxAmount         = req.TaxAmount,
            TotalAmount       = totalAmount,
            MatchedPoValue    = matchedPoValue,
            MatchedGrnValue   = matchedGrnValue,
            VarianceAmount    = variance,
            MatchStatus       = matchStatus,
            PaymentStatus     = "Unpaid",
            PaymentMethod     = req.PaymentMethod?.Trim(),
            Notes             = req.Notes?.Trim(),
            AttachmentUrl     = req.AttachmentUrl?.Trim(),
            IsActive          = true,
            CreatedBy         = createdBy,
            CreatedDate       = now
        };

        if (req.GrnUuid.HasValue)
        {
            var grn = await _warehouse.Grns.FirstOrDefaultAsync(g => g.UUID == req.GrnUuid);
            e.GrnNumber = grn?.GrnNumber;
        }

        // Persist invoice lines if provided
        if (req.Lines?.Count > 0)
        {
            int lineNo = 1;
            foreach (var lr in req.Lines)
            {
                var lineTotal = lr.QtyInvoiced * lr.UnitPrice;
                e.Lines.Add(new InvoiceLine
                {
                    UUID            = Guid.NewGuid(),
                    GrnLineUuid     = lr.GrnLineUuid,
                    PoLineUuid      = lr.PoLineUuid,
                    LineNo          = lineNo++,
                    ItemDescription = lr.ItemDescription.Trim(),
                    UnitOfMeasure   = lr.UnitOfMeasure?.Trim(),
                    QtyInvoiced     = lr.QtyInvoiced,
                    UnitPrice       = lr.UnitPrice,
                    LineTotal       = lineTotal
                });
            }
        }

        _db.Invoices.Add(e);
        await _db.SaveChangesAsync();
        return e.UUID;
    }

    public async Task<PaginatedResponse<InvoiceListItemModel>> GetListAsync(InvoiceFilter filter)
    {
        var q = _db.Invoices.Where(x => !x.IsDelete).AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.MatchStatus))
            q = q.Where(x => x.MatchStatus == filter.MatchStatus);
        if (!string.IsNullOrWhiteSpace(filter.PaymentStatus))
            q = q.Where(x => x.PaymentStatus == filter.PaymentStatus);
        if (filter.SupplierId.HasValue)
            q = q.Where(x => x.SupplierId == filter.SupplierId);
        if (filter.DateFrom.HasValue)
            q = q.Where(x => x.InvoiceDate >= filter.DateFrom);
        if (filter.DateTo.HasValue)
            q = q.Where(x => x.InvoiceDate <= filter.DateTo);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim().ToLower();
            q = q.Where(x => x.InvoiceNumber.ToLower().Contains(s)
                           || x.SupplierName.ToLower().Contains(s)
                           || (x.SupplierInvoiceNo != null && x.SupplierInvoiceNo.ToLower().Contains(s)));
        }

        var total = await q.CountAsync();
        var data  = await q
            .OrderByDescending(x => x.CreatedDate)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(x => new InvoiceListItemModel
            {
                UUID              = x.UUID,
                TraceId           = x.TraceId,
                InvoiceNumber     = x.InvoiceNumber,
                SupplierInvoiceNo = x.SupplierInvoiceNo,
                SupplierName      = x.SupplierName,
                PoNumber          = x.PoNumber,
                GrnNumber         = x.GrnNumber,
                InvoiceDate       = x.InvoiceDate,
                DueDate           = x.DueDate,
                TotalAmount       = x.TotalAmount,
                Currency          = x.Currency,
                MatchStatus       = x.MatchStatus,
                PaymentStatus     = x.PaymentStatus
            })
            .ToListAsync();

        return new PaginatedResponse<InvoiceListItemModel>
        {
            Data         = data,
            TotalRecords = total,
            Page         = filter.Page,
            PageSize     = filter.PageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)filter.PageSize)
        };
    }

    public async Task<InvoiceDetailModel?> GetByUuidAsync(Guid uuid)
    {
        var inv = await _db.Invoices
            .Include(x => x.Lines.OrderBy(l => l.LineNo))
            .Include(x => x.Payments)
            .Where(x => x.UUID == uuid && !x.IsDelete)
            .FirstOrDefaultAsync();

        if (inv is null) return null;

        return new InvoiceDetailModel
        {
            UUID              = inv.UUID,
            TraceId           = inv.TraceId,
            InvoiceNumber     = inv.InvoiceNumber,
            SupplierInvoiceNo = inv.SupplierInvoiceNo,
            SupplierId        = inv.SupplierId,
            SupplierName      = inv.SupplierName,
            PoUuid            = inv.PoUuid,
            PoNumber          = inv.PoNumber,
            GrnUuid           = inv.GrnUuid,
            GrnNumber         = inv.GrnNumber,
            InvoiceDate       = inv.InvoiceDate,
            ReceivedDate      = inv.ReceivedDate,
            DueDate           = inv.DueDate,
            Currency          = inv.Currency,
            Subtotal          = inv.Subtotal,
            TaxAmount         = inv.TaxAmount,
            TotalAmount       = inv.TotalAmount,
            MatchedPoValue    = inv.MatchedPoValue,
            MatchedGrnValue   = inv.MatchedGrnValue,
            VarianceAmount    = inv.VarianceAmount,
            MatchStatus       = inv.MatchStatus,
            PaymentStatus     = inv.PaymentStatus,
            PaidAmount        = inv.PaidAmount,
            PaymentMethod     = inv.PaymentMethod,
            ApprovedBy        = inv.ApprovedBy,
            ApprovedAt        = inv.ApprovedAt,
            Notes             = inv.Notes,
            AttachmentUrl     = inv.AttachmentUrl,
            CreatedBy         = inv.CreatedBy,
            CreatedDate       = inv.CreatedDate,
            Lines = inv.Lines.Select(l => new InvoiceLineModel
            {
                UUID            = l.UUID,
                GrnLineUuid     = l.GrnLineUuid,
                PoLineUuid      = l.PoLineUuid,
                LineNo          = l.LineNo,
                ItemDescription = l.ItemDescription,
                UnitOfMeasure   = l.UnitOfMeasure,
                QtyInvoiced     = l.QtyInvoiced,
                UnitPrice       = l.UnitPrice,
                LineTotal       = l.LineTotal
            }).ToList(),
            Payments          = inv.Payments
                .Where(p => !p.IsDelete)
                .Select(p => new PaymentListItemModel
                {
                    UUID          = p.UUID,
                    PaymentNumber = p.PaymentNumber,
                    InvoiceNumber = inv.InvoiceNumber,
                    SupplierName  = p.SupplierName,
                    PaymentDate   = p.PaymentDate,
                    AmountPaid    = p.AmountPaid,
                    PaymentMethod = p.PaymentMethod,
                    Status        = p.Status
                }).ToList()
        };
    }

    public async Task<bool> PatchAsync(Guid uuid, PatchInvoiceRequest req, int modifiedBy)
    {
        var e = await _db.Invoices.FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete);
        if (e is null) return false;

        if (req.SupplierInvoiceNo is not null) e.SupplierInvoiceNo = req.SupplierInvoiceNo.Trim();
        if (req.DueDate           is not null) e.DueDate           = req.DueDate.Value;
        if (req.PaymentMethod     is not null) e.PaymentMethod     = req.PaymentMethod.Trim();
        if (req.TaxAmount         is not null)
        {
            e.TaxAmount      = req.TaxAmount.Value;
            e.TotalAmount    = e.Subtotal + req.TaxAmount.Value;
            e.VarianceAmount = e.TotalAmount - e.MatchedPoValue;
            e.MatchStatus    = DetermineMatchStatus(e.TotalAmount, e.MatchedPoValue, e.MatchedGrnValue, e.GrnUuid.HasValue);
        }
        if (req.MatchStatus       is not null) e.MatchStatus       = req.MatchStatus;
        if (req.PaymentStatus     is not null) e.PaymentStatus     = req.PaymentStatus;
        if (req.Notes             is not null) e.Notes             = req.Notes.Trim();
        if (req.AttachmentUrl     is not null) e.AttachmentUrl     = req.AttachmentUrl.Trim();

        e.ModifiedBy   = modifiedBy;
        e.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ApproveAsync(Guid uuid, string? notes, int approvedBy)
    {
        var inv = await _db.Invoices
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete);
        if (inv is null) return false;

        inv.MatchStatus  = "Approved";
        inv.ApprovedBy   = approvedBy;
        inv.ApprovedAt   = DateTime.UtcNow;
        if (notes is not null) inv.Notes = notes.Trim();
        inv.ModifiedBy   = approvedBy;
        inv.ModifiedDate = DateTime.UtcNow;

        // SFM-002: the ledger debit is posted in the SAME SaveChangesAsync as the invoice
        // approval itself (PostEntryAsync performs that save) — if it throws, none of the
        // invoice's tracked changes above have been persisted either, so the whole approval
        // rolls back atomically. Do not call _db.SaveChangesAsync() separately here.
        await _ledger.PostEntryAsync(
            inv.SupplierId, "INVOICE_APPROVED", "Invoice", inv.UUID, inv.InvoiceNumber,
            debitAmount: inv.TotalAmount, creditAmount: 0m,
            narration: $"Invoice {inv.InvoiceNumber} approved.", createdBy: approvedBy);

        // Update PO line QtyInvoiced — prefer invoice lines (precise), fall back to GRN lines
        var po = await _demand.PurchaseOrders
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.UUID == inv.PoUuid && !p.IsDelete);

        if (po is not null)
        {
            if (inv.Lines.Count > 0)
            {
                // Line-level invoicing: use exact QtyInvoiced per PO line
                foreach (var invLine in inv.Lines)
                {
                    var poLine = po.Lines.FirstOrDefault(l => l.UUID == invLine.PoLineUuid);
                    if (poLine is not null)
                        poLine.QtyInvoiced += invLine.QtyInvoiced;
                }
            }
            else if (inv.GrnUuid.HasValue)
            {
                // Fallback: no invoice lines — derive from GRN accepted quantities
                var grn = await _warehouse.Grns
                    .Include(g => g.Lines)
                    .FirstOrDefaultAsync(g => g.UUID == inv.GrnUuid && !g.IsDelete);

                if (grn is not null)
                {
                    foreach (var grnLine in grn.Lines)
                    {
                        var poLine = po.Lines.FirstOrDefault(l => l.UUID == grnLine.PoLineUuid);
                        if (poLine is not null)
                            poLine.QtyInvoiced += grnLine.QtyAccepted;
                    }
                }
            }

            // Recompute PO status (invoice progress takes priority over receipt)
            var allInvoiced = po.Lines.All(l => l.QtyInvoiced >= l.Quantity);
            var anyInvoiced = po.Lines.Any(l => l.QtyInvoiced > 0);
            var allReceived = po.Lines.All(l => l.QtyReceived >= l.Quantity);

            po.Status = allInvoiced ? "CLOSED"
                : anyInvoiced ? "PARTIALLY_INVOICED"
                : allReceived ? "RECEIVED"
                : "PARTIALLY_RECEIVED";

            po.ModifiedBy   = approvedBy;
            po.ModifiedDate = DateTime.UtcNow;
            await _demand.SaveChangesAsync();
        }

        return true;
    }

    public async Task<bool> RejectAsync(Guid uuid, string reason, int rejectedBy)
    {
        var e = await _db.Invoices.FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete);
        if (e is null) return false;

        e.MatchStatus  = "Rejected";
        e.Notes        = reason.Trim();
        e.ModifiedBy   = rejectedBy;
        e.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UploadAttachmentAsync(Guid uuid, string url, int modifiedBy)
    {
        var e = await _db.Invoices.FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete);
        if (e is null) return false;
        e.AttachmentUrl = url;
        e.ModifiedBy    = modifiedBy;
        e.ModifiedDate  = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    private static string DetermineMatchStatus(decimal invoiceTotal, decimal poValue, decimal grnValue, bool hasGrn)
    {
        const decimal tolerance = 0.05m;
        if (hasGrn)
        {
            var poVariancePct  = poValue  > 0 ? Math.Abs(invoiceTotal - poValue)  / poValue  : 1m;
            var grnVariancePct = grnValue > 0 ? Math.Abs(invoiceTotal - grnValue) / grnValue : 1m;
            if (poVariancePct <= tolerance && grnVariancePct <= tolerance)
                return "Matched";
            return "Variance";
        }
        else
        {
            var poVariancePct = poValue > 0 ? Math.Abs(invoiceTotal - poValue) / poValue : 1m;
            return poVariancePct <= tolerance ? "Matched" : "Variance";
        }
    }

    private async Task<string> GenerateInvoiceNumberAsync(int year)
    {
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = yearStart.AddYears(1);
        var count     = await _db.Invoices
            .CountAsync(i => i.CreatedDate >= yearStart && i.CreatedDate < yearEnd);
        return $"INV-{year}-{(count + 1):D5}";
    }
}

internal sealed class PaymentRepository : IPaymentRepository
{
    private readonly FinanceDbContext _db;

    public PaymentRepository(FinanceDbContext db) => _db = db;

    public async Task<Guid> CreateAsync(CreatePaymentRequest req, int createdBy)
    {
        if (req.AmountPaid <= 0)
            throw new BadRequestException("Payment amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(req.PaymentMethod))
            throw new BadRequestException("Payment method is required.");

        var invoice = await _db.Invoices
            .FirstOrDefaultAsync(i => i.UUID == req.InvoiceUuid && !i.IsDelete)
            ?? throw new NotFoundException("Invoice", req.InvoiceUuid);

        if (invoice.PaymentStatus == "Paid")
            throw new UnprocessableEntityException("Invoice is already fully paid.");

        var now           = DateTime.UtcNow;
        var paymentNumber = await GeneratePaymentNumberAsync(now.Year);

        var e = new Payment
        {
            UUID           = Guid.NewGuid(),
            PaymentNumber  = paymentNumber,
            InvoiceId      = invoice.Id,
            InvoiceUuid    = invoice.UUID,
            SupplierId     = invoice.SupplierId,
            SupplierName   = invoice.SupplierName,
            PaymentDate    = req.PaymentDate,
            AmountPaid     = req.AmountPaid,
            PaymentMethod  = req.PaymentMethod.Trim(),
            BankReference  = req.BankReference?.Trim(),
            ChequeNumber   = req.ChequeNumber?.Trim(),
            AccountDebited = req.AccountDebited?.Trim(),
            Status         = "Pending",
            Notes          = req.Notes?.Trim(),
            ProcessedBy    = createdBy,
            ProcessedAt    = now,
            IsActive       = true,
            CreatedBy      = createdBy,
            CreatedDate    = now
        };

        _db.Payments.Add(e);

        // Update invoice payment status
        var totalPaid = await _db.Payments
            .Where(p => p.InvoiceId == invoice.Id && !p.IsDelete && p.Status != "Reversed")
            .SumAsync(p => p.AmountPaid);
        totalPaid += req.AmountPaid;

        invoice.PaymentStatus = totalPaid >= invoice.TotalAmount ? "Paid"
            : totalPaid > 0 ? "Partial"
            : "Unpaid";
        invoice.ModifiedDate = now;

        await _db.SaveChangesAsync();
        return e.UUID;
    }

    public async Task<PaginatedResponse<PaymentListItemModel>> GetListAsync(PaymentFilter filter)
    {
        var q = _db.Payments
            .Include(x => x.Invoice)
            .Where(x => !x.IsDelete)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Status))
            q = q.Where(x => x.Status == filter.Status);
        if (filter.SupplierId.HasValue)
            q = q.Where(x => x.SupplierId == filter.SupplierId);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim().ToLower();
            q = q.Where(x => x.PaymentNumber.ToLower().Contains(s)
                           || x.SupplierName.ToLower().Contains(s));
        }

        var total = await q.CountAsync();
        var data  = await q
            .OrderByDescending(x => x.CreatedDate)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(x => new PaymentListItemModel
            {
                UUID          = x.UUID,
                PaymentNumber = x.PaymentNumber,
                InvoiceNumber = x.Invoice.InvoiceNumber,
                SupplierName  = x.SupplierName,
                PaymentDate   = x.PaymentDate,
                AmountPaid    = x.AmountPaid,
                PaymentMethod = x.PaymentMethod,
                Status        = x.Status
            })
            .ToListAsync();

        return new PaginatedResponse<PaymentListItemModel>
        {
            Data         = data,
            TotalRecords = total,
            Page         = filter.Page,
            PageSize     = filter.PageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)filter.PageSize)
        };
    }

    public async Task<PaymentDetailModel?> GetByUuidAsync(Guid uuid)
    {
        return await _db.Payments
            .Include(x => x.Invoice)
            .Where(x => x.UUID == uuid && !x.IsDelete)
            .Select(x => new PaymentDetailModel
            {
                UUID           = x.UUID,
                PaymentNumber  = x.PaymentNumber,
                InvoiceUuid    = x.InvoiceUuid,
                InvoiceNumber  = x.Invoice.InvoiceNumber,
                SupplierId     = x.SupplierId,
                SupplierName   = x.SupplierName,
                PaymentDate    = x.PaymentDate,
                AmountPaid     = x.AmountPaid,
                PaymentMethod  = x.PaymentMethod,
                BankReference  = x.BankReference,
                ChequeNumber   = x.ChequeNumber,
                AccountDebited = x.AccountDebited,
                Status         = x.Status,
                Notes          = x.Notes,
                ProcessedAt    = x.ProcessedAt,
                CreatedDate    = x.CreatedDate
            })
            .FirstOrDefaultAsync();
    }

    public async Task<bool> PatchAsync(Guid uuid, PatchPaymentRequest req, int modifiedBy)
    {
        var e = await _db.Payments.FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete);
        if (e is null) return false;

        if (req.Status        is not null) e.Status        = req.Status;
        if (req.BankReference is not null) e.BankReference = req.BankReference.Trim();
        if (req.ChequeNumber  is not null) e.ChequeNumber  = req.ChequeNumber.Trim();
        if (req.Notes         is not null) e.Notes         = req.Notes.Trim();

        // If reversed, update invoice payment status
        if (req.Status == "Reversed")
        {
            var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == e.InvoiceId);
            if (invoice is not null)
            {
                var totalPaid = await _db.Payments
                    .Where(p => p.InvoiceId == e.InvoiceId && !p.IsDelete && p.Status != "Reversed" && p.UUID != uuid)
                    .SumAsync(p => p.AmountPaid);

                invoice.PaymentStatus = totalPaid >= invoice.TotalAmount ? "Paid"
                    : totalPaid > 0 ? "Partial"
                    : "Unpaid";
                invoice.ModifiedDate = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync();
        return true;
    }

    private async Task<string> GeneratePaymentNumberAsync(int year)
    {
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = yearStart.AddYears(1);
        var count     = await _db.Payments
            .CountAsync(p => p.CreatedDate >= yearStart && p.CreatedDate < yearEnd);
        return $"PAY-{year}-{(count + 1):D5}";
    }
}
