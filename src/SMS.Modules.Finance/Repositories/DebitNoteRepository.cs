using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Repositories;

internal sealed class DebitNoteRepository : IDebitNoteRepository
{
    private readonly FinanceDbContext       _fin;
    private readonly WarehouseDbContext     _wh;
    private readonly IBackgroundJobClient   _jobs;
    private readonly INotificationService   _notif;
    private readonly IAuditService          _audit;
    private readonly ISupplierLedgerService _ledger;

    public DebitNoteRepository(
        FinanceDbContext fin,
        WarehouseDbContext wh,
        IBackgroundJobClient jobs,
        INotificationService notif,
        IAuditService audit,
        ISupplierLedgerService ledger)
    {
        _fin    = fin;
        _wh     = wh;
        _jobs   = jobs;
        _notif  = notif;
        _audit  = audit;
        _ledger = ledger;
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateDebitNoteRequest req, int createdBy)
    {
        var sro = await _wh.SupplierReturnOrders
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.UUID == req.SroId && s.IsActive)
            ?? throw new NotFoundException("SupplierReturnOrder", req.SroId);

        if (sro.Status != "SUPPLIER_RECEIVED" && sro.Status != "ESCALATED")
            throw new UnprocessableEntityException(
                $"SRO must be in SUPPLIER_RECEIVED or ESCALATED status to raise a debit note. Current: {sro.Status}.");

        if (req.DebitAmount <= 0)
            throw new BadRequestException("DebitAmount must be greater than zero.");

        var sroTotalValue = sro.Lines.Sum(l => l.QtyToReturn * (l.UnitCost ?? 0m));
        if (sroTotalValue > 0 && req.DebitAmount > sroTotalValue)
            throw new BadRequestException(
                $"DebitAmount ({req.DebitAmount:N2}) exceeds SRO total return value ({sroTotalValue:N2}).");

        var now    = DateTime.UtcNow;
        var number = await GenerateDebitNoteNumberAsync(now.Year);

        // Resolve supplier contact email from SRO supplier — stored on the SRO itself if supplied, otherwise null
        string? supplierEmail = null;
        // Try fetching from the Suppliers module is not feasible here (no reference), so
        // we rely on supplier contact email that may have been captured at SRO creation time.
        // If the SupplierContactEmail field is populated on the SRO entity, use it; otherwise skip email.

        // Resolve invoice: prefer explicit InvoiceUuid; fall back to SRO's GRN reference
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

        var dn = new DebitNote
        {
            UUID                 = Guid.NewGuid(),
            DebitNoteNumber      = number,
            SroUuid              = sro.UUID,
            SroNumber            = sro.ReturnNumber,
            SupplierId           = sro.SupplierId,
            SupplierName         = sro.SupplierName,
            SupplierContactEmail = supplierEmail,
            DebitReason          = req.DebitReason,
            DebitReasonDetail    = req.DebitReasonDetail,
            DebitAmount          = req.DebitAmount,
            InvoiceUuid          = invoice?.UUID,
            InvoiceNumber        = invoice?.InvoiceNumber,
            Status               = "ISSUED",
            IssuedAt             = now,
            Notes                = req.Notes,
            IsActive             = true,
            CreatedBy            = createdBy,
            CreatedDate          = now
        };

        // Apply to invoice immediately (matching CreditNote's behavior) or carry forward
        if (invoice is not null && !InvoicePaymentStatus.IsFullyPaid(invoice.PaymentStatus))
        {
            invoice.TotalAmount -= dn.DebitAmount;
            if (invoice.TotalAmount < 0) invoice.TotalAmount = 0;
            invoice.ModifiedDate = now;

            dn.ApplicationStatus     = "APPLIED_TO_INVOICE";
            dn.AppliedToInvoiceUuid  = invoice.UUID;
            dn.AppliedToInvoiceNumber = invoice.InvoiceNumber;
            dn.AppliedAt             = now;
        }
        else
        {
            dn.ApplicationStatus   = "CARRIED_FORWARD";
            dn.CarriedForwardAmount = dn.DebitAmount;
        }

        _fin.DebitNotes.Add(dn);

        // SFM-002 / Addendum 8A: the ledger entry is posted in the SAME SaveChangesAsync as the
        // debit note creation (PostEntryAsync performs that save) — if it throws, the debit note
        // (and any invoice adjustment above) is never persisted either. Do not call
        // _fin.SaveChangesAsync() separately here.
        //
        // Direction: a debit note recovers value FROM the supplier for a return, which reduces
        // what we owe them — same accounting direction as a credit note, so it's posted as a
        // ledger credit (not a debit, which would incorrectly increase the balance owed).
        await _ledger.PostEntryAsync(
            dn.SupplierId, "DEBIT_NOTE_APPROVED", "DebitNote", dn.UUID, dn.DebitNoteNumber,
            debitAmount: 0m, creditAmount: dn.DebitAmount,
            narration: $"Debit note {dn.DebitNoteNumber} issued against SRO {dn.SroNumber} — {dn.DebitReason}.",
            createdBy: createdBy, supplierName: dn.SupplierName);

        // Mark SRO as resolved via debit note
        sro.Status         = "RESOLVED_DEBIT";
        sro.ResolutionType = "DEBIT";
        sro.ResolvedAt     = now;
        sro.ModifiedBy     = createdBy;
        sro.ModifiedDate   = now;
        await _wh.SaveChangesAsync();

        await _audit.LogAsync(createdBy, null, "FINANCE", "CREATE", "DebitNote", dn.UUID,
            notes: $"Debit note {dn.DebitNoteNumber} raised against SRO {dn.SroNumber} — amount: {dn.DebitAmount:N2}, reason: {dn.DebitReason}");

        // Enqueue supplier email notification if contact email is available
        if (!string.IsNullOrWhiteSpace(supplierEmail))
        {
            var emailBody = BuildDebitNoteEmailHtml(dn);
            _jobs.Enqueue<INotificationService>(svc =>
                svc.SendEmailAsync(supplierEmail,
                    $"Debit Note {number} — Action Required",
                    emailBody));
        }

        return dn.UUID;
    }

    // ── Update status ─────────────────────────────────────────────────────────

    public async Task UpdateStatusAsync(Guid uuid, UpdateDebitNoteStatusRequest req, int userId)
    {
        var dn = await _fin.DebitNotes
            .FirstOrDefaultAsync(x => x.UUID == uuid && x.IsActive && !x.IsDelete)
            ?? throw new NotFoundException("DebitNote", uuid);

        var now = DateTime.UtcNow;

        switch (req.NewStatus)
        {
            case "ACKNOWLEDGED":
                if (dn.Status != "ISSUED")
                    throw new UnprocessableEntityException(
                        $"Can only acknowledge a debit note in ISSUED status. Current: {dn.Status}.");
                dn.AcknowledgedAt = now;
                break;

            case "DISPUTED":
                if (dn.Status != "ISSUED" && dn.Status != "ACKNOWLEDGED")
                    throw new UnprocessableEntityException(
                        $"Can only dispute a debit note in ISSUED or ACKNOWLEDGED status. Current: {dn.Status}.");
                dn.DisputedAt  = now;
                dn.DisputeNotes = req.DisputeNotes;
                break;

            case "SETTLED":
                if (dn.Status != "ACKNOWLEDGED" && dn.Status != "DISPUTED")
                    throw new UnprocessableEntityException(
                        $"Can only settle a debit note in ACKNOWLEDGED or DISPUTED status. Current: {dn.Status}.");
                dn.SettledAt = now;
                break;

            case "WRITTEN_OFF":
                if (dn.Status == "SETTLED" || dn.Status == "WRITTEN_OFF")
                    throw new UnprocessableEntityException(
                        $"Cannot write off a debit note in {dn.Status} status.");
                break;

            default:
                throw new BadRequestException($"Invalid status transition target: {req.NewStatus}.");
        }

        dn.Status       = req.NewStatus;
        if (!string.IsNullOrWhiteSpace(req.Notes))
            dn.Notes = req.Notes;
        dn.ModifiedBy   = userId;
        dn.ModifiedDate = now;

        await _fin.SaveChangesAsync();
    }

    // ── Apply carried-forward debit to a specified invoice ────────────────────

    public async Task ApplyCarriedForwardAsync(Guid uuid, ApplyDebitNoteRequest req, int userId)
    {
        var dn = await _fin.DebitNotes
            .FirstOrDefaultAsync(x => x.UUID == uuid && x.IsActive && !x.IsDelete)
            ?? throw new NotFoundException("DebitNote", uuid);

        if (dn.ApplicationStatus != "CARRIED_FORWARD")
            throw new UnprocessableEntityException(
                $"Debit note must be in CARRIED_FORWARD status to apply. Current: {dn.ApplicationStatus}.");

        var invoice = await _fin.Invoices
            .FirstOrDefaultAsync(i => i.UUID == req.InvoiceUuid && !i.IsDelete)
            ?? throw new NotFoundException("Invoice", req.InvoiceUuid);

        if (InvoicePaymentStatus.IsFullyPaid(invoice.PaymentStatus))
            throw new UnprocessableEntityException("Cannot apply a debit note to a fully paid invoice.");

        var amountToApply = dn.CarriedForwardAmount ?? dn.DebitAmount;

        invoice.TotalAmount -= amountToApply;
        if (invoice.TotalAmount < 0) invoice.TotalAmount = 0;
        invoice.ModifiedDate = DateTime.UtcNow;

        dn.ApplicationStatus      = "APPLIED";
        dn.AppliedToInvoiceUuid   = invoice.UUID;
        dn.AppliedToInvoiceNumber = invoice.InvoiceNumber;
        dn.AppliedAt              = DateTime.UtcNow;
        dn.ModifiedBy             = userId;
        dn.ModifiedDate           = DateTime.UtcNow;

        await _fin.SaveChangesAsync();
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<DebitNoteListItemModel>> GetListAsync(DebitNoteListFilter filter)
    {
        var query = _fin.DebitNotes
            .Where(d => d.IsActive && !d.IsDelete)
            .AsQueryable();

        if (filter.SupplierId.HasValue)
            query = query.Where(d => d.SupplierId == filter.SupplierId.Value);
        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(d => d.Status == filter.Status);
        if (filter.DateFrom.HasValue)
            query = query.Where(d => d.CreatedDate >= filter.DateFrom.Value);
        if (filter.DateTo.HasValue)
            query = query.Where(d => d.CreatedDate <= filter.DateTo.Value);

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderByDescending(d => d.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new DebitNoteListItemModel
            {
                UUID                   = d.UUID,
                DebitNoteNumber        = d.DebitNoteNumber,
                SroNumber              = d.SroNumber,
                SupplierId             = d.SupplierId,
                SupplierName           = d.SupplierName,
                DebitReason            = d.DebitReason,
                DebitAmount            = d.DebitAmount,
                InvoiceNumber          = d.InvoiceNumber,
                ApplicationStatus      = d.ApplicationStatus,
                AppliedToInvoiceNumber = d.AppliedToInvoiceNumber,
                Status                 = d.Status,
                IssuedAt               = d.IssuedAt,
                CreatedDate            = d.CreatedDate
            })
            .ToListAsync();

        return new PaginatedResponse<DebitNoteListItemModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    // ── Get by ID ─────────────────────────────────────────────────────────────

    public async Task<DebitNoteDetailModel?> GetByIdAsync(Guid uuid)
    {
        var d = await _fin.DebitNotes
            .FirstOrDefaultAsync(x => x.UUID == uuid && x.IsActive && !x.IsDelete);

        return d is null ? null : MapToDetail(d);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DebitNoteDetailModel MapToDetail(DebitNote d) => new()
    {
        UUID                   = d.UUID,
        DebitNoteNumber        = d.DebitNoteNumber,
        SroUuid                = d.SroUuid,
        SroNumber              = d.SroNumber,
        SupplierId             = d.SupplierId,
        SupplierName           = d.SupplierName,
        SupplierContactEmail   = d.SupplierContactEmail,
        DebitReason            = d.DebitReason,
        DebitReasonDetail      = d.DebitReasonDetail,
        DebitAmount            = d.DebitAmount,
        InvoiceUuid            = d.InvoiceUuid,
        InvoiceNumber          = d.InvoiceNumber,
        ApplicationStatus      = d.ApplicationStatus,
        AppliedToInvoiceUuid   = d.AppliedToInvoiceUuid,
        AppliedToInvoiceNumber = d.AppliedToInvoiceNumber,
        CarriedForwardAmount   = d.CarriedForwardAmount,
        AppliedAt              = d.AppliedAt,
        Status                 = d.Status,
        IssuedAt               = d.IssuedAt,
        AcknowledgedAt         = d.AcknowledgedAt,
        DisputedAt             = d.DisputedAt,
        SettledAt              = d.SettledAt,
        DisputeNotes           = d.DisputeNotes,
        Notes                  = d.Notes,
        CreatedDate            = d.CreatedDate
    };

    private async Task<string> GenerateDebitNoteNumberAsync(int year)
    {
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = yearStart.AddYears(1);
        var count     = await _fin.DebitNotes
            .CountAsync(d => d.CreatedDate >= yearStart && d.CreatedDate < yearEnd);
        return $"DN-{year}-{(count + 1):D5}";
    }

    private static string BuildDebitNoteEmailHtml(DebitNote dn) =>
        $"""
        <html><body style="font-family:Arial,sans-serif;color:#333">
          <h2 style="color:#c0392b">Debit Note Issued</h2>
          <p>Dear {dn.SupplierName},</p>
          <p>A debit note has been raised against your account. Please review the details below:</p>
          <table style="border-collapse:collapse;width:100%;max-width:500px">
            <tr><td style="padding:6px 12px;font-weight:bold;background:#f5f5f5">Debit Note No.</td>
                <td style="padding:6px 12px">{dn.DebitNoteNumber}</td></tr>
            <tr><td style="padding:6px 12px;font-weight:bold;background:#f5f5f5">SRO Reference</td>
                <td style="padding:6px 12px">{dn.SroNumber}</td></tr>
            <tr><td style="padding:6px 12px;font-weight:bold;background:#f5f5f5">Reason</td>
                <td style="padding:6px 12px">{dn.DebitReason}</td></tr>
            <tr><td style="padding:6px 12px;font-weight:bold;background:#f5f5f5">Amount</td>
                <td style="padding:6px 12px">{dn.DebitAmount:N2}</td></tr>
            <tr><td style="padding:6px 12px;font-weight:bold;background:#f5f5f5">Issued On</td>
                <td style="padding:6px 12px">{dn.IssuedAt:dd MMM yyyy}</td></tr>
          </table>
          <p style="margin-top:16px">Please acknowledge receipt within 7 days or contact our procurement team.</p>
          <hr/>
          <p style="font-size:12px;color:#999">This is an automated notification from the Supply Management System.</p>
        </body></html>
        """;
}
