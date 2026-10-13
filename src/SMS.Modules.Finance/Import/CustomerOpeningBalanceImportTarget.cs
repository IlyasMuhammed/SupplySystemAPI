using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

using static SMS.Modules.Finance.Import.FinanceImport;

namespace SMS.Modules.Finance.Import;

/// <summary>
/// A40 (TFIN, D-5) — CUSTOMER_OPENING_BALANCE: each row becomes a real open receivable, an <b>issued</b> sales invoice with
/// source OPENING (no sale order, no delivery) and one non-stock line "Opening balance {ref}" (no variant, so nothing on the
/// product ledger and no cost of sales), dated the legacy invoice date (≤ the opening balance date), its rate locked at that
/// date (A35 D-12) and booked on the customer ledger as an OPENING_BAL debit (reference SalesInvoice). It then ages, falls
/// overdue, shows in the customer's balance and is paid by customer payments like any invoice — and stays out of the sales
/// reports (they count INVOICE entries). Not sent to QuickBooks (D-14). Opening balances are never updated (IMP-OB-06): a
/// second row for the same customer and reference is refused (DUPLICATE_IN_DB), in validation and by the unique source-uuid
/// index. One SaveChanges per batch: all rows or none.
/// </summary>
internal sealed class CustomerOpeningBalanceImportTarget : IImportTarget
{
    internal const string FCustomer = "customer_code", FReference = "invoice_reference", FDate = "invoice_date",
        FDueDate = "due_date", FAmount = "amount", FCurrency = "currency_code", FNotes = "notes";

    internal const string CustomerList = "customers", CurrencyList = "currencies";
    internal const string LedgerReferenceType = "SalesInvoice";
    private const string NumberPrefix = "SINV";
    private const int MaxAttempts = 3;

    private static readonly OpeningBalancePlanner.Fields Names = new(FCustomer, FReference, FDate, FDueDate, FAmount, FCurrency);

    private readonly FinanceDbContext       _db;
    private readonly ICustomerLedgerService _ledger;
    private readonly OpeningBalancePlanner  _planner;
    private readonly IOrgCurrencyLookup     _orgCurrencies;
    private readonly IImportPartnerLookup?  _partners;
    private readonly TimeProvider           _clock;

    public CustomerOpeningBalanceImportTarget(
        FinanceDbContext db, ICustomerLedgerService ledger, IOpeningBalanceDateProvider openingDate, ICurrencyService currency,
        IOrgCurrencyLookup orgCurrencies, IImportPartnerLookup? partners = null, TimeProvider? clock = null)
    {
        _db            = db;
        _ledger        = ledger;
        _orgCurrencies = orgCurrencies;
        _partners      = partners;
        _planner       = new OpeningBalancePlanner(openingDate, currency, orgCurrencies, partners);
        _clock         = clock ?? TimeProvider.System;
    }

    public ImportProfileDefinition Describe() => new(
        ImportEntityTypes.CustomerOpeningBalance, "Customer opening balances",
        "What each customer owed at the opening balance date, one row per open invoice. Each row becomes an issued sales invoice "
      + "(source OPENING) that ages and is paid like any other. Dates must not be after the opening balance date.",
        ImportOrder: 12, IsOpeningBalance: true, MatchFields: [FCustomer, FReference],
        Fields:
        [
            new(FCustomer, "Customer Code", "An existing customer's code.", ImportFieldTypes.Lookup, IsRequired: true, IsKey: true,
                SortOrder: 1, SampleValue: "C-00001", MaxLength: 50, LookupList: CustomerList, Aliases: ["customer", "customer_id", "kunnr", "account"]),
            new(FReference, "Invoice Reference", "The legacy invoice number. Blank = one opening balance for the customer.",
                ImportFieldTypes.Text, IsRequired: false, IsKey: true, SortOrder: 2, SampleValue: "INV-0042",
                MaxLength: OpeningBalancePlanner.MaxReferenceLength, Aliases: ["reference", "invoice_no", "invoice_number", "document_no", "belnr"]),
            new(FDate, "Invoice Date", "On or before the opening balance date.", ImportFieldTypes.Date, IsRequired: true, IsKey: false,
                SortOrder: 3, SampleValue: "2026-06-15", Aliases: ["date", "document_date", "bldat"]),
            new(FDueDate, "Due Date", "Blank = the customer's payment terms (30 days when none).", ImportFieldTypes.Date,
                IsRequired: false, IsKey: false, SortOrder: 4, SampleValue: "2026-07-15", Aliases: ["due", "net_due_date"]),
            new(FAmount, "Amount Owed", "The open amount, greater than zero.", ImportFieldTypes.Decimal, IsRequired: true, IsKey: false,
                SortOrder: 5, SampleValue: "15000.00", MinValue: 0.01m, DecimalPlaces: 2, Aliases: ["amount", "balance", "open_amount", "outstanding"]),
            new(FCurrency, "Currency", "Blank = the organization's sale base currency.", ImportFieldTypes.Lookup, IsRequired: false,
                IsKey: false, SortOrder: 6, SampleValue: "PKR", MaxLength: 3, LookupList: CurrencyList, Aliases: ["currency", "waers"]),
            new(FNotes, "Notes", null, ImportFieldTypes.Text, IsRequired: false, IsKey: false, SortOrder: 7, SampleValue: null, MaxLength: 300)
        ],
        AllowUpdate: false);

    public async Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var customers  = _partners is null ? [] : await _partners.ListCodesAsync(organizationId, customers: true, MaxListValues, ct);
        var currencies = (await _orgCurrencies.ListAsync(organizationId, activeOnly: true, ct)).Select(c => c.Code).Take(MaxListValues).ToList();
        return [new ImportTemplateList(CustomerList, customers), new ImportTemplateList(CurrencyList, currencies)];
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

        await ReleaseFinishedTransactionAsync(ct);
        var input = rows.Select(r => (r.RowNumber, r.Values)).ToList();

        for (var attempt = 1; ; attempt++)
        {
            var plans = await PlanAsync(organizationId, input, ct);
            if (FirstError(plans.Select(p => new ImportRowCheck(p.RowNumber, p.Existing, p.Errors))) is { } error) throw error;

            var now = _clock.GetUtcNow().UtcDateTime;
            var outcomes = new List<ImportRowOutcome>(rows.Count);
            try
            {
                for (var i = 0; i < plans.Count; i++)
                {
                    var invoice = await TrackInvoiceAsync(organizationId, userId, plans[i], rows[i].Values, now, ct);
                    outcomes.Add(new ImportRowOutcome(plans[i].RowNumber, "IMPORTED", invoice.UUID));
                }

                StampOrganization(_db, organizationId);
                await _db.SaveChangesAsync(ct);
                return outcomes;
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                // A number or a ledger sequence taken by another writer, or the same opening balance imported at the same
                // moment: forget this pass and plan again from what is committed (a duplicate is then reported as such).
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
        var (_, plans) = await _planner.PlanAsync(org, rows, Names, customers: true, keyKind: "AR", ct);

        foreach (var (plan, values) in plans.Zip(rows.Select(r => r.Values)))
            if (Value(values, FNotes) is { Length: > 300 } notes)
                plan.Errors.Add(new(FNotes, "MAX_LENGTH", "Notes can be at most 300 characters.", notes));

        var keys = plans.Where(p => p.Key != Guid.Empty).Select(p => (Guid?)p.Key).ToList();
        if (keys.Count == 0) return plans;

        var existing = await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && keys.Contains(i.SourceUuid))
            .Select(i => new { Key = i.SourceUuid!.Value, i.UUID, i.InvoiceNumber, i.Status })
            .ToListAsync(ct);

        foreach (var hit in existing)
            foreach (var plan in plans.Where(p => p.Key == hit.Key))
            {
                plan.Existing = hit.UUID;
                plan.Errors.Add(new(FReference, "DUPLICATE_IN_DB",
                    $"This opening balance was already imported as invoice {hit.InvoiceNumber} ({hit.Status}); opening balances are never updated.",
                    plan.Reference));
            }

        return plans;
    }

    private async Task<SalesInvoice> TrackInvoiceAsync(
        Guid org, int userId, OpeningBalancePlanner.Plan plan, IReadOnlyDictionary<string, string?> values, DateTime now, CancellationToken ct)
    {
        var partner = plan.Partner!;
        var rate    = plan.Rate!;
        var day     = plan.Date.ToDateTime(TimeOnly.MinValue);
        var due     = plan.DueDate?.ToDateTime(TimeOnly.MinValue)
                      ?? day.AddDays(partner.PaymentTermsDays > 0 ? partner.PaymentTermsDays : SalesInvoiceService.DefaultPaymentTermDays);
        var label   = plan.Reference is null ? "Opening balance" : $"Opening balance {plan.Reference}";

        var invoice = new SalesInvoice
        {
            UUID            = Guid.NewGuid(),
            OrganizationId  = org,
            TraceId         = Guid.NewGuid(),
            InvoiceNumber   = await NextNumberAsync(org, day, ct),
            SaleOrderUuid   = null,
            SaleOrderNumber = string.Empty,
            SourceType      = SalesInvoiceSources.Opening,
            SourceUuid      = plan.Key,
            SourceNumber    = plan.Reference,
            PartnerId       = partner.Uuid,
            PartnerName     = Truncate(partner.Name, 200),
            InvoiceDate     = day,
            DueDate         = due,
            Subtotal        = plan.Amount,
            DiscountAmount  = 0m,
            TaxAmount       = 0m,
            GrandTotal      = plan.Amount,
            AmountPaid      = 0m,
            BalanceDue      = plan.Amount,
            Status          = SalesInvoiceStatuses.Issued,
            CurrencyCode    = rate.CurrencyCode,
            Notes           = Truncate(Value(values, FNotes) ?? $"{label} (imported as at the opening balance date).", 500),
            CreatedBy       = userId,
            CreatedDate     = now,
            ModifiedBy      = userId,
            ModifiedDate    = now
        };

        invoice.Lines.Add(new SalesInvoiceLine
        {
            UUID            = Guid.NewGuid(),
            OrganizationId  = org,
            LineNo          = 1,
            // No variant: an opening balance sells nothing, so nothing goes on the product ledger.
            VariantUuid     = Guid.Empty,
            Description     = Truncate(label, 300),
            Quantity        = 1m,
            UnitPrice       = plan.Amount,
            DiscountPercent = 0m,
            TaxPercent      = 0m,
            LineTotal       = plan.Amount
        });

        _db.SalesInvoices.Add(invoice);
        SalesInvoiceService.ApplyRateLock(invoice, rate, now);

        await _ledger.TrackEntryAsync(new CustomerLedgerPosting(
            partner.Uuid, CustomerLedgerEntryTypes.OpeningBal, LedgerReferenceType, invoice.UUID, invoice.InvoiceNumber,
            Debit: plan.Amount, Credit: 0m, invoice.CurrencyCode,
            Truncate($"{label}: invoice {invoice.InvoiceNumber} dated {Iso(plan.Date)}", 500),
            day, userId));

        return invoice;
    }

    /// <summary>SINV-yyyyMMdd-NNNN of the invoice date: the highest used that day (this unit's included) + 1.</summary>
    private async Task<string> NextNumberAsync(Guid org, DateTime day, CancellationToken ct)
    {
        var prefix = $"{NumberPrefix}-{day:yyyyMMdd}-";
        var used = await _db.SalesInvoices.IgnoreQueryFilters()
            .Where(i => i.OrganizationId == org && i.InvoiceNumber.StartsWith(prefix))
            .Select(i => i.InvoiceNumber).ToListAsync(ct);
        used.AddRange(_db.ChangeTracker.Entries<SalesInvoice>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.InvoiceNumber));

        var highest = used.Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => int.TryParse(n[prefix.Length..], out var v) ? v : 0)
            .DefaultIfEmpty(0).Max();
        return $"{prefix}{highest + 1:D4}";
    }

    /// <summary>As the counter sale does: a transaction another module finished on this scoped context is let go.</summary>
    private async Task ReleaseFinishedTransactionAsync(CancellationToken ct)
    {
        if (!_db.Database.IsRelational()) return;
        if (_db.Database.CurrentTransaction?.GetDbTransaction() is { Connection: null })
            await _db.Database.UseTransactionAsync(null, ct);
    }
}
