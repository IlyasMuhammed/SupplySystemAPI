using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Repositories;

internal sealed class InvoiceRepository : IInvoiceRepository
{
    /// <summary>The supplier ledger entry an approval posts, and the one a reversal posts against it.</summary>
    internal const string ApprovedLedgerType = "INVOICE_APPROVED";
    internal const string ReversedLedgerType = "INVOICE_REVERSED";

    /// <summary>Invoice.ReversalReason's column length.</summary>
    internal const int ReversalReasonMaxLength = 500;
    /// <summary>Invoice.Notes' column length — where a rejection reason, and an approval's notes, are kept.</summary>
    internal const int NotesMaxLength = 300;
    /// <summary>Invoice.SupplierInvoiceNo's, PaymentMethod's and AttachmentUrl's column lengths.</summary>
    internal const int SupplierInvoiceNoMaxLength = 50;
    internal const int PaymentMethodMaxLength     = 50;
    internal const int AttachmentUrlMaxLength     = 500;

    private readonly FinanceDbContext            _db;
    private readonly DemandDbContext             _demand;
    private readonly WarehouseDbContext          _warehouse;
    private readonly ISupplierLedgerService      _ledger;
    private readonly ISupplierNameLookupService  _supplierNames;
    private readonly ITaxCodeLookup?               _taxCodes;
    private readonly IExchangeRateProvider?        _rates;
    private readonly IOrganizationCurrencyService? _orgCurrency;
    private readonly ICurrencyCodeLookup?          _currencyCodes;
    private readonly ILogger<InvoiceRepository>?   _log;
    private readonly ICurrencyService?             _currency;
    private readonly SMS.Modules.Lookups.Services.ILookupsService? _lookups;

    /// <param name="taxCodes">Purchase tax codes (S-3). Optional so a hand-built harness needs nothing extra; without it an invoice can only carry tax as an amount.</param>
    /// <param name="rates">Exchange rates, for the snapshot at approval (S-5). Optional; without it a foreign-currency invoice gets no rate.</param>
    /// <param name="orgCurrency">The organization's base currency id (Tenancy). Optional; without it no snapshot is taken.</param>
    /// <param name="currencyCodes">Turns that id into an ISO code (Lookups). Optional, as above.</param>
    /// <param name="currency">
    /// A35 (D-5, D-12) — when registered (always, in the host) approval <b>locks</b> the rate against the <b>purchase</b>
    /// base at the approval date; a foreign invoice with no rate is refused (400). Without it the S-5 snapshot applies.
    /// </param>
    /// <param name="lookups">The global currency catalog (code ↔ id), for the invoice's CurrencyId (D-10). Optional.</param>
    public InvoiceRepository(
        FinanceDbContext db, DemandDbContext demand, WarehouseDbContext warehouse,
        ISupplierLedgerService ledger, ISupplierNameLookupService supplierNames,
        ITaxCodeLookup? taxCodes = null, IExchangeRateProvider? rates = null,
        IOrganizationCurrencyService? orgCurrency = null, ICurrencyCodeLookup? currencyCodes = null,
        ILogger<InvoiceRepository>? log = null,
        ICurrencyService? currency = null, SMS.Modules.Lookups.Services.ILookupsService? lookups = null)
    {
        _currency      = currency;
        _lookups       = lookups;
        _db            = db;
        _demand        = demand;
        _warehouse     = warehouse;
        _ledger        = ledger;
        _supplierNames = supplierNames;
        _taxCodes      = taxCodes;
        _rates         = rates;
        _orgCurrency   = orgCurrency;
        _currencyCodes = currencyCodes;
        _log           = log;
    }

    // ── One change to one invoice at a time ───────────────────────────────────

    /// <summary>
    /// The invoices a change may touch: this organization's own, not deleted. A super admin reads every
    /// organization's invoices (the tenant filter lets them), but approving, rejecting, reversing or editing another
    /// organization's would book its ledger entries — stamped with the caller's organization — into the caller's own
    /// books, and read the wrong organization's rates. Another organization's invoice is therefore not found (404).
    /// </summary>
    private IQueryable<Invoice> OwnInvoices => InvoiceRowLocks.Own(_db);

    /// <summary>
    /// Runs <paramref name="work"/> — one approval, rejection, reversal or edit of one invoice — so that no other
    /// change to the same invoice can interleave with it. The <c>invoices</c> table has no concurrency token (adding
    /// one is a model change), so before this two requests could both read "Pending" and both approve: two ledger
    /// debits, the purchase order invoiced twice; or a rejection or an edit could write over an approval already
    /// booked.
    /// <para>
    /// On SQL Server it is one transaction (inside the retrying execution strategy, as production configures it):
    /// first a no-op UPDATE of the invoice's row (EF's ExecuteUpdate), which takes the row's exclusive lock until
    /// commit, then <paramref name="work"/> reads the invoice afresh and does its checks on what is committed now. A
    /// second change of the same invoice waits at its own UPDATE until the first has committed, then sees what it did
    /// — and is refused (409) when it no longer applies. Demand's context joins the same transaction on the same
    /// connection, so an approval or reversal, its ledger entries and the purchase order's invoiced quantities commit
    /// together or not at all, and a reversal can never interleave with the approval it undoes. The purchase order's
    /// own row is locked too, after the invoice (<see cref="LockPurchaseOrderAsync"/>), so two invoices of one PO
    /// cannot lose each other's quantities. The mechanics, and the lock order every module keeps, are in
    /// <see cref="InvoiceRowLocks"/>.
    /// </para>
    /// <para>
    /// False when there is no such invoice in this organization (the caller's 404). The in-memory provider (unit
    /// tests) has neither transactions nor ExecuteUpdate: there the work simply runs.
    /// </para>
    /// </summary>
    private Task<bool> OneAtATimeAsync(Guid uuid, Func<Task<bool>> work) =>
        InvoiceRowLocks.InTransactionAsync(_db, async () =>
            await InvoiceRowLocks.LockOneAsync(_db, uuid) && await work(), _demand);

    /// <summary>
    /// Item A of the hardening: the purchase order's row lock (a no-op UPDATE of its status), taken after the
    /// invoice's — always in that order — inside the transition's transaction, which Demand's context has joined.
    /// Two approvals or reversals of different invoices of one PO used to read its invoiced quantities side by side,
    /// each add or take off its own and write the result back: the second write lost the first. Now the second waits
    /// here until the first has committed, and then reads what it wrote. Any copy of the PO this context was tracking
    /// is forgotten, so it is read afresh. Only on a relational provider.
    /// </summary>
    private async Task LockPurchaseOrderAsync(Guid poUuid)
    {
        if (!_demand.Database.IsRelational()) return;

        var organizationId = _demand.TenantContext.OrganizationId;
        await _demand.PurchaseOrders
            .Where(p => p.UUID == poUuid && !p.IsDelete && p.OrganizationId == organizationId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, p => p.Status));

        var stalePo = _demand.ChangeTracker.Entries<PurchaseOrder>().Where(e => e.Entity.UUID == poUuid).ToList();
        var poIds   = stalePo.Select(e => e.Entity.Id).ToHashSet();
        foreach (var line in _demand.ChangeTracker.Entries<PurchaseOrderLine>().Where(e => poIds.Contains(e.Entity.PurchaseOrderId)).ToList())
            line.State = EntityState.Detached;
        foreach (var po in stalePo)
            po.State = EntityState.Detached;
    }

    /// <summary>400 instead of a database error when a value is longer than its column.</summary>
    private static void CheckLength(string? value, int max, string what)
    {
        if (value is not null && value.Length > max)
            throw new BadRequestException($"{what} can be at most {max} characters.");
    }

    /// <summary>
    /// The supplier's name when there is no purchase order to take it from (G10). Denormalised onto
    /// the invoice for the same reason the PO path denormalises it: a payable has to stay readable
    /// after the supplier row changes underneath it.
    /// </summary>
    private async Task<string> SupplierNameAsync(Guid supplierId)
    {
        var names = await _supplierNames.GetNamesAsync([supplierId]);

        return names.TryGetValue(supplierId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : throw new BadRequestException(
                "That supplier does not exist, so this invoice would name nobody to pay.");
    }

    public async Task<Guid> CreateAsync(CreateInvoiceRequest req, int createdBy)
    {
        CheckLength(req.SupplierInvoiceNo?.Trim(), SupplierInvoiceNoMaxLength, "The supplier's invoice number");
        CheckLength(req.PaymentMethod?.Trim(),     PaymentMethodMaxLength,     "The payment method");
        CheckLength(req.Notes?.Trim(),             NotesMaxLength,             "The notes");
        CheckLength(req.AttachmentUrl?.Trim(),     AttachmentUrlMaxLength,     "The attachment link");

        // Optional since G10. A payable raised from a purchase order still has to find it — a
        // PoUuid that matches nothing is a mistake, not a freight bill — but an invoice that never
        // claimed to have one is now legitimate. This organization's own PO and GRN only: a super admin
        // reads every organization's (the tenant filter lets them), but an invoice of theirs must never
        // copy, match against or later move another organization's purchase order.
        var demandOrg = _demand.TenantContext.OrganizationId;
        var po = req.PoUuid is { } poUuid
            ? await _demand.PurchaseOrders.AsNoTracking().Include(p => p.Lines)
                  .FirstOrDefaultAsync(p => p.UUID == poUuid && !p.IsDelete && p.OrganizationId == demandOrg)
                  ?? throw new NotFoundException("PurchaseOrder", poUuid)
            : null;

        var warehouseOrg = _warehouse.TenantContext.OrganizationId;
        var grn = req.GrnUuid is { } grnUuid
            ? await _warehouse.Grns.AsNoTracking().Include(g => g.Lines)
                  .FirstOrDefaultAsync(g => g.UUID == grnUuid && !g.IsDelete && g.OrganizationId == warehouseOrg)
            : null;

        // The supplier billed is the one the purchase order was placed with: the invoice takes the PO's supplier
        // name, so another supplier's id would book the debit to one supplier under the other's name.
        if (po is not null && req.SupplierId != Guid.Empty && req.SupplierId != po.SupplierId)
            throw new BadRequestException(
                $"Purchase order {po.PoNumber} was placed with {po.SupplierName}; an invoice against it is that supplier's.");
        var supplierId = po?.SupplierId ?? req.SupplierId;

        // When lines are provided, compute subtotal from them — each line rounded to the cent as it is stored
        // (decimal(18,2)), so the Subtotal is the sum of the stored line totals and the tax below is worked out
        // from the Subtotal that is stored, not from a figure with more decimals than the column keeps.
        var subtotal = req.Lines?.Count > 0
            ? req.Lines.Sum(l => Money(l.QtyInvoiced * l.UnitPrice))
            : Money(req.Subtotal);

        // S-3: with a purchase tax code the server works the tax out from the net subtotal and the code's
        // rate, and the amount the client sent is ignored — the stored figure is the one the code implies,
        // and the detail the client reads back shows it. Without one, the tax is the amount entered.
        var taxCode     = await ResolvePurchaseTaxCodeAsync(req.TaxCodeUuid);
        var taxAmount   = taxCode is null ? Money(req.TaxAmount) : TaxFor(subtotal, taxCode.RatePercent);
        var totalAmount = subtotal + taxAmount;

        if (totalAmount <= 0)
            throw new BadRequestException("Invoice total must be greater than zero.");

        // 3-way match (S-8: on the net Subtotal; hardening item C: against what this invoice bills).
        var (matchedPoValue, matchedGrnValue, matchStatus) = ThreeWayMatch(subtotal, req.Lines, po, grn);
        var variance = po is null ? 0m : subtotal - matchedPoValue;

        // A35 D-14 — the currency asked for by id, else the purchase order's, else the code given.
        var (currencyCode, currencyId) = await ResolveInvoiceCurrencyAsync(req, po);

        var now           = DateTime.UtcNow;
        var invoiceNumber = await GenerateInvoiceNumberAsync(now.Year);

        var e = new Invoice
        {
            UUID              = req.InvoiceUuid is { } gid && gid != Guid.Empty ? gid : Guid.NewGuid(),
            // Its own chain when there is no purchase order to hang off — the document timeline
            // still needs a root, and sharing one with an unrelated PO would be worse than a new one.
            TraceId           = po?.TraceId ?? Guid.NewGuid(),
            InvoiceNumber     = invoiceNumber,
            SupplierInvoiceNo = req.SupplierInvoiceNo?.Trim(),
            SupplierId        = supplierId,
            SupplierName      = po?.SupplierName ?? await SupplierNameAsync(supplierId),
            PoUuid            = po?.UUID,
            PoNumber          = po?.PoNumber,
            // Only a GRN of this organization's that was found — never a reference to one it may not read.
            GrnUuid           = grn?.UUID,
            InvoiceDate       = req.InvoiceDate,
            ReceivedDate      = req.ReceivedDate,
            DueDate           = req.DueDate,
            Currency          = currencyCode,
            CurrencyId        = currencyId,
            Subtotal          = subtotal,
            TaxAmount         = taxAmount,
            TaxCodeUuid       = taxCode?.Uuid,
            TaxCode           = taxCode?.Code,
            TaxPercent        = taxCode?.RatePercent,
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

        e.GrnNumber = grn?.GrnNumber;

        // Persist invoice lines if provided
        if (req.Lines?.Count > 0)
        {
            int lineNo = 1;
            foreach (var lr in req.Lines)
            {
                var lineTotal = Money(lr.QtyInvoiced * lr.UnitPrice);
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
        {
            // The legacy single-payment flow writes Unpaid/Partial/Paid, supplier payments (SFM-004) write
            // UNPAID/PARTIALLY_PAID/FULLY_PAID/OVERPAID: a filter for one means the other as well.
            var statuses = PaymentStatusesMeaning(filter.PaymentStatus.Trim());
            q = q.Where(x => statuses.Contains(x.PaymentStatus));
        }
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
                TaxCode           = x.TaxCode,
                TaxPercent        = x.TaxPercent,
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

        var debitNotes = await _db.DebitNotes
            .Where(d => d.AppliedToInvoiceUuid == inv.UUID && !d.IsDelete)
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
            }).ToListAsync();

        var creditNotes = await _db.CreditNotes
            .Where(c => c.AppliedToInvoiceUuid == inv.UUID && !c.IsDelete)
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
            }).ToListAsync();

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
            TaxCodeUuid       = inv.TaxCodeUuid,
            TaxCode           = inv.TaxCode,
            TaxPercent        = inv.TaxPercent,
            ExchangeRate      = inv.ExchangeRate,
            BaseCurrencyCode  = inv.BaseCurrencyCode,
            BaseTotalAmount   = inv.BaseTotalAmount,
            CurrencyId           = inv.CurrencyId,
            BaseCurrencyId       = inv.BaseCurrencyId,
            ExchangeRateLockedAt = inv.ExchangeRateLockedAt,
            MatchedPoValue    = inv.MatchedPoValue,
            MatchedGrnValue   = inv.MatchedGrnValue,
            VarianceAmount    = inv.VarianceAmount,
            MatchStatus       = inv.MatchStatus,
            PaymentStatus     = inv.PaymentStatus,
            PaidAmount        = inv.PaidAmount,
            PaymentMethod     = inv.PaymentMethod,
            ApprovedBy        = inv.ApprovedBy,
            ApprovedAt        = inv.ApprovedAt,
            ReversedAt        = inv.ReversedAt,
            ReversedBy        = inv.ReversedBy,
            ReversalReason    = inv.ReversalReason,
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
                }).ToList(),
            DebitNotes  = debitNotes,
            CreditNotes = creditNotes,
            // A35 E-06 — supplier-payment lines with their realized differences, and the net booked on this invoice.
            SupplierPayments = await _db.SupplierPaymentLines.AsNoTracking()
                .Where(l => l.InvoiceUuid == inv.UUID && l.SupplierPayment.OrganizationId == inv.OrganizationId)
                .OrderBy(l => l.SupplierPayment.PaymentDate).ThenBy(l => l.Id)
                .Select(l => new InvoiceSupplierPaymentModel
                {
                    PaymentUuid        = l.SupplierPayment.UUID,
                    PaymentNumber      = l.SupplierPayment.PaymentNumber,
                    PaymentDate        = l.SupplierPayment.PaymentDate,
                    PaymentMethod      = l.SupplierPayment.PaymentMethod,
                    Status             = l.SupplierPayment.Status,
                    CurrencyCode       = l.SupplierPayment.CurrencyCode,
                    AllocatedAmount    = l.AllocatedAmount,
                    ExchangeDifference = l.ExchangeDifference
                }).ToListAsync(),
            RealizedExchangeDifference = await RealizedDifferenceAsync(inv)
        };
    }

    /// <summary>Net REALIZED difference booked on the invoice (reversals included); null when none.</summary>
    private async Task<decimal?> RealizedDifferenceAsync(Invoice inv)
    {
        var rows = await _db.ExchangeDifferences.AsNoTracking()
            .Where(d => d.OrganizationId == inv.OrganizationId && d.Kind == ExchangeDifferenceKinds.Realized
                     && d.DocumentType == ExchangeDifferenceRefs.SupplierInvoice && d.DocumentUuid == inv.UUID)
            .Select(d => d.DifferenceBase).ToListAsync();
        return rows.Count == 0 ? null : rows.Sum();
    }

    /// <summary>
    /// Changes what may still change (S-7: reverse, don't edit). Everything is checked before anything is
    /// applied, and a field sent with the value it already has is not a change — a client that resends a
    /// whole form is not refused for the parts it did not touch.
    /// <list type="bullet">
    /// <item><b>Reversed</b>: only Notes and AttachmentUrl (409 otherwise).</item>
    /// <item><b>Approved</b>: no tax, tax code or match status change — the supplier ledger already holds
    /// the total (409, "reverse the invoice instead"). Due date, payment method, notes, attachment and the
    /// supplier's number may change.</item>
    /// <item><b>MatchStatus</b>: Pending, Matched or Variance only. Approved, Rejected and Reversed have their
    /// own actions, always (409); and no change once Approved or Rejected (409). Tax no longer moves it:
    /// the match is on the net Subtotal (S-8), which tax does not change.</item>
    /// <item><b>PaymentStatus</b>: Unpaid or Scheduled only (400 otherwise — the rest follows the payments),
    /// and not once a payment is recorded against the invoice (409).</item>
    /// <item><b>Tax / SupplierInvoiceNo</b>: not once a payment is recorded (409) — it was paid against that
    /// total, under that number.</item>
    /// <item><b>Tax code</b>: a code recomputes TaxAmount from the Subtotal and the amount sent is ignored;
    /// <c>Guid.Empty</c> removes the code (the tax is then the amount sent, or stays as it is); a bare
    /// TaxAmount on an invoice whose tax comes from a code is refused (400). TotalAmount moves by the change
    /// in tax, so a credit or debit note already deducted from it stays deducted.</item>
    /// <item>A value longer than its column is a 400. Another organization's invoice is not found.</item>
    /// </list>
    /// One change at a time with any other change to the same invoice (<see cref="OneAtATimeAsync"/>).
    /// </summary>
    public async Task<bool> PatchAsync(Guid uuid, PatchInvoiceRequest req, int modifiedBy)
    {
        CheckLength(req.SupplierInvoiceNo?.Trim(), SupplierInvoiceNoMaxLength, "The supplier's invoice number");
        CheckLength(req.PaymentMethod?.Trim(),     PaymentMethodMaxLength,     "The payment method");
        CheckLength(req.Notes?.Trim(),             NotesMaxLength,             "The notes");
        CheckLength(req.AttachmentUrl?.Trim(),     AttachmentUrlMaxLength,     "The attachment link");

        return await OneAtATimeAsync(uuid, () => PatchCoreAsync(uuid, req, modifiedBy));
    }

    private async Task<bool> PatchCoreAsync(Guid uuid, PatchInvoiceRequest req, int modifiedBy)
    {
        var e = await OwnInvoices.FirstOrDefaultAsync(x => x.UUID == uuid);
        if (e is null) return false;

        var approved = InvoiceMatchStatus.Is(e.MatchStatus, InvoiceMatchStatus.Approved);
        var rejected = InvoiceMatchStatus.Is(e.MatchStatus, InvoiceMatchStatus.Rejected);
        var reversed = InvoiceMatchStatus.Is(e.MatchStatus, InvoiceMatchStatus.Reversed);

        // ── What would change ──────────────────────────────────────────────────

        var supplierNo       = req.SupplierInvoiceNo?.Trim();
        var supplierNoChange = supplierNo is not null && supplierNo != (e.SupplierInvoiceNo ?? string.Empty);
        var dueDateChange    = req.DueDate is { } due && due != e.DueDate;
        var method           = req.PaymentMethod?.Trim();
        var methodChange     = method is not null && method != (e.PaymentMethod ?? string.Empty);

        string? matchStatus = null;
        if (req.MatchStatus is not null)
        {
            var target = InvoiceMatchStatus.Canonical(req.MatchStatus)
                ?? throw new BadRequestException(
                    $"'{req.MatchStatus}' is not a match status. An invoice's match can be set to Pending, Matched or Variance.");

            if (target == InvoiceMatchStatus.Approved)
                throw new ConflictException(
                    "An invoice is approved with its Approve action, which also books it to the supplier ledger — not by editing its match status.");
            if (target == InvoiceMatchStatus.Rejected)
                throw new ConflictException(
                    "An invoice is rejected with its Reject action, which records why — not by editing its match status.");
            if (target == InvoiceMatchStatus.Reversed)
                throw new ConflictException(
                    "An approved invoice is reversed with its Reverse action, which posts the opposite ledger entry — not by editing its match status.");

            if (!InvoiceMatchStatus.Is(e.MatchStatus, target)) matchStatus = target;
        }

        string? paymentStatus = null;
        if (req.PaymentStatus is not null)
        {
            var target = ManualPaymentStatus(req.PaymentStatus)
                ?? throw new BadRequestException(
                    "An invoice's payment status follows the payments recorded against it. Only Unpaid or Scheduled can be set by hand.");

            if (!string.Equals(target, e.PaymentStatus, StringComparison.OrdinalIgnoreCase)) paymentStatus = target;
        }

        // Tax: apply a different code, remove the code (Guid.Empty), or a different hand-entered amount.
        // The same code again keeps its snapshot — re-picking it does not pick up a changed master rate.
        Guid? codeToApply = null;
        var   removeCode  = false;
        decimal? manualTax = null;
        if (req.TaxCodeUuid is { } codeUuid && codeUuid != Guid.Empty)
        {
            if (codeUuid != e.TaxCodeUuid) codeToApply = codeUuid;
        }
        else if (req.TaxCodeUuid == Guid.Empty && e.TaxCodeUuid is not null)
        {
            removeCode = true;
            manualTax  = Money(req.TaxAmount ?? e.TaxAmount);
        }
        else if (req.TaxAmount is { } amount && Money(amount) != e.TaxAmount)
        {
            manualTax = Money(amount);
        }
        var taxChange = codeToApply is not null || removeCode || manualTax is not null;

        // ── What may not change ────────────────────────────────────────────────

        if (reversed && (supplierNoChange || dueDateChange || methodChange || taxChange || matchStatus is not null || paymentStatus is not null))
            throw new ConflictException(
                $"Invoice {e.InvoiceNumber} has been reversed. Only its notes and attachment can still change.");

        if (approved && taxChange)
            throw new ConflictException(
                $"Invoice {e.InvoiceNumber} is approved, so its tax can no longer change — the supplier ledger already holds its total. "
              + "Reverse the invoice instead, then enter it again.");

        if (approved && matchStatus is not null)
            throw new ConflictException(
                $"Invoice {e.InvoiceNumber} is approved, so its match status can no longer change. Reverse the invoice instead.");

        if (rejected && matchStatus is not null)
            throw new ConflictException(
                $"Invoice {e.InvoiceNumber} has been rejected, so its match status can no longer change.");

        if (manualTax is not null && !removeCode && e.TaxCodeUuid is not null)
            throw new BadRequestException(
                $"Invoice {e.InvoiceNumber}'s tax comes from tax code {e.TaxCode} ({e.TaxPercent:0.##}%). "
              + "Choose another code, or remove the code to enter the tax as an amount.");

        if ((taxChange || supplierNoChange || paymentStatus is not null) && await HasPaymentsAsync(e))
            throw new ConflictException(
                taxChange        ? $"Payments have been recorded against invoice {e.InvoiceNumber}, so its tax can no longer change — they were made against its total."
              : supplierNoChange ? $"Payments have been recorded against invoice {e.InvoiceNumber}, so the supplier's invoice number they were made against can no longer change."
              :                    $"Payments have been recorded against invoice {e.InvoiceNumber}, so its payment status now follows them.");

        var taxCode = codeToApply is null ? null : await ResolvePurchaseTaxCodeAsync(codeToApply);

        // ── Apply ──────────────────────────────────────────────────────────────

        if (taxChange)
        {
            var oldTax = e.TaxAmount;

            if (taxCode is not null)
            {
                e.TaxCodeUuid = taxCode.Uuid;
                e.TaxCode     = taxCode.Code;
                e.TaxPercent  = taxCode.RatePercent;
                e.TaxAmount   = TaxFor(e.Subtotal, taxCode.RatePercent);
            }
            else
            {
                if (removeCode)
                {
                    e.TaxCodeUuid = null;
                    e.TaxCode     = null;
                    e.TaxPercent  = null;
                }
                e.TaxAmount = manualTax!.Value;
            }

            // By the change, not Subtotal + Tax: a credit or debit note already deducted from the total
            // stays deducted. The match status and variance stay too — they are on the Subtotal (S-8).
            e.TotalAmount += e.TaxAmount - oldTax;

            if (e.TotalAmount <= 0)
                throw new BadRequestException("Invoice total must be greater than zero.");
        }

        if (supplierNoChange)          e.SupplierInvoiceNo = supplierNo;
        if (dueDateChange)             e.DueDate           = req.DueDate!.Value;
        if (methodChange)              e.PaymentMethod     = method;
        if (matchStatus   is not null) e.MatchStatus       = matchStatus;
        if (paymentStatus is not null) e.PaymentStatus     = paymentStatus;
        if (req.Notes         is not null) e.Notes         = req.Notes.Trim();
        if (req.AttachmentUrl is not null) e.AttachmentUrl = req.AttachmentUrl.Trim();

        e.ModifiedBy   = modifiedBy;
        e.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Every stored spelling of the payment status a list filter asks for.</summary>
    private static List<string> PaymentStatusesMeaning(string status) => status.ToUpperInvariant() switch
    {
        "PAID" or InvoicePaymentStatus.FullyPaid      => ["Paid", InvoicePaymentStatus.FullyPaid, InvoicePaymentStatus.Overpaid],
        "PARTIAL" or InvoicePaymentStatus.PartiallyPaid => ["Partial", InvoicePaymentStatus.PartiallyPaid],
        "UNPAID"                                       => ["Unpaid", InvoicePaymentStatus.Unpaid],
        _                                              => [status]
    };

    /// <summary>The payment statuses a person may set: a scheduling marker, never a claim that money moved.</summary>
    private static string? ManualPaymentStatus(string? status) =>
        string.Equals(status?.Trim(), "Unpaid", StringComparison.OrdinalIgnoreCase)    ? "Unpaid"
      : string.Equals(status?.Trim(), "Scheduled", StringComparison.OrdinalIgnoreCase) ? "Scheduled"
      : null;

    /// <summary>
    /// Books a Pending, Matched or Variance invoice: status Approved, the exchange-rate snapshot (S-5), the
    /// purchase order's invoiced quantities and an INVOICE_APPROVED debit — all in one transaction on SQL Server,
    /// one change at a time with any other change to the same invoice, so it is booked once however many approvals
    /// arrive together (<see cref="OneAtATimeAsync"/>). Notes longer than the column are a 400.
    /// </summary>
    public async Task<bool> ApproveAsync(Guid uuid, string? notes, int approvedBy)
    {
        CheckLength(notes?.Trim(), NotesMaxLength, "The notes");
        return await OneAtATimeAsync(uuid, () => ApproveCoreAsync(uuid, notes, approvedBy));
    }

    private async Task<bool> ApproveCoreAsync(Guid uuid, string? notes, int approvedBy)
    {
        var inv = await OwnInvoices
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.UUID == uuid);
        if (inv is null) return false;

        // S-7: once only. A second approval used to post a second INVOICE_APPROVED debit and add the
        // invoiced quantities to the purchase order a second time.
        if (!InvoiceMatchStatus.IsApprovable(inv.MatchStatus))
            throw new ConflictException(InvoiceMatchStatus.Is(inv.MatchStatus, InvoiceMatchStatus.Approved)
                ? $"Invoice {inv.InvoiceNumber} is already approved. Approving it again would book it to the supplier ledger twice."
                : $"Invoice {inv.InvoiceNumber} is {inv.MatchStatus}, so it cannot be approved. Only a Pending, Matched or Variance invoice can be.");

        if (_currency is not null)
        {
            // A35 D-12 — locked now, against the PURCHASE base, at the approval date. Foreign with no rate: 400 (D-5),
            // and nothing of the approval is saved.
            await LockExchangeRateAsync(inv, DateTime.UtcNow);
        }
        else
        {
            // S-5: the rate is fixed now, when the invoice is booked. A missing rate never stops the approval.
            await SnapshotExchangeRateAsync(inv);
        }

        inv.MatchStatus  = InvoiceMatchStatus.Approved;
        inv.ApprovedBy   = approvedBy;
        inv.ApprovedAt   = DateTime.UtcNow;
        // Blank notes leave the invoice's own notes as they are — an empty approval box is not "erase them".
        if (!string.IsNullOrWhiteSpace(notes)) inv.Notes = notes.Trim();
        inv.ModifiedBy   = approvedBy;
        inv.ModifiedDate = DateTime.UtcNow;

        // Update PO line QtyInvoiced — prefer invoice lines (precise), fall back to GRN lines.
        // Skipped entirely for an invoice with no purchase order (G10): there is nothing whose
        // invoiced quantity this could advance. On SQL Server Demand's save is in this approval's
        // transaction (OneAtATimeAsync); it goes first so the ledgers — which every posting in the
        // organization appends to — are held for as short a time as possible.
        await MoveQtyInvoicedAsync(inv, factor: 1m, approvedBy);

        // SFM-002: the ledger debit is posted in the SAME SaveChangesAsync as the invoice
        // approval itself (PostEntryAsync performs that save) — if it throws, none of the
        // invoice's tracked changes above have been persisted either, and on SQL Server the
        // transaction takes the PO's quantities back with it. Do not call _db.SaveChangesAsync()
        // separately here.
        await _ledger.PostEntryAsync(
            inv.SupplierId, ApprovedLedgerType, "Invoice", inv.UUID, inv.InvoiceNumber,
            debitAmount: inv.TotalAmount, creditAmount: 0m,
            narration: $"Invoice {inv.InvoiceNumber} approved.", createdBy: approvedBy,
            supplierName: inv.SupplierName);

        return true;
    }

    /// <summary>
    /// Rejects an invoice that was never booked, with the reason (kept in Notes, at most 300). Refused (409) once it
    /// is Approved (reverse it instead), Reversed, already Rejected, or has a payment recorded against it — a rejected
    /// invoice is owed nothing, so it would leave that money paid against nothing. One change at a time with any other
    /// change to the same invoice (<see cref="OneAtATimeAsync"/>).
    /// </summary>
    public async Task<bool> RejectAsync(Guid uuid, string reason, int rejectedBy)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why))
            throw new BadRequestException("Say why the invoice is being rejected.");
        if (why.Length > NotesMaxLength)
            throw new BadRequestException($"The reason can be at most {NotesMaxLength} characters.");

        return await OneAtATimeAsync(uuid, () => RejectCoreAsync(uuid, why, rejectedBy));
    }

    private async Task<bool> RejectCoreAsync(Guid uuid, string why, int rejectedBy)
    {
        var e = await OwnInvoices.FirstOrDefaultAsync(x => x.UUID == uuid);
        if (e is null) return false;

        // S-7: an approved invoice is booked; un-booking it is a reversal, which posts the opposite entry.
        if (InvoiceMatchStatus.Is(e.MatchStatus, InvoiceMatchStatus.Approved))
            throw new ConflictException(
                $"Invoice {e.InvoiceNumber} is approved and booked to the supplier ledger. Reverse it instead of rejecting it.");
        if (InvoiceMatchStatus.Is(e.MatchStatus, InvoiceMatchStatus.Reversed))
            throw new ConflictException(
                $"Invoice {e.InvoiceNumber} has been reversed; there is nothing left to reject.");
        if (InvoiceMatchStatus.Is(e.MatchStatus, InvoiceMatchStatus.Rejected))
            throw new ConflictException(
                $"Invoice {e.InvoiceNumber} has already been rejected.");

        var payments = await LivePaymentNumbersAsync(e);
        if (e.PaidAmount != 0m || payments.Count > 0)
            throw new ConflictException(payments.Count > 0
                ? $"Invoice {e.InvoiceNumber} is on payment {string.Join(", ", payments)}. Cancel that payment, or take the invoice off it, before rejecting the invoice."
                : $"Invoice {e.InvoiceNumber} has {e.PaidAmount:N2} {e.Currency} paid against it, so it cannot be rejected.");

        e.MatchStatus  = InvoiceMatchStatus.Rejected;
        e.Notes        = why;
        e.ModifiedBy   = rejectedBy;
        e.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// S-7 — reverse, don't edit. An approved invoice that nothing has been paid against gets the opposite
    /// of what its approval posted, in one Finance save: a supplier ledger CREDIT "INVOICE_REVERSED" for the
    /// amount the approval debited (mirrored to the master ledger by <see cref="ISupplierLedgerService"/>),
    /// status Reversed, and who/when/why. As approval does, the purchase order lines' QtyInvoiced go back down
    /// and the PO status follows (Demand's save, in the same transaction on SQL Server).
    /// <para>
    /// Refused (409) unless Approved; when anything is paid (PaidAmount, a supplier payment that is not
    /// cancelled or bounced, a legacy payment that is not reversed); and when a credit or debit note has been
    /// applied to it — reversing it would leave that note deducted from an invoice that no longer stands.
    /// </para>
    /// <para>
    /// The amount is read from the ledger, not the invoice: the sum of its INVOICE_APPROVED debits. That is
    /// what it owes the ledger, and it undoes an invoice the old re-approval bug debited twice — whose PO
    /// quantities were added twice, so they come off as many times.
    /// </para>
    /// <para>
    /// One change at a time with any other change to the same invoice (<see cref="OneAtATimeAsync"/>): on SQL Server
    /// the reversal, its ledger entries and the PO's quantities are one transaction, so two reversals credit it once
    /// and a reversal never interleaves with the approval it undoes. Another organization's invoice is not found.
    /// </para>
    /// </summary>
    public async Task<bool> ReverseAsync(Guid uuid, string reason, int reversedBy)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why))
            throw new BadRequestException("Say why the invoice is being reversed — the reason is kept on the invoice and in the ledger.");
        if (why.Length > ReversalReasonMaxLength)
            throw new BadRequestException($"The reason can be at most {ReversalReasonMaxLength} characters.");

        return await OneAtATimeAsync(uuid, () => ReverseCoreAsync(uuid, why, reversedBy));
    }

    private async Task<bool> ReverseCoreAsync(Guid uuid, string why, int reversedBy)
    {
        var inv = await OwnInvoices
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.UUID == uuid);
        if (inv is null) return false;

        if (InvoiceMatchStatus.Is(inv.MatchStatus, InvoiceMatchStatus.Reversed))
            throw new ConflictException($"Invoice {inv.InvoiceNumber} has already been reversed.");
        if (!InvoiceMatchStatus.Is(inv.MatchStatus, InvoiceMatchStatus.Approved))
            throw new ConflictException(
                $"Only an approved invoice can be reversed. Invoice {inv.InvoiceNumber} is {inv.MatchStatus}, so nothing has been booked to the supplier ledger for it.");

        if (inv.PaidAmount != 0m)
            throw new ConflictException(
                $"Invoice {inv.InvoiceNumber} has {inv.PaidAmount:N2} {inv.Currency} paid against it. Only an unpaid invoice can be reversed.");

        var payments = await LivePaymentNumbersAsync(inv);
        if (payments.Count > 0)
            throw new ConflictException(
                $"Invoice {inv.InvoiceNumber} is on payment {string.Join(", ", payments)}. Only an unpaid invoice can be reversed — "
              + "cancel that payment, or take the invoice off it, first.");

        var notes = await _db.CreditNotes
            .Where(c => c.AppliedToInvoiceUuid == inv.UUID && !c.IsDelete)
            .Select(c => c.CreditNoteNumber)
            .ToListAsync();
        notes.AddRange(await _db.DebitNotes
            .Where(d => d.AppliedToInvoiceUuid == inv.UUID && !d.IsDelete)
            .Select(d => d.DebitNoteNumber)
            .ToListAsync());
        if (notes.Count > 0)
            throw new ConflictException(
                $"{string.Join(", ", notes)} {(notes.Count == 1 ? "has" : "have")} been deducted from invoice {inv.InvoiceNumber}. "
              + "Reversing it would leave that deduction on an invoice that no longer stands, so it cannot be reversed.");

        var approvalDebits = await _db.SupplierLedgerEntries
            .Where(x => x.SupplierId == inv.SupplierId && x.ReferenceId == inv.UUID && x.TransactionType == ApprovedLedgerType)
            .Select(x => x.DebitAmount)
            .ToListAsync();
        var credit = approvalDebits.Sum();
        var times  = Math.Max(1, approvalDebits.Count);

        var now = DateTime.UtcNow;
        inv.MatchStatus    = InvoiceMatchStatus.Reversed;
        inv.ReversedAt     = now;
        inv.ReversedBy     = reversedBy;
        inv.ReversalReason = why;
        inv.ModifiedBy     = reversedBy;
        inv.ModifiedDate   = now;

        // The PO's quantities first, as approval does: on SQL Server in the same transaction.
        await MoveQtyInvoicedAsync(inv, factor: -times, reversedBy);

        if (credit > 0m)
        {
            // Same single save as approval: the reversal and its ledger entry commit together, or neither.
            var narration = $"Invoice {inv.InvoiceNumber} reversed: {why}";
            await _ledger.PostEntryAsync(
                inv.SupplierId, ReversedLedgerType, "Invoice", inv.UUID, inv.InvoiceNumber,
                debitAmount: 0m, creditAmount: credit,
                narration: narration.Length > 500 ? narration[..500] : narration, createdBy: reversedBy,
                supplierName: inv.SupplierName);
        }
        else
        {
            // Approved before approvals were posted to the supplier ledger: there is nothing to undo there.
            _log?.LogWarning(
                "Supplier invoice {InvoiceNumber} ({InvoiceUuid}) has no INVOICE_APPROVED ledger entry, so it was reversed without a ledger entry.",
                inv.InvoiceNumber, inv.UUID);
            await _db.SaveChangesAsync();
        }

        return true;
    }

    /// <summary>
    /// Moves the purchase order lines' QtyInvoiced by what <paramref name="inv"/> bills, <paramref name="factor"/>
    /// times — +1 when it is approved, −n when it is reversed — then the PO status, in Demand's save (which
    /// <see cref="OneAtATimeAsync"/> puts in the invoice's transaction on SQL Server).
    /// Prefers the invoice's lines (exact), falls back to the GRN's accepted quantities. Nothing for an
    /// invoice with no purchase order (G10).
    /// </summary>
    private async Task MoveQtyInvoicedAsync(Invoice inv, decimal factor, int userId)
    {
        if (inv.PoUuid is not { } poUuid) return;

        // Locked before it is read (after the invoice — the one order), so it is read as committed now.
        await LockPurchaseOrderAsync(poUuid);

        // This organization's PO only: an invoice never moves another organization's purchase order.
        var demandOrg = _demand.TenantContext.OrganizationId;
        var po = await _demand.PurchaseOrders
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.UUID == poUuid && !p.IsDelete && p.OrganizationId == demandOrg);
        if (po is null) return;

        void Move(Guid? poLineUuid, decimal qty)
        {
            var poLine = po.Lines.FirstOrDefault(l => l.UUID == poLineUuid);
            if (poLine is not null)
                poLine.QtyInvoiced = Math.Max(0m, poLine.QtyInvoiced + factor * qty);
        }

        if (inv.Lines.Count > 0)
        {
            // Line-level invoicing: use exact QtyInvoiced per PO line
            foreach (var invLine in inv.Lines.Where(l => l.PoLineUuid.HasValue))
                Move(invLine.PoLineUuid, invLine.QtyInvoiced);
        }
        else if (inv.GrnUuid.HasValue)
        {
            // Fallback: no invoice lines — derive from GRN accepted quantities (this organization's GRN only)
            var warehouseOrg = _warehouse.TenantContext.OrganizationId;
            var grn = await _warehouse.Grns
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.UUID == inv.GrnUuid && !g.IsDelete && g.OrganizationId == warehouseOrg);

            if (grn is not null)
                foreach (var grnLine in grn.Lines)
                    Move(grnLine.PoLineUuid, grnLine.QtyAccepted);
        }

        // Approval always recomputes the status. A reversal only undoes what approval could have set —
        // a PO that is, say, CANCELLED stays so.
        if (factor > 0 || po.Status is "CLOSED" or "PARTIALLY_INVOICED")
            po.Status = PoStatusAfterInvoicing(po, reversing: factor < 0);

        po.ModifiedBy   = userId;
        po.ModifiedDate = DateTime.UtcNow;
        await _demand.SaveChangesAsync();
    }

    /// <summary>
    /// Invoice progress takes priority over receipt. Approval's rule, unchanged; a reversal that leaves
    /// nothing invoiced and nothing received puts the PO back to SENT rather than claiming a receipt.
    /// </summary>
    private static string PoStatusAfterInvoicing(PurchaseOrder po, bool reversing)
    {
        var allInvoiced = po.Lines.All(l => l.QtyInvoiced >= l.Quantity);
        var anyInvoiced = po.Lines.Any(l => l.QtyInvoiced > 0);
        var allReceived = po.Lines.All(l => l.QtyReceived >= l.Quantity);

        return allInvoiced ? "CLOSED"
            : anyInvoiced ? "PARTIALLY_INVOICED"
            : allReceived ? "RECEIVED"
            : !reversing || po.Lines.Any(l => l.QtyReceived > 0) ? "PARTIALLY_RECEIVED"
            : "SENT";
    }

    /// <summary>
    /// Supplier payments (not cancelled or bounced) with a line for this invoice, and legacy single-invoice
    /// payments (not reversed) — by number, for the message that refuses something because of them.
    /// </summary>
    private async Task<List<string>> LivePaymentNumbersAsync(Invoice inv)
    {
        var numbers = await _db.SupplierPaymentLines
            .Where(l => l.InvoiceUuid == inv.UUID
                     && l.SupplierPayment.Status != "CANCELLED"
                     && l.SupplierPayment.Status != "BOUNCED")
            .Select(l => l.SupplierPayment.PaymentNumber)
            .Distinct()
            .ToListAsync();

        numbers.AddRange(await _db.Payments
            .Where(p => p.InvoiceId == inv.Id && !p.IsDelete && p.Status != "Reversed")
            .Select(p => p.PaymentNumber)
            .ToListAsync());

        return numbers;
    }

    private async Task<bool> HasPaymentsAsync(Invoice inv) =>
        inv.PaidAmount != 0m || (await LivePaymentNumbersAsync(inv)).Count > 0;

    /// <summary>
    /// S-3: the purchase tax code asked for, checked — it exists in this organization, is active and may be
    /// used on a purchase (PURCHASE or BOTH) — or 400. Null when none was asked for.
    /// </summary>
    private async Task<TaxCodeInfo?> ResolvePurchaseTaxCodeAsync(Guid? taxCodeUuid)
    {
        if (taxCodeUuid is not { } uuid || uuid == Guid.Empty) return null;

        if (_taxCodes is null)
            throw new BadRequestException("Tax codes are not available here; enter the tax as an amount instead.");

        var code = await _taxCodes.GetAsync(uuid)
            ?? throw new BadRequestException("That tax code does not exist.");

        if (!code.IsActive)
            throw new BadRequestException($"Tax code {code.Code} is inactive. Choose an active purchase tax code.");
        if (!TaxCodeUsage.Allows(code.Usage, TaxCodeUsage.Purchase))
            throw new BadRequestException($"Tax code {code.Code} is for sales only; it cannot be used on a supplier invoice.");

        return code;
    }

    /// <summary>TaxAmount = round(Subtotal × rate / 100, 2, away from zero) — how every Finance amount is rounded.</summary>
    internal static decimal TaxFor(decimal subtotal, decimal ratePercent) =>
        Math.Round(subtotal * ratePercent / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>An amount as its decimal(18,2) column keeps it: to the cent, half away from zero.</summary>
    internal static decimal Money(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// S-5: 1 unit of the invoice's currency in the organization's base currency, as of the invoice date —
    /// 1 when they are the same — and the total converted at it. The base currency is recorded whenever it is
    /// known; the rate and the base total stay null when no rate is on file (or anything here fails, which is
    /// logged): a missing rate never blocks an approval.
    /// </summary>
    private async Task SnapshotExchangeRateAsync(Invoice inv)
    {
        inv.ExchangeRate     = null;
        inv.BaseCurrencyCode = null;
        inv.BaseTotalAmount  = null;

        if (_orgCurrency is null || _currencyCodes is null) return;

        try
        {
            var baseId = await _orgCurrency.GetBaseCurrencyIdAsync(inv.OrganizationId);
            if (baseId is null) return;

            var baseCode = (await _currencyCodes.GetCodeAsync(baseId.Value))?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(baseCode)) return;
            inv.BaseCurrencyCode = baseCode;

            var currency = inv.Currency?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(currency)) return;

            var rate = currency == baseCode ? 1m
                : _rates is null ? (decimal?)null
                : (await _rates.GetRateAsync(currency, baseCode, inv.InvoiceDate))?.Rate;
            if (rate is not { } r) return;

            inv.ExchangeRate    = r;
            inv.BaseTotalAmount = ExchangeRateMath.Convert(inv.TotalAmount, r);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            inv.ExchangeRate    = null;
            inv.BaseTotalAmount = null;
            _log?.LogWarning(ex,
                "Supplier invoice {InvoiceNumber} ({InvoiceUuid}) is approved without an exchange-rate snapshot: the rate could not be read.",
                inv.InvoiceNumber, inv.UUID);
        }
    }

    /// <summary>
    /// A35 D-12 — the rate of the invoice's currency into the organization's purchase base at <paramref name="now"/>'s date:
    /// exactly 1 with no lookup when they are the same (BR-C5-04), the locked rate otherwise; base total rounded at the base's
    /// decimals (D-13). Throws the contract's 400 when no rate covers the date.
    /// </summary>
    private async Task LockExchangeRateAsync(Invoice inv, DateTime now)
    {
        var currencyId = inv.CurrencyId ?? CurrencyIdOf(inv.Currency)
            ?? throw new BadRequestException(
                $"Invoice {inv.InvoiceNumber} is in {inv.Currency}, which is not a currency in the Lookups catalog, so its exchange rate cannot be locked.");

        var lk = await _currency!.LockRateAsync(inv.OrganizationId, currencyId, DateOnly.FromDateTime(now), TransactionDomain.Purchase);
        inv.CurrencyId           = currencyId;
        inv.ExchangeRate         = lk.Rate;
        inv.BaseCurrencyId       = lk.BaseCurrencyId;
        inv.BaseCurrencyCode     = lk.BaseCurrencyCode;
        inv.BaseTotalAmount      = lk.ToBase(inv.TotalAmount);
        inv.ExchangeRateLockedAt = now;
    }

    /// <summary>D-14 — the currency of a new invoice: the id asked for, else the purchase order's, else the code given.</summary>
    private async Task<(string Code, Guid? Id)> ResolveInvoiceCurrencyAsync(CreateInvoiceRequest req, PurchaseOrder? po)
    {
        async Task<string?> CodeOf(Guid id) =>
            _lookups?.GetCurrencies().FirstOrDefault(c => c.Id == id)?.Code?.Trim().ToUpperInvariant()
            ?? (_currencyCodes is null ? null : (await _currencyCodes.GetCodeAsync(id))?.Trim().ToUpperInvariant());

        if (req.CurrencyId is { } asked && asked != Guid.Empty)
            return (await CodeOf(asked) ?? throw new BadRequestException("The invoice's currency is not a currency in the Lookups catalog."), asked);

        if (po?.CurrencyId is { } poCurrency && await CodeOf(poCurrency) is { } poCode)
            return (poCode, poCurrency);

        return (req.Currency, CurrencyIdOf(req.Currency));
    }

    private Guid? CurrencyIdOf(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null
        : _lookups?.GetCurrencies().FirstOrDefault(c => string.Equals(c.Code?.Trim(), code.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;

    public async Task<bool> UploadAttachmentAsync(Guid uuid, string url, int modifiedBy)
    {
        var e = await OwnInvoices.FirstOrDefaultAsync(x => x.UUID == uuid);
        if (e is null) return false;
        e.AttachmentUrl = url;
        e.ModifiedBy    = modifiedBy;
        e.ModifiedDate  = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>The three-way match's tolerance: the net Subtotal within 5% of what it should bill.</summary>
    private const decimal MatchTolerance = 0.05m;

    /// <summary>
    /// The three-way match (S-8: on the net Subtotal — the PO and GRN values are before tax; hardening item C:
    /// against what THIS invoice bills, the way SAP matches an invoice, not against the whole purchase order, which
    /// made every partial invoice a Variance). Returns the expected value (stored as MatchedPoValue), the GRN's
    /// accepted value (MatchedGrnValue, 0 without a GRN) and the status.
    /// <list type="bullet">
    /// <item><b>No purchase order</b>: Pending, nothing to match (G10) — a person has to look at it.</item>
    /// <item><b>Lines</b>: expected = Σ each line's quantity × its PO line's unit price; a line no PO line of this order
    /// stands behind adds nothing (so it shows as variance). Variance as well when a PO line is billed for more than
    /// was received and is not yet invoiced (QtyReceived − QtyInvoiced, which approvals advance), or — when the
    /// invoice names a GRN — more than that GRN accepted for it.</item>
    /// <item><b>A GRN but no lines</b>: expected = the GRN's accepted value.</item>
    /// <item><b>Neither</b>: expected = the PO total, as before.</item>
    /// </list>
    /// Matched when the Subtotal is within 5% of the expected value and nothing is over-billed.
    /// </summary>
    internal static (decimal Expected, decimal GrnValue, string Status) ThreeWayMatch(
        decimal subtotal, IReadOnlyList<InvoiceLineRequest>? lines, PurchaseOrder? po, SMS.Modules.Warehouse.Domain.Grn? grn)
    {
        var grnValue = grn is null ? 0m : Money(grn.Lines.Sum(l => l.QtyAccepted * (l.UnitCost ?? 0m)));
        if (po is null) return (0m, grnValue, InvoiceMatchStatus.Pending);

        decimal expected;
        var overBilled = false;

        if (lines is { Count: > 0 })
        {
            var poLines = po.Lines.ToDictionary(l => l.UUID);
            var billed  = lines
                .Where(l => l.PoLineUuid is { } id && poLines.ContainsKey(id))
                .GroupBy(l => l.PoLineUuid!.Value)
                .Select(g => (Line: poLines[g.Key], Qty: g.Sum(l => l.QtyInvoiced)))
                .ToList();

            expected = Money(billed.Sum(b => b.Qty * b.Line.UnitPrice));

            var acceptedOnGrn = grn?.Lines
                .GroupBy(l => l.PoLineUuid)
                .ToDictionary(g => g.Key, g => g.Sum(l => l.QtyAccepted));

            foreach (var (line, qty) in billed)
            {
                if (qty > line.QtyReceived - line.QtyInvoiced) overBilled = true;
                if (acceptedOnGrn is not null && qty > acceptedOnGrn.GetValueOrDefault(line.UUID)) overBilled = true;
            }
        }
        else
        {
            expected = grn is not null ? grnValue : po.TotalAmount;
        }

        var within = expected > 0m && Math.Abs(subtotal - expected) / expected <= MatchTolerance;
        return (expected, grnValue, within && !overBilled ? InvoiceMatchStatus.Matched : InvoiceMatchStatus.Variance);
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

    /// <summary>The legacy payment's statuses, and where each may go next. Reversed is final.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> LegacyTransitions = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Pending"]   = ["Processed", "Reversed"],
        ["Processed"] = ["Cleared", "Reversed"],
        ["Cleared"]   = ["Reversed"],
        ["Reversed"]  = []
    };

    /// <summary>
    /// Records a legacy single-invoice payment. One change at a time with anything else that changes the invoice
    /// (<see cref="InvoiceRowLocks"/>): a reversal cannot slip in between the checks below and the save. This
    /// organization's own invoice only — another organization's is not found, even for a super admin. Item E: only an
    /// Approved invoice is a payable (400 otherwise); a reversed or rejected one owes nothing (422, as before).
    /// </summary>
    public Task<Guid> CreateAsync(CreatePaymentRequest req, int createdBy)
    {
        if (req.AmountPaid <= 0)
            throw new BadRequestException("Payment amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(req.PaymentMethod))
            throw new BadRequestException("Payment method is required.");

        return InvoiceRowLocks.InTransactionAsync(_db, async () =>
        {
            await InvoiceRowLocks.LockOneAsync(_db, req.InvoiceUuid);
            return await CreateCoreAsync(req, createdBy);
        });
    }

    private async Task<Guid> CreateCoreAsync(CreatePaymentRequest req, int createdBy)
    {
        var invoice = await InvoiceRowLocks.Own(_db)
            .FirstOrDefaultAsync(i => i.UUID == req.InvoiceUuid)
            ?? throw new NotFoundException("Invoice", req.InvoiceUuid);

        // Either flow's "settled" — the supplier-payment flow writes FULLY_PAID/OVERPAID, which "Paid" alone missed.
        if (InvoicePaymentStatus.IsSettled(invoice.PaymentStatus))
            throw new UnprocessableEntityException("Invoice is already fully paid.");
        // S-7: a reversed invoice's ledger debit has been cancelled — nothing is owed on it; nor on a rejected one.
        if (InvoiceMatchStatus.Is(invoice.MatchStatus, InvoiceMatchStatus.Reversed))
            throw new UnprocessableEntityException($"Invoice {invoice.InvoiceNumber} has been reversed, so nothing is owed on it.");
        if (InvoiceMatchStatus.Is(invoice.MatchStatus, InvoiceMatchStatus.Rejected))
            throw new UnprocessableEntityException($"Invoice {invoice.InvoiceNumber} has been rejected, so nothing is owed on it.");
        // Item E: a supplier invoice is a payable only once it is Approved — booked to the supplier ledger.
        if (!InvoiceMatchStatus.Is(invoice.MatchStatus, InvoiceMatchStatus.Approved))
            throw new BadRequestException($"Invoice {invoice.InvoiceNumber} is not approved yet; approve it before paying.");

        // Never more than is still owed: the total less what supplier payments posted or hold, and legacy payments.
        var available = await InvoiceSettlement.AvailableAsync(_db, invoice);
        if (req.AmountPaid > available)
            throw new UnprocessableEntityException(
                $"{req.AmountPaid:N2} is more than is still owed on invoice {invoice.InvoiceNumber} ({Math.Max(0m, available):N2}).");

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

        invoice.PaymentStatus = InvoicePaymentStatus.AfterSettlement(invoice.PaidAmount, totalPaid, invoice.TotalAmount);
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

    /// <summary>
    /// Edits a legacy payment. This organization's own only (false — 404 — for another's, even for a super admin).
    /// The status moves only forward along the flow (<see cref="LegacyTransitions"/>): an unknown status is a 400, a
    /// step the flow does not allow — anything out of Reversed above all, which would make a reversed payment count
    /// again — a 409; resending the current status is not a change. Reversing one works whatever the invoice's status
    /// (a payment made before item E against an invoice not yet approved can still be undone) and, under the
    /// invoice's lock, recomputes the invoice's payment status.
    /// </summary>
    public async Task<bool> PatchAsync(Guid uuid, PatchPaymentRequest req, int modifiedBy)
    {
        var organizationId = _db.TenantContext.OrganizationId;
        var found = await _db.Payments.AsNoTracking()
            .Where(x => x.UUID == uuid && !x.IsDelete && x.OrganizationId == organizationId)
            .Select(x => new { x.InvoiceUuid })
            .FirstOrDefaultAsync();
        if (found is null) return false;

        string? target = null;
        if (req.Status is not null)
        {
            target = LegacyTransitions.Keys.FirstOrDefault(k => string.Equals(k, req.Status.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new BadRequestException(
                    $"'{req.Status}' is not a payment status. A payment is Pending, Processed, Cleared or Reversed.");
        }

        return await InvoiceRowLocks.InTransactionAsync(_db, async () =>
        {
            await InvoiceRowLocks.LockOneAsync(_db, found.InvoiceUuid);

            var e = await _db.Payments.FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete && x.OrganizationId == organizationId);
            if (e is null) return false;

            var reversing = false;
            if (target is not null && !string.Equals(target, e.Status, StringComparison.OrdinalIgnoreCase))
            {
                var next = LegacyTransitions.TryGetValue(e.Status, out var allowed) ? allowed : [];
                if (!next.Contains(target, StringComparer.OrdinalIgnoreCase))
                    throw new ConflictException(string.Equals(e.Status, "Reversed", StringComparison.OrdinalIgnoreCase)
                        ? $"Payment {e.PaymentNumber} has been reversed; it cannot be brought back. Record a new payment instead."
                        : $"Payment {e.PaymentNumber} is {e.Status}; it can go to {(next.Length == 0 ? "nothing else" : string.Join(" or ", next))}, not {target}.");

                e.Status  = target;
                reversing = target == "Reversed";
            }

            if (req.BankReference is not null) e.BankReference = req.BankReference.Trim();
            if (req.ChequeNumber  is not null) e.ChequeNumber  = req.ChequeNumber.Trim();
            if (req.Notes         is not null) e.Notes         = req.Notes.Trim();

            // If reversed, update invoice payment status
            if (reversing)
            {
                var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == e.InvoiceId);
                if (invoice is not null)
                {
                    var totalPaid = await _db.Payments
                        .Where(p => p.InvoiceId == e.InvoiceId && !p.IsDelete && p.Status != "Reversed" && p.UUID != uuid)
                        .SumAsync(p => p.AmountPaid);

                    invoice.PaymentStatus = InvoicePaymentStatus.AfterSettlement(invoice.PaidAmount, totalPaid, invoice.TotalAmount);
                    invoice.ModifiedDate = DateTime.UtcNow;
                }
            }

            await _db.SaveChangesAsync();
            return true;
        });
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
