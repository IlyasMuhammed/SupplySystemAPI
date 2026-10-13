using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

using static SMS.Modules.Finance.Import.FinanceImport;

namespace SMS.Modules.Finance.Import;

/// <summary>
/// A40 (TFIN, D-5) — SUPPLIER_OPENING_BALANCE: each row becomes a real open payable, an <b>approved</b> supplier invoice with
/// source OPENING and no purchase order or GRN (the G10 shape a carrier's bill already has), one line "Opening balance
/// {ref}", dated the supplier's bill date (≤ the opening balance date) with its rate locked at that date against the purchase
/// base (A35 D-12), and booked as approval books an invoice: an INVOICE_APPROVED debit on the supplier ledger and its master
/// ledger mirror (so a reversal finds it, and the A24 one-time OPENING_BALANCE import is not mistaken as run). It is then
/// paid by supplier payments, ages and can be reversed like any approved invoice. Not sent to QuickBooks (D-14). Never
/// updated (IMP-OB-06): a second row for the same supplier and reference is refused. One SaveChanges per batch.
/// </summary>
internal sealed class SupplierOpeningBalanceImportTarget : IImportTarget
{
    internal const string FSupplier = "supplier_code", FReference = "bill_reference", FDate = "bill_date",
        FDueDate = "due_date", FAmount = "amount", FCurrency = "currency_code", FNotes = "notes";

    internal const string SupplierList = "suppliers", CurrencyList = "currencies";
    internal const int DefaultTermDays = 30;
    private const int MaxAttempts = 3;

    private static readonly OpeningBalancePlanner.Fields Names = new(FSupplier, FReference, FDate, FDueDate, FAmount, FCurrency);

    private readonly FinanceDbContext      _db;
    private readonly OpeningBalancePlanner _planner;
    private readonly IOrgCurrencyLookup    _orgCurrencies;
    private readonly IImportPartnerLookup? _partners;
    private readonly TimeProvider          _clock;

    public SupplierOpeningBalanceImportTarget(
        FinanceDbContext db, IOpeningBalanceDateProvider openingDate, ICurrencyService currency, IOrgCurrencyLookup orgCurrencies,
        IImportPartnerLookup? partners = null, TimeProvider? clock = null)
    {
        _db            = db;
        _orgCurrencies = orgCurrencies;
        _partners      = partners;
        _planner       = new OpeningBalancePlanner(openingDate, currency, orgCurrencies, partners);
        _clock         = clock ?? TimeProvider.System;
    }

    public ImportProfileDefinition Describe() => new(
        ImportEntityTypes.SupplierOpeningBalance, "Supplier opening balances",
        "What was owed to each supplier at the opening balance date, one row per open bill. Each row becomes an approved supplier "
      + "invoice (source OPENING, no purchase order) that is paid like any other. Dates must not be after the opening balance date.",
        ImportOrder: 13, IsOpeningBalance: true, MatchFields: [FSupplier, FReference],
        Fields:
        [
            new(FSupplier, "Supplier Code", "An existing supplier's code.", ImportFieldTypes.Lookup, IsRequired: true, IsKey: true,
                SortOrder: 1, SampleValue: "S-00001", MaxLength: 50, LookupList: SupplierList, Aliases: ["supplier", "vendor", "vendor_code", "lifnr"]),
            new(FReference, "Bill Reference", "The supplier's invoice number. Blank = one opening balance for the supplier.",
                ImportFieldTypes.Text, IsRequired: false, IsKey: true, SortOrder: 2, SampleValue: "BILL-7781",
                MaxLength: OpeningBalancePlanner.MaxReferenceLength, Aliases: ["reference", "supplier_invoice_no", "invoice_no", "bill_no", "xblnr"]),
            new(FDate, "Bill Date", "On or before the opening balance date.", ImportFieldTypes.Date, IsRequired: true, IsKey: false,
                SortOrder: 3, SampleValue: "2026-06-10", Aliases: ["date", "invoice_date", "document_date", "bldat"]),
            new(FDueDate, "Due Date", "Blank = 30 days after the bill date.", ImportFieldTypes.Date, IsRequired: false, IsKey: false,
                SortOrder: 4, SampleValue: "2026-07-10", Aliases: ["due", "net_due_date"]),
            new(FAmount, "Amount Owed", "The open amount, greater than zero.", ImportFieldTypes.Decimal, IsRequired: true, IsKey: false,
                SortOrder: 5, SampleValue: "8200.00", MinValue: 0.01m, DecimalPlaces: 2, Aliases: ["amount", "balance", "open_amount", "outstanding"]),
            new(FCurrency, "Currency", "Blank = the organization's purchase base currency.", ImportFieldTypes.Lookup, IsRequired: false,
                IsKey: false, SortOrder: 6, SampleValue: "PKR", MaxLength: 3, LookupList: CurrencyList, Aliases: ["currency", "waers"]),
            new(FNotes, "Notes", null, ImportFieldTypes.Text, IsRequired: false, IsKey: false, SortOrder: 7, SampleValue: null,
                MaxLength: InvoiceRepository.NotesMaxLength)
        ],
        AllowUpdate: false);

    public async Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var suppliers  = _partners is null ? [] : await _partners.ListCodesAsync(organizationId, customers: false, MaxListValues, ct);
        var currencies = (await _orgCurrencies.ListAsync(organizationId, activeOnly: true, ct)).Select(c => c.Code).Take(MaxListValues).ToList();
        return [new ImportTemplateList(SupplierList, suppliers), new ImportTemplateList(CurrencyList, currencies)];
    }

    public async Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(
        Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default)
    {
        EnsureOrganization(_db, organizationId);
        var plans = await PlanAsync(organizationId, rows.Select(r => (r.RowNumber, r.Values)).ToList(), ct);
        return plans.Select(p => new ImportRowCheck(p.RowNumber, p.Existing, p.Errors)).ToList();
    }

    public async Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(
        Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default)
    {
        EnsureOrganization(_db, organizationId);
        if (rows.Count == 0) return [];
        if (rows.FirstOrDefault(r => !string.Equals(r.Action, "CREATE", StringComparison.OrdinalIgnoreCase)) is { } update)
            throw new BadRequestException($"Row {update.RowNumber}: opening balances are never updated; import a new one with its own reference.");

        if (_db.Database.IsRelational() && _db.Database.CurrentTransaction?.GetDbTransaction() is { Connection: null })
            await _db.Database.UseTransactionAsync(null, ct);

        var input = rows.Select(r => (r.RowNumber, r.Values)).ToList();

        for (var attempt = 1; ; attempt++)
        {
            var plans = await PlanAsync(organizationId, input, ct);
            if (FirstError(plans.Select(p => new ImportRowCheck(p.RowNumber, p.Existing, p.Errors))) is { } error) throw error;

            var now      = _clock.GetUtcNow().UtcDateTime;
            var outcomes = new List<ImportRowOutcome>(rows.Count);
            try
            {
                var numbers = new InvoiceNumbers(await NumberBaseAsync(organizationId, now.Year, ct), now.Year);
                var ledger  = new LedgerTails(_db, organizationId);

                for (var i = 0; i < plans.Count; i++)
                {
                    var bill = Track(organizationId, userId, plans[i], rows[i].Values, numbers.Next(), now);
                    await ledger.TrackAsync(bill, userId, now, ct);
                    outcomes.Add(new ImportRowOutcome(plans[i].RowNumber, "IMPORTED", bill.UUID));
                }

                StampOrganization(_db, organizationId);
                await _db.SaveChangesAsync(ct);
                return outcomes;
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                _db.ChangeTracker.Clear();
            }
            catch
            {
                _db.ChangeTracker.Clear();
                throw;
            }
        }
    }

    private async Task<List<OpeningBalancePlanner.Plan>> PlanAsync(
        Guid org, IReadOnlyList<(int RowNumber, IReadOnlyDictionary<string, string?> Values)> rows, CancellationToken ct)
    {
        var (_, plans) = await _planner.PlanAsync(org, rows, Names, customers: false, keyKind: "AP", ct);

        foreach (var (plan, values) in plans.Zip(rows.Select(r => r.Values)))
            if (Value(values, FNotes) is { Length: > InvoiceRepository.NotesMaxLength } notes)
                plan.Errors.Add(new(FNotes, "MAX_LENGTH", $"Notes can be at most {InvoiceRepository.NotesMaxLength} characters.", notes));

        var keys = plans.Where(p => p.Key != Guid.Empty).Select(p => (Guid?)p.Key).ToList();
        if (keys.Count == 0) return plans;

        var existing = await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.SourceType == SalesInvoiceSources.Opening && keys.Contains(i.SourceUuid) && !i.IsDelete)
            .Select(i => new { Key = i.SourceUuid!.Value, i.UUID, i.InvoiceNumber, i.MatchStatus })
            .ToListAsync(ct);

        foreach (var hit in existing)
            foreach (var plan in plans.Where(p => p.Key == hit.Key))
            {
                plan.Existing = hit.UUID;
                plan.Errors.Add(new(FReference, "DUPLICATE_IN_DB",
                    $"This opening balance was already imported as supplier invoice {hit.InvoiceNumber} ({hit.MatchStatus}); opening balances are never updated.",
                    plan.Reference));
            }

        return plans;
    }

    private Invoice Track(Guid org, int userId, OpeningBalancePlanner.Plan plan, IReadOnlyDictionary<string, string?> values, string number, DateTime now)
    {
        var supplier = plan.Partner!;
        var rate     = plan.Rate!;
        var day      = plan.Date.ToDateTime(TimeOnly.MinValue);
        var label    = plan.Reference is null ? "Opening balance" : $"Opening balance {plan.Reference}";

        var bill = new Invoice
        {
            UUID              = Guid.NewGuid(),
            OrganizationId    = org,
            TraceId           = Guid.NewGuid(),
            InvoiceNumber     = number,
            SupplierInvoiceNo = plan.Reference,
            SupplierId        = supplier.Uuid,
            SupplierName      = Truncate(supplier.Name, 200),
            PoUuid            = null,
            GrnUuid           = null,
            InvoiceDate       = day,
            ReceivedDate      = day,
            DueDate           = plan.DueDate?.ToDateTime(TimeOnly.MinValue) ?? day.AddDays(DefaultTermDays),
            Currency          = rate.CurrencyCode,
            CurrencyId        = rate.CurrencyId,
            Subtotal          = plan.Amount,
            TaxAmount         = 0m,
            TotalAmount       = plan.Amount,
            ExchangeRate      = rate.Rate,
            BaseCurrencyId    = rate.BaseCurrencyId,
            BaseCurrencyCode  = rate.BaseCurrencyCode,
            BaseTotalAmount   = rate.ToBase(plan.Amount),
            ExchangeRateLockedAt = now,
            MatchedPoValue    = 0m,
            MatchedGrnValue   = 0m,
            VarianceAmount    = 0m,
            MatchStatus       = InvoiceMatchStatus.Approved,
            PaymentStatus     = "Unpaid",
            PaidAmount        = 0m,
            ApprovedBy        = userId,
            ApprovedAt        = now,
            Notes             = Truncate(Value(values, FNotes) ?? $"{label} (imported as at the opening balance date).", InvoiceRepository.NotesMaxLength),
            SourceType        = SalesInvoiceSources.Opening,
            SourceUuid        = plan.Key,
            IsActive          = true,
            CreatedBy         = userId,
            CreatedDate       = now,
            ModifiedBy        = userId,
            ModifiedDate      = now
        };

        bill.Lines.Add(new InvoiceLine
        {
            UUID            = Guid.NewGuid(),
            OrganizationId  = org,
            LineNo          = 1,
            ItemDescription = Truncate(label, 300),
            QtyInvoiced     = 1m,
            UnitPrice       = plan.Amount,
            LineTotal       = plan.Amount
        });

        _db.Invoices.Add(bill);
        return bill;
    }

    /// <summary>
    /// INV-yyyy-NNNNN as <see cref="InvoiceRepository"/> numbers them (the year's invoices counted + 1), never below a number
    /// already used that year — each bill of the batch takes the next.
    /// </summary>
    private async Task<int> NumberBaseAsync(Guid org, int year, CancellationToken ct)
    {
        var start  = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end    = start.AddYears(1);
        var prefix = $"INV-{year}-";
        var mine   = _db.Invoices.IgnoreQueryFilters().Where(i => i.OrganizationId == org);

        var count  = await mine.CountAsync(i => i.CreatedDate >= start && i.CreatedDate < end, ct);
        var used   = await mine.Where(i => i.InvoiceNumber.StartsWith(prefix)).Select(i => i.InvoiceNumber).ToListAsync(ct);
        var max    = used.Select(n => int.TryParse(n[prefix.Length..], out var v) ? v : 0).DefaultIfEmpty(0).Max();
        return Math.Max(count, max);
    }

    private sealed class InvoiceNumbers(int last, int year)
    {
        private int _last = last;
        public string Next() => $"INV-{year}-{++_last:D5}";
    }

    /// <summary>
    /// The supplier ledger and its master mirror, appended to in memory: each entry continues from the last committed one
    /// or the last this batch tracked (the per-supplier and per-organization sequence indexes are the race guard), dated the
    /// bill date — what <see cref="SupplierLedgerService.PostEntryAsync"/> writes for an approval, but unsaved so the whole
    /// batch commits in one SaveChanges.
    /// </summary>
    private sealed class LedgerTails(FinanceDbContext db, Guid org)
    {
        private readonly Dictionary<Guid, (int Seq, decimal Balance)> _suppliers = [];
        private (int Seq, decimal Balance)? _master;

        public async Task TrackAsync(Invoice bill, int userId, DateTime now, CancellationToken ct)
        {
            if (!_suppliers.TryGetValue(bill.SupplierId, out var tail))
            {
                var last = await db.SupplierLedgerEntries.IgnoreQueryFilters().AsNoTracking()
                    .Where(e => e.SupplierId == bill.SupplierId)
                    .OrderByDescending(e => e.SequenceNo)
                    .Select(e => new { e.SequenceNo, e.BalanceAfter })
                    .FirstOrDefaultAsync(ct);
                tail = (last?.SequenceNo ?? 0, last?.BalanceAfter ?? 0m);
            }

            if (_master is null)
            {
                var last = await db.MasterFinancialLedgers.IgnoreQueryFilters().AsNoTracking()
                    .Where(e => e.OrganizationId == org)
                    .OrderByDescending(e => e.SequenceNo)
                    .Select(e => new { e.SequenceNo, e.BalanceAfter })
                    .FirstOrDefaultAsync(ct);
                _master = (last?.SequenceNo ?? 0, last?.BalanceAfter ?? 0m);
            }

            var label     = bill.SupplierInvoiceNo is null ? "Opening balance" : $"Opening balance {bill.SupplierInvoiceNo}";
            var narration = Truncate($"{label}: invoice {bill.InvoiceNumber} approved on import.", 500);
            var amount    = bill.TotalAmount;

            tail = (tail.Seq + 1, tail.Balance + amount);
            _suppliers[bill.SupplierId] = tail;
            db.SupplierLedgerEntries.Add(new SupplierLedgerEntry
            {
                UUID = Guid.NewGuid(), OrganizationId = org, SupplierId = bill.SupplierId, SequenceNo = tail.Seq,
                TransactionType = InvoiceRepository.ApprovedLedgerType, ReferenceType = "Invoice", ReferenceId = bill.UUID,
                ReferenceNo = bill.InvoiceNumber, EntryDate = bill.InvoiceDate, DebitAmount = amount, CreditAmount = 0m,
                BalanceAfter = tail.Balance, Narration = narration, CreatedBy = userId, CreatedDate = now
            });

            var master = (_master.Value.Seq + 1, _master.Value.Balance + amount);
            _master = master;
            db.MasterFinancialLedgers.Add(new MasterFinancialLedger
            {
                UUID = Guid.NewGuid(), OrganizationId = org, SequenceNo = master.Item1, SupplierId = bill.SupplierId,
                SupplierName = bill.SupplierName, TransactionType = InvoiceRepository.ApprovedLedgerType, ReferenceType = "Invoice",
                ReferenceId = bill.UUID, ReferenceNo = bill.InvoiceNumber, EntryDate = bill.InvoiceDate, DebitAmount = amount,
                CreditAmount = 0m, BalanceAfter = master.Item2, Narration = narration, CreatedBy = userId, CreatedDate = now
            });
        }
    }
}
