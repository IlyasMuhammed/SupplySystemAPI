using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Repositories;

internal sealed class CreditNoteRepository : ICreditNoteRepository
{
    private readonly FinanceDbContext       _fin;
    private readonly WarehouseDbContext     _wh;
    private readonly ISupplierLedgerService _ledger;

    public CreditNoteRepository(FinanceDbContext fin, WarehouseDbContext wh, ISupplierLedgerService ledger)
    {
        _fin    = fin;
        _wh     = wh;
        _ledger = ledger;
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateCreditNoteRequest req, int createdBy)
    {
        // 1. Load the SRO
        var sro = await _wh.SupplierReturnOrders
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.UUID == req.SroId && s.IsActive)
            ?? throw new NotFoundException("SupplierReturnOrder", req.SroId);

        if (sro.Status != "SUPPLIER_RECEIVED" && sro.Status != "AWAITING_REPLACEMENT")
            throw new UnprocessableEntityException(
                $"SRO must be in SUPPLIER_RECEIVED or AWAITING_REPLACEMENT status to create a credit note. Current: {sro.Status}.");

        // 2. Validate CreditAmount ≤ SRO total return value
        var sroTotalValue = sro.Lines.Sum(l => l.QtyToReturn * (l.UnitCost ?? 0m));
        if (req.CreditAmount > sroTotalValue && sroTotalValue > 0)
            throw new BadRequestException(
                $"CreditAmount ({req.CreditAmount:N2}) exceeds SRO total return value ({sroTotalValue:N2}).");

        if (req.CreditAmount <= 0)
            throw new BadRequestException("CreditAmount must be greater than zero.");

        var now    = DateTime.UtcNow;
        var number = await GenerateCreditNoteNumberAsync(now.Year);

        // 3. Resolve invoice: prefer explicit InvoiceUuid; fall back to SRO's GRN reference
        Invoice? invoice = null;
        if (req.InvoiceUuid.HasValue)
        {
            invoice = await _fin.Invoices
                .FirstOrDefaultAsync(i => i.UUID == req.InvoiceUuid.Value && !i.IsDelete);
        }
        else if (sro.GrnUuid.HasValue)
        {
            invoice = await _fin.Invoices
                .FirstOrDefaultAsync(i => i.GrnUuid == sro.GrnUuid.Value && !i.IsDelete);
        }

        var creditNote = new CreditNote
        {
            UUID                 = Guid.NewGuid(),
            CreditNoteNumber     = number,
            SupplierCreditNoteNo = req.SupplierCreditNoteNo,
            SroUuid              = sro.UUID,
            SroNumber            = sro.ReturnNumber,
            SupplierId           = sro.SupplierId,
            SupplierName         = sro.SupplierName,
            InvoiceUuid          = invoice?.UUID,
            InvoiceNumber        = invoice?.InvoiceNumber,
            CreditDate           = req.CreditDate,
            CreditAmount         = req.CreditAmount,
            Notes                = req.Notes,
            IsActive             = true,
            CreatedBy            = createdBy,
            CreatedDate          = now
        };

        // 4. Apply to invoice or carry forward
        if (invoice is not null && !InvoicePaymentStatus.IsFullyPaid(invoice.PaymentStatus))
        {
            invoice.TotalAmount    -= req.CreditAmount;
            if (invoice.TotalAmount < 0) invoice.TotalAmount = 0;
            invoice.ModifiedDate    = now;

            creditNote.ApplicationStatus     = "APPLIED_TO_INVOICE";
            creditNote.AppliedToInvoiceUuid  = invoice.UUID;
            creditNote.AppliedToInvoiceNumber = invoice.InvoiceNumber;
            creditNote.AppliedAt             = now;
        }
        else
        {
            creditNote.ApplicationStatus   = "CARRIED_FORWARD";
            creditNote.CarriedForwardAmount = req.CreditAmount;
        }

        _fin.CreditNotes.Add(creditNote);

        // SFM-002 / Addendum 8A: the ledger credit is posted in the SAME SaveChangesAsync as the
        // credit note creation (PostEntryAsync performs that save) — if it throws, the credit
        // note (and any invoice adjustment above) is never persisted either. Do not call
        // _fin.SaveChangesAsync() separately here.
        await _ledger.PostEntryAsync(
            creditNote.SupplierId, "CREDIT_NOTE_APPROVED", "CreditNote", creditNote.UUID, creditNote.CreditNoteNumber,
            debitAmount: 0m, creditAmount: creditNote.CreditAmount,
            narration: $"Credit note {creditNote.CreditNoteNumber} issued against SRO {creditNote.SroNumber}.",
            createdBy: createdBy, supplierName: creditNote.SupplierName);

        // 5. Update SRO — mark as RESOLVED_CREDIT
        sro.Status         = "RESOLVED_CREDIT";
        sro.ResolutionType = "CREDIT";
        sro.ResolvedAt     = now;
        sro.ModifiedBy     = createdBy;
        sro.ModifiedDate   = now;
        await _wh.SaveChangesAsync();

        return creditNote.UUID;
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<CreditNoteListItemModel>> GetListAsync(CreditNoteListFilter filter)
    {
        var query = _fin.CreditNotes
            .Where(c => c.IsActive && !c.IsDelete)
            .AsQueryable();

        if (filter.SupplierId.HasValue)
            query = query.Where(c => c.SupplierId == filter.SupplierId.Value);
        if (!string.IsNullOrWhiteSpace(filter.ApplicationStatus))
            query = query.Where(c => c.ApplicationStatus == filter.ApplicationStatus);
        if (filter.DateFrom.HasValue)
            query = query.Where(c => c.CreditDate >= filter.DateFrom.Value);
        if (filter.DateTo.HasValue)
            query = query.Where(c => c.CreditDate <= filter.DateTo.Value);

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderByDescending(c => c.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CreditNoteListItemModel
            {
                UUID                   = c.UUID,
                CreditNoteNumber       = c.CreditNoteNumber,
                SupplierCreditNoteNo   = c.SupplierCreditNoteNo,
                SroNumber              = c.SroNumber,
                SupplierId             = c.SupplierId,
                SupplierName           = c.SupplierName,
                InvoiceNumber          = c.InvoiceNumber,
                CreditDate             = c.CreditDate,
                CreditAmount           = c.CreditAmount,
                ApplicationStatus      = c.ApplicationStatus,
                AppliedToInvoiceNumber = c.AppliedToInvoiceNumber,
                CreatedDate            = c.CreatedDate
            })
            .ToListAsync();

        return new PaginatedResponse<CreditNoteListItemModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    // ── Get by ID ─────────────────────────────────────────────────────────────

    public async Task<CreditNoteDetailModel?> GetByIdAsync(Guid uuid)
    {
        var c = await _fin.CreditNotes
            .FirstOrDefaultAsync(x => x.UUID == uuid && x.IsActive && !x.IsDelete);

        return c is null ? null : MapToDetail(c);
    }

    // ── Apply carried-forward credit to a specified invoice ───────────────────

    public async Task ApplyCarriedForwardAsync(Guid uuid, ApplyCreditNoteRequest req, int userId)
    {
        var cn = await _fin.CreditNotes
            .FirstOrDefaultAsync(x => x.UUID == uuid && x.IsActive && !x.IsDelete)
            ?? throw new NotFoundException("CreditNote", uuid);

        if (cn.ApplicationStatus != "CARRIED_FORWARD")
            throw new UnprocessableEntityException(
                $"Credit note must be in CARRIED_FORWARD status to apply. Current: {cn.ApplicationStatus}.");

        var invoice = await _fin.Invoices
            .FirstOrDefaultAsync(i => i.UUID == req.InvoiceUuid && !i.IsDelete)
            ?? throw new NotFoundException("Invoice", req.InvoiceUuid);

        if (InvoicePaymentStatus.IsFullyPaid(invoice.PaymentStatus))
            throw new UnprocessableEntityException("Cannot apply credit to a fully paid invoice.");

        var amountToApply = cn.CarriedForwardAmount ?? cn.CreditAmount;

        invoice.TotalAmount  -= amountToApply;
        if (invoice.TotalAmount < 0) invoice.TotalAmount = 0;
        invoice.ModifiedDate  = DateTime.UtcNow;

        cn.ApplicationStatus      = "APPLIED";
        cn.AppliedToInvoiceUuid   = invoice.UUID;
        cn.AppliedToInvoiceNumber = invoice.InvoiceNumber;
        cn.AppliedAt              = DateTime.UtcNow;
        cn.ModifiedBy             = userId;
        cn.ModifiedDate           = DateTime.UtcNow;

        await _fin.SaveChangesAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CreditNoteDetailModel MapToDetail(CreditNote c) => new()
    {
        UUID                   = c.UUID,
        CreditNoteNumber       = c.CreditNoteNumber,
        SupplierCreditNoteNo   = c.SupplierCreditNoteNo,
        SroUuid                = c.SroUuid,
        SroNumber              = c.SroNumber,
        SupplierId             = c.SupplierId,
        SupplierName           = c.SupplierName,
        InvoiceUuid            = c.InvoiceUuid,
        InvoiceNumber          = c.InvoiceNumber,
        CreditDate             = c.CreditDate,
        CreditAmount           = c.CreditAmount,
        ApplicationStatus      = c.ApplicationStatus,
        AppliedToInvoiceUuid   = c.AppliedToInvoiceUuid,
        AppliedToInvoiceNumber = c.AppliedToInvoiceNumber,
        CarriedForwardAmount   = c.CarriedForwardAmount,
        AppliedAt              = c.AppliedAt,
        Notes                  = c.Notes,
        CreatedDate            = c.CreatedDate
    };

    private async Task<string> GenerateCreditNoteNumberAsync(int year)
    {
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = yearStart.AddYears(1);
        var count     = await _fin.CreditNotes
            .CountAsync(c => c.CreatedDate >= yearStart && c.CreatedDate < yearEnd);
        return $"CN-{year}-{(count + 1):D5}";
    }
}
