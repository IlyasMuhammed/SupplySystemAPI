using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Repositories;

/// <summary>
/// Credit notes for supplier returns. A note is deducted from a supplier invoice — when it is raised, or later from
/// the carried-forward credit — only when that invoice is a payable: this organization's own, the note's supplier's,
/// approved, and still owing at least the note (<see cref="InvoiceSettlement.AvailableAsync"/>). A note deducted from
/// an invoice not yet approved was counted twice: its ledger credit at once, and again in the smaller debit the
/// approval then posted. Each change locks first, in the platform-wide order — the return (SRO), then the invoice,
/// then the note's own row — and checks what is committed now (<see cref="SupplierNotes"/>).
/// </summary>
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

    /// <summary>This organization's own credit notes, not deleted — the only ones a change may touch.</summary>
    private IQueryable<CreditNote> OwnNotes()
    {
        var organizationId = _fin.TenantContext.OrganizationId;
        return _fin.CreditNotes.Where(x => x.OrganizationId == organizationId && x.IsActive && !x.IsDelete);
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateCreditNoteRequest req, int createdBy)
    {
        var organizationId = _fin.TenantContext.OrganizationId;

        // One transaction with Warehouse's context on the same connection: the SRO's resolution commits with the
        // note and its ledger credit, or not at all.
        return await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            // 1. The SRO, locked first — a return is resolved once, by one credit or debit note — then read as
            //    committed now.
            var sro = await SupplierNotes.LockReturnAsync(_wh, req.SroId, organizationId)
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

            // 3. The invoice — named, or the SRO's GRN's — this organization's own and the return's supplier's,
            //    locked after the SRO and read afresh.
            var invoice = await SupplierNotes.LockNamedInvoiceAsync(_fin, req.InvoiceUuid, sro.GrnUuid);
            if (invoice is not null && invoice.SupplierId != sro.SupplierId) invoice = null;

            var now    = DateTime.UtcNow;
            var number = await NextNumberAsync(now.Year);

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

            // 4. Deduct it from the invoice when that is a payable still owing at least this much; otherwise carry it
            //    forward, to be applied once there is one (a reversed invoice owes nothing; one not yet approved is
            //    not on the ledger, and its approval would debit the reduced total — the credit counted twice).
            if (invoice is not null && await SupplierNotes.DeductibleAtCreationAsync(_fin, invoice, req.CreditAmount))
            {
                await SupplierNotes.DeductAsync(_fin, invoice, req.CreditAmount, now);

                creditNote.ApplicationStatus      = "APPLIED_TO_INVOICE";
                creditNote.AppliedToInvoiceUuid   = invoice.UUID;
                creditNote.AppliedToInvoiceNumber = invoice.InvoiceNumber;
                creditNote.AppliedAt              = now;
            }
            else
            {
                creditNote.ApplicationStatus    = SupplierNotes.CarriedForward;
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

            // 5. Update SRO — mark as RESOLVED_CREDIT (in the same transaction on SQL Server)
            sro.Status         = "RESOLVED_CREDIT";
            sro.ResolutionType = "CREDIT";
            sro.ResolvedAt     = now;
            sro.ModifiedBy     = createdBy;
            sro.ModifiedDate   = now;
            await _wh.SaveChangesAsync();

            return creditNote.UUID;
        }, _wh);
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

    /// <summary>
    /// Deducts a carried-forward credit note from an invoice: this organization's own (else 404), the note's
    /// supplier's (else 400), approved, not fully paid, not reversed, and still owing at least the note (else 422).
    /// The invoice is locked first, then the note's own row, and both are checked as committed now — so a reversal of
    /// the invoice, a payment of it, or another application of the note cannot slip in between.
    /// </summary>
    public async Task ApplyCarriedForwardAsync(Guid uuid, ApplyCreditNoteRequest req, int userId)
    {
        // The note's 404 and status first, as before; both are checked again under the locks.
        var cn = await OwnNotes().FirstOrDefaultAsync(x => x.UUID == uuid)
            ?? throw new NotFoundException("CreditNote", uuid);
        if (cn.ApplicationStatus != SupplierNotes.CarriedForward)
            throw new UnprocessableEntityException(
                $"Credit note must be in CARRIED_FORWARD status to apply. Current: {cn.ApplicationStatus}.");

        await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            if (!await InvoiceRowLocks.LockOneAsync(_fin, req.InvoiceUuid))
                throw new NotFoundException("Invoice", req.InvoiceUuid);

            var note = await LockNoteAsync(uuid) ?? throw new NotFoundException("CreditNote", uuid);
            if (note.ApplicationStatus != SupplierNotes.CarriedForward)
                throw new UnprocessableEntityException(
                    $"Credit note must be in CARRIED_FORWARD status to apply. Current: {note.ApplicationStatus}.");

            var invoice = await InvoiceRowLocks.Own(_fin).FirstOrDefaultAsync(i => i.UUID == req.InvoiceUuid)
                ?? throw new NotFoundException("Invoice", req.InvoiceUuid);

            var amountToApply = note.CarriedForwardAmount ?? note.CreditAmount;
            await SupplierNotes.CheckApplicationAsync(_fin, invoice, note.SupplierId, note.CreditNoteNumber, amountToApply,
                fullyPaid: "Cannot apply credit to a fully paid invoice.",
                reversed:  "Cannot apply credit to a reversed invoice — nothing is owed on it.");

            var now = DateTime.UtcNow;
            await SupplierNotes.DeductAsync(_fin, invoice, amountToApply, now);

            note.ApplicationStatus      = "APPLIED";
            note.AppliedToInvoiceUuid   = invoice.UUID;
            note.AppliedToInvoiceNumber = invoice.InvoiceNumber;
            note.AppliedAt              = now;
            note.ModifiedBy             = userId;
            note.ModifiedDate           = now;

            await _fin.SaveChangesAsync();
            return true;
        });
    }

    /// <summary>The note's row, locked (after the invoice's), then read as committed now. Null when it is not this organization's.</summary>
    private async Task<CreditNote?> LockNoteAsync(Guid uuid)
    {
        if (_fin.Database.IsRelational()
            && await OwnNotes().Where(x => x.UUID == uuid)
                   .ExecuteUpdateAsync(s => s.SetProperty(x => x.ApplicationStatus, x => x.ApplicationStatus)) == 0)
            return null;

        foreach (var stale in _fin.ChangeTracker.Entries<CreditNote>().Where(e => e.Entity.UUID == uuid).ToList())
            stale.State = EntityState.Detached;

        return await OwnNotes().FirstOrDefaultAsync(x => x.UUID == uuid);
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

    /// <summary>CN-YYYY-NNNNN: this organization's next free number this year, under the numbering lock (<see cref="DocumentNumbers"/>).</summary>
    private async Task<string> NextNumberAsync(int year)
    {
        var organizationId = _fin.TenantContext.OrganizationId;
        var yearStart      = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd        = yearStart.AddYears(1);
        var own            = _fin.CreditNotes.IgnoreQueryFilters().Where(c => c.OrganizationId == organizationId);

        await DocumentNumbers.SerializeAsync(_fin, "finance.credit-note-number", organizationId);
        var count = await own.CountAsync(c => c.CreatedDate >= yearStart && c.CreatedDate < yearEnd);
        return await DocumentNumbers.NextFreeAsync(count, n => $"CN-{year}-{n:D5}",
            number => own.AnyAsync(c => c.CreditNoteNumber == number));
    }
}

/// <summary>
/// What credit and debit notes share: the locks a new note takes (its return, then the invoice it names) and when a
/// note may be deducted from an invoice.
/// <para>
/// <b>Lock order.</b> The supplier return order (SRO) first — nothing else locks an SRO — then the invoice
/// (<see cref="InvoiceRowLocks"/>), then the note's own row or the numbering lock. Never the other way round.
/// </para>
/// </summary>
internal static class SupplierNotes
{
    internal const string CarriedForward = "CARRIED_FORWARD";

    /// <summary>
    /// The return's row, locked (a no-op UPDATE, this organization's only), then read as committed now with its
    /// lines. Null when this organization has no such active return. On the in-memory provider it only reads.
    /// </summary>
    internal static async Task<SupplierReturnOrder?> LockReturnAsync(WarehouseDbContext wh, Guid sroUuid, Guid organizationId)
    {
        var own = wh.SupplierReturnOrders.Where(s => s.UUID == sroUuid && s.IsActive && s.OrganizationId == organizationId);

        if (wh.Database.IsRelational()
            && await own.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, x => x.Status)) == 0)
            return null;

        foreach (var stale in wh.ChangeTracker.Entries<SupplierReturnOrder>().Where(e => e.Entity.UUID == sroUuid).ToList())
            stale.State = EntityState.Detached;

        return await own.Include(s => s.Lines).FirstOrDefaultAsync();
    }

    /// <summary>
    /// The invoice a new note names — explicitly, or else the one raised for the return's GRN (an approved one first)
    /// — if it is this organization's own: locked, then read as committed now. Null when there is none.
    /// </summary>
    internal static async Task<Invoice?> LockNamedInvoiceAsync(FinanceDbContext fin, Guid? named, Guid? grnUuid)
    {
        var uuid = named;
        if (uuid is null && grnUuid is { } grn)
            uuid = await InvoiceRowLocks.Own(fin)
                .Where(i => i.GrnUuid == grn)
                .OrderByDescending(i => i.MatchStatus == InvoiceMatchStatus.Approved)
                .ThenByDescending(i => i.Id)
                .Select(i => (Guid?)i.UUID)
                .FirstOrDefaultAsync();

        if (uuid is not { } id || !await InvoiceRowLocks.LockOneAsync(fin, id)) return null;
        return await InvoiceRowLocks.Own(fin).FirstOrDefaultAsync(i => i.UUID == id);
    }

    /// <summary>Whether a new note can be deducted from the invoice at once: approved, not fully paid, and still owing at least the note.</summary>
    internal static async Task<bool> DeductibleAtCreationAsync(FinanceDbContext fin, Invoice invoice, decimal amount) =>
        InvoiceMatchStatus.Is(invoice.MatchStatus, InvoiceMatchStatus.Approved)
     && !InvoicePaymentStatus.IsFullyPaid(invoice.PaymentStatus)
     && amount <= await InvoiceSettlement.AvailableAsync(fin, invoice);

    /// <summary>
    /// Refuses to deduct a carried-forward note from the invoice: another supplier's (400); fully paid, reversed, not
    /// approved, or owing less than the note (422).
    /// </summary>
    internal static async Task CheckApplicationAsync(
        FinanceDbContext fin, Invoice invoice, Guid noteSupplierId, string noteNumber, decimal amount, string fullyPaid, string reversed)
    {
        if (invoice.SupplierId != noteSupplierId)
            throw new BadRequestException(
                $"Invoice {invoice.InvoiceNumber} is another supplier's; {noteNumber} can be applied only to its own supplier's invoices.");
        if (InvoicePaymentStatus.IsFullyPaid(invoice.PaymentStatus))
            throw new UnprocessableEntityException(fullyPaid);
        if (InvoiceMatchStatus.Is(invoice.MatchStatus, InvoiceMatchStatus.Reversed))
            throw new UnprocessableEntityException(reversed);
        if (!InvoiceMatchStatus.Is(invoice.MatchStatus, InvoiceMatchStatus.Approved))
            throw new UnprocessableEntityException(
                $"Invoice {invoice.InvoiceNumber} is not approved yet; a note can be applied only to an approved invoice.");

        var available = await InvoiceSettlement.AvailableAsync(fin, invoice);
        if (amount > available)
            throw new UnprocessableEntityException(
                $"Cannot apply {noteNumber} ({amount:N2}): the note is more than is still owed on invoice {invoice.InvoiceNumber} "
              + $"({Math.Max(0m, available):N2} after what is paid or on payments).");
    }

    /// <summary>
    /// Takes the note off the invoice's total — never below what is paid or on payments, which the checks above
    /// guarantee — and, when something is paid, the payment status follows (a note that brings the total down to
    /// what is paid makes it fully paid).
    /// </summary>
    internal static async Task DeductAsync(FinanceDbContext fin, Invoice invoice, decimal amount, DateTime now)
    {
        invoice.TotalAmount -= amount;

        // Money from both flows: legacy single-invoice payments never reach PaidAmount, so a note that settles an
        // invoice paid that way must count them too, or it stays "Partial" — and owed — on every payables list.
        var legacyPaid = await fin.Payments
            .Where(p => p.InvoiceId == invoice.Id && !p.IsDelete && p.Status != "Reversed")
            .SumAsync(p => (decimal?)p.AmountPaid) ?? 0m;
        if (invoice.PaidAmount > 0m || legacyPaid > 0m)
            invoice.PaymentStatus = InvoicePaymentStatus.AfterSettlement(invoice.PaidAmount, legacyPaid, invoice.TotalAmount);
        invoice.ModifiedDate = now;
    }
}
