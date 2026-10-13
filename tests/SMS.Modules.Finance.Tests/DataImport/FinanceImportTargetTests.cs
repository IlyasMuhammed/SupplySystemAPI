using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Import;
using SMS.Modules.Finance.Integration;
using SMS.Modules.Finance.Services;
using SMS.Modules.Finance.Tests.QuickBooks;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

using C = SMS.Modules.Finance.Import.CustomerOpeningBalanceImportTarget;
using S = SMS.Modules.Finance.Import.SupplierOpeningBalanceImportTarget;
using T = SMS.Modules.Finance.Import.TaxCodeImportTarget;

namespace SMS.Modules.Finance.Tests.DataImport;

// ── Opening balance date (D-7) ───────────────────────────────────────────────

public sealed class OpeningBalanceDateTests
{
    private readonly FinanceImportRig _rig = new();

    [Fact]
    public async Task It_is_unset_until_set_then_read_back_by_the_service_and_by_the_provider_for_that_organization_only()
    {
        await using (var db = _rig.Db())
            (await new OpeningBalanceDateService(db).GetAsync()).Should().BeNull();

        await _rig.SetOpeningDateAsync("2026-06-30");

        await using var read = _rig.Db();
        var service = new OpeningBalanceDateService(read);
        (await service.GetAsync()).Should().Be(new DateOnly(2026, 6, 30));
        IOpeningBalanceDateProvider provider = service;
        (await provider.GetAsync(_rig.Org)).Should().Be(new DateOnly(2026, 6, 30));
        (await provider.GetAsync(Guid.NewGuid())).Should().BeNull("another organization has set nothing");

        await _rig.SetOpeningDateAsync("2026-07-31");
        (await provider.GetAsync(_rig.Org)).Should().Be(new DateOnly(2026, 7, 31));
        (await read.OrgSettings.IgnoreQueryFilters().CountAsync()).Should().Be(1, "one settings row per organization");
    }

    [Theory]
    [InlineData("2026-6-30")]
    [InlineData("30/06/2026")]
    [InlineData("tomorrow")]
    public void A_date_not_in_yyyy_MM_dd_is_refused(string text)
    {
        var act = () => OpeningBalanceDateService.Parse(text);
        act.Should().Throw<BadRequestException>();
    }

    [Fact]
    public async Task Once_opening_documents_exist_it_cannot_be_cleared_or_moved_before_them()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");
        _rig.Customer("C-00001");
        await _rig.CustomerTargetAsync(t => t.ApplyAsync(_rig.Org, FinanceImportRig.User,
            [FinanceImportRig.Create(2, (C.FCustomer, "C-00001"), (C.FReference, "OLD-1"), (C.FDate, "2026-06-20"), (C.FAmount, "100"))]));

        await using var db = _rig.Db();
        var service = new OpeningBalanceDateService(db);

        await service.Invoking(s => s.SetAsync(null, 1)).Should().ThrowAsync<ConflictException>();
        await service.Invoking(s => s.SetAsync(new DateOnly(2026, 6, 19), 1)).Should().ThrowAsync<ConflictException>()
            .WithMessage("*2026-06-20*");
        (await service.SetAsync(new DateOnly(2026, 6, 20), 1)).Should().Be(new DateOnly(2026, 6, 20));
    }

    private sealed class FakeOpeningStock(Guid org, DateOnly? latest) : IOpeningStockDateSource
    {
        public Task<DateOnly?> GetLatestAsync(Guid organizationId, CancellationToken ct = default) =>
            Task.FromResult(organizationId == org ? latest : null);
    }

    [Fact]
    public async Task Posted_opening_stock_also_stops_the_date_being_cleared_or_moved_before_it()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");

        await using var db = _rig.Db();
        var service = new OpeningBalanceDateService(db, _rig.World.Clock, new FakeOpeningStock(_rig.Org, new DateOnly(2026, 6, 30)));

        await service.Invoking(s => s.SetAsync(null, 1)).Should().ThrowAsync<ConflictException>().WithMessage("*cannot be cleared*");
        await service.Invoking(s => s.SetAsync(new DateOnly(2026, 6, 29), 1)).Should().ThrowAsync<ConflictException>()
            .WithMessage("Opening balance documents dated up to 2026-06-30 were already imported; the opening balance date cannot be earlier than that.");
        (await service.SetAsync(new DateOnly(2026, 7, 31), 1)).Should().Be(new DateOnly(2026, 7, 31), "moving it later is fine");
        (await service.SetAsync(new DateOnly(2026, 6, 30), 1)).Should().Be(new DateOnly(2026, 6, 30), "back to the stock's own date");

        var noStock = new OpeningBalanceDateService(db, _rig.World.Clock, new FakeOpeningStock(Guid.NewGuid(), new DateOnly(2026, 6, 30)));
        (await noStock.SetAsync(null, 1)).Should().BeNull("another organization's opening stock does not count");
    }
}

// ── TAX_CODE ─────────────────────────────────────────────────────────────────

public sealed class TaxCodeImportTargetTests
{
    private readonly FinanceImportRig _rig = new();

    [Fact]
    public async Task Describe_and_template_lists_follow_the_contract()
    {
        var profile = await _rig.TaxTargetAsync(t => Task.FromResult(t.Describe()));
        profile.EntityType.Should().Be(ImportEntityTypes.TaxCode);
        profile.MatchFields.Should().Equal(T.FCode);
        profile.AllowUpdate.Should().BeTrue();
        profile.Fields.Select(f => f.FieldName).Should().Contain([T.FCode, T.FName, T.FRate, T.FUsage, T.FDefault]);

        var lists = await _rig.TaxTargetAsync(t => t.GetTemplateListsAsync(_rig.Org));
        lists.Single().Values.Should().Equal("SALES", "PURCHASE", "BOTH");
    }

    [Fact]
    public async Task New_codes_are_created_upper_case_and_an_existing_one_is_matched_and_updated_leaving_blank_fields_alone()
    {
        var outcome = await _rig.TaxTargetAsync(t => t.ApplyAsync(_rig.Org, 9,
        [
            FinanceImportRig.Create(2, (T.FCode, "gst17"), (T.FName, "GST 17%"), (T.FRate, "17"), (T.FUsage, "sales"), (T.FDefault, "true")),
            FinanceImportRig.Create(3, (T.FCode, "WHT5"), (T.FName, "Withholding 5%"), (T.FRate, "5.5"), (T.FUsage, "PURCHASE"))
        ]));
        outcome.Select(o => o.Status).Should().Equal("IMPORTED", "IMPORTED");

        // Validation now matches GST17 by code (case-insensitive) and offers it for UPDATE.
        var checks = await _rig.TaxTargetAsync(t => t.ValidateAsync(_rig.Org,
            [FinanceImportRig.Input(2, (T.FCode, "Gst17"), (T.FRate, "18"))]));
        checks.Single().Errors.Should().BeEmpty();
        checks.Single().ExistingEntityUuid.Should().Be(outcome[0].EntityUuid);

        var updated = await _rig.TaxTargetAsync(t => t.ApplyAsync(_rig.Org, 9,
            [FinanceImportRig.Update(2, outcome[0].EntityUuid, (T.FCode, "GST17"), (T.FRate, "18"))]));
        updated.Single().Status.Should().Be("UPDATED");

        await using var db = _rig.Db();
        var codes = await db.TaxCodes.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
        codes.Select(c => (c.Code, c.Name, c.RatePercent, c.Usage, c.IsDefault, c.CreatedBy))
            .Should().Equal(("GST17", "GST 17%", 18m, "SALES", true, 9), ("WHT5", "Withholding 5%", 5.5m, "PURCHASE", false, 9));
        codes[0].ModifiedBy.Should().Be(9);
    }

    [Fact]
    public async Task An_imported_default_dethrones_the_existing_default_of_its_side()
    {
        await using (var db = _rig.Db())
        {
            db.TaxCodes.Add(new TaxCode { OrganizationId = _rig.Org, Code = "OLD", Name = "Old", RatePercent = 16m, Usage = "BOTH", IsDefault = true });
            await db.SaveChangesAsync();
        }

        await _rig.TaxTargetAsync(t => t.ApplyAsync(_rig.Org, 1,
            [FinanceImportRig.Create(2, (T.FCode, "NEW"), (T.FName, "New"), (T.FRate, "18"), (T.FUsage, "SALES"), (T.FDefault, "yes"))]));

        await using var read = _rig.Db();
        (await read.TaxCodes.AsNoTracking().Where(c => c.IsDefault).Select(c => c.Code).ToListAsync()).Should().Equal("NEW");
    }

    [Fact]
    public async Task Bad_rows_are_reported_per_field_and_two_defaults_of_one_side_in_one_file_are_refused()
    {
        var checks = await _rig.TaxTargetAsync(t => t.ValidateAsync(_rig.Org,
        [
            FinanceImportRig.Input(2, (T.FCode, "GST 17"), (T.FName, "x"), (T.FRate, "17")),
            FinanceImportRig.Input(3, (T.FCode, "A"), (T.FName, "x"), (T.FRate, "101")),
            FinanceImportRig.Input(4, (T.FCode, "B"), (T.FName, "x"), (T.FRate, "1.234")),
            FinanceImportRig.Input(5, (T.FCode, "C"), (T.FName, "x"), (T.FRate, "5"), (T.FUsage, "VAT")),
            FinanceImportRig.Input(6, (T.FCode, "D"), (T.FRate, "5")),
            FinanceImportRig.Input(7, (T.FCode, "E"), (T.FName, "x"), (T.FRate, "5"), (T.FUsage, "SALES"), (T.FDefault, "true")),
            FinanceImportRig.Input(8, (T.FCode, "F"), (T.FName, "x"), (T.FRate, "5"), (T.FUsage, "BOTH"), (T.FDefault, "true")),
            FinanceImportRig.Input(9, (T.FCode, "G"), (T.FName, "x"), (T.FRate, "5"), (T.FUsage, "PURCHASE"), (T.FDefault, "true")),
            FinanceImportRig.Input(10, (T.FCode, "e"), (T.FName, "x"), (T.FRate, "5"))
        ]));

        checks.Select(c => c.Errors.Select(e => e.Code).FirstOrDefault()).Should().Equal(
            "PATTERN_MISMATCH", "OUT_OF_RANGE", "INVALID_PRECISION", "LOOKUP_NOT_FOUND", "REQUIRED", null,
            FinanceImport.MultipleDefaults, null, "DUPLICATE_IN_FILE");

        // And Apply refuses the whole batch, writing nothing.
        var apply = () => _rig.TaxTargetAsync(t => t.ApplyAsync(_rig.Org, 1,
        [
            FinanceImportRig.Create(2, (T.FCode, "OK"), (T.FName, "ok"), (T.FRate, "5")),
            FinanceImportRig.Create(3, (T.FCode, "BAD"), (T.FName, "bad"), (T.FRate, "500"))
        ]));
        await apply.Should().ThrowAsync<BadRequestException>().WithMessage("Row 3:*");
        await using var db = _rig.Db();
        (await db.TaxCodes.CountAsync()).Should().Be(0);
    }
}

// ── CUSTOMER_OPENING_BALANCE ─────────────────────────────────────────────────

public sealed class CustomerOpeningBalanceImportTargetTests
{
    private readonly FinanceImportRig _rig = new();
    private readonly Guid _customer;

    public CustomerOpeningBalanceImportTargetTests()
    {
        _customer = _rig.Customer("C-00001", "Acme Ltd", terms: 45);
    }

    private static ImportApplyRow Row(int n, string? reference, string date, string amount, string? currency = null, string? due = null, string code = "C-00001") =>
        FinanceImportRig.Create(n, (C.FCustomer, code), (C.FReference, reference), (C.FDate, date), (C.FAmount, amount),
            (C.FCurrency, currency), (C.FDueDate, due));

    private Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(params ImportApplyRow[] rows) =>
        _rig.CustomerTargetAsync(t => t.ApplyAsync(_rig.Org, FinanceImportRig.User, rows));

    private Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(params ImportApplyRow[] rows) =>
        _rig.CustomerTargetAsync(t => t.ValidateAsync(_rig.Org, rows.Select(r => new ImportRowInput(r.RowNumber, r.Values, null)).ToList()));

    [Fact]
    public async Task Without_an_opening_balance_date_every_row_is_refused_with_the_contract_message()
    {
        var checks = await ValidateAsync(Row(2, "A", "2026-06-01", "10"));
        checks.Single().Errors.Should().Contain(e => e.Code == FinanceImport.OpeningDateMissing
                                                  && e.Message == "Set the opening balance date in Finance settings first.");
    }

    [Fact]
    public async Task Each_row_becomes_an_issued_opening_invoice_with_an_OPENING_BAL_debit_that_ages_and_counts_in_the_balance()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");

        var outcome = await ApplyAsync(Row(2, "INV-0042", "2026-06-15", "15000.50"), Row(3, "INV-0043", "2026-06-20", "4999.50", due: "2026-09-30"));
        outcome.Select(o => o.Status).Should().Equal("IMPORTED", "IMPORTED");

        var books = await _rig.Desk.BooksAsync(_customer);
        books.Invoices.Should().HaveCount(2);
        var first = books.Invoice(outcome[0].EntityUuid);
        first.SourceType.Should().Be("OPENING");
        first.SourceNumber.Should().Be("INV-0042");
        first.Status.Should().Be("ISSUED");
        first.SaleOrderUuid.Should().BeNull();
        first.InvoiceNumber.Should().Be("SINV-20260615-0001");
        first.InvoiceDate.Should().Be(new DateTime(2026, 6, 15));
        first.DueDate.Should().Be(new DateTime(2026, 7, 30), "45 days of the customer's terms");
        books.Invoice(outcome[1].EntityUuid).DueDate.Should().Be(new DateTime(2026, 9, 30));
        (first.GrandTotal, first.BalanceDue, first.CurrencyCode, first.ExchangeRate, first.BaseGrandTotal)
            .Should().Be((15000.50m, 15000.50m, "PKR", 1m, 15000.50m));
        first.ExchangeRateLockedAt.Should().NotBeNull();
        first.CreatedBy.Should().Be(FinanceImportRig.User);
        first.OrganizationId.Should().Be(_rig.Org);

        books.Ledger.Select(e => (e.EntryType, e.ReferenceType, e.DebitAmount, e.RunningBalance))
            .Should().Equal(("OPENING_BAL", "SalesInvoice", 15000.50m, 15000.50m), ("OPENING_BAL", "SalesInvoice", 4999.50m, 20000m));
        books.Ledger[0].EntryDate.Should().Be(new DateTime(2026, 6, 15));
        ReceivablesInvariants.Violations(books, _rig.World.Clock.Value.Date).Should().BeEmpty();

        await using (var db = _rig.Db())
        {
            var line = await db.SalesInvoiceLines.AsNoTracking().SingleAsync(l => l.SalesInvoice.UUID == first.UUID);
            (line.VariantUuid, line.Description, line.Quantity, line.UnitPrice, line.LineTotal)
                .Should().Be((Guid.Empty, "Opening balance INV-0042", 1m, 15000.50m, 15000.50m));
            (await db.ProductLedgerEntries.CountAsync()).Should().Be(0, "an opening balance sells nothing");
        }

        (await _rig.AgingAsync(_customer)).Should().Be(20000m);
        (await _rig.OpenBalanceAsync(_customer)).Should().Be(20000m);

        // Not a sale of any period: the sales reports count INVOICE entries only.
        (await _rig.SalesReports().GetSalesByCustomerAsync(new SalesByCustomerFilter())).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task An_opening_invoice_is_paid_down_by_customer_payments_like_any_other()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");
        var outcome = await ApplyAsync(Row(2, "INV-1", "2026-06-15", "1000"), Row(3, "INV-2", "2026-06-25", "500"));

        var paid = await _rig.Desk.PayAsync(_customer, 1200m);
        paid.Allocations.Select(a => (a.InvoiceUuid, a.Amount)).Should().Equal((outcome[0].EntityUuid, 1000m), (outcome[1].EntityUuid, 200m));

        var books = await _rig.Desk.BooksAsync(_customer);
        books.Invoice(outcome[0].EntityUuid).Status.Should().Be("PAID");
        books.Invoice(outcome[1].EntityUuid).Status.Should().Be("PARTIALLY_PAID");
        books.Ledger.Last().RunningBalance.Should().Be(300m);
        ReceivablesInvariants.Violations(books, _rig.World.Clock.Value.Date).Should().BeEmpty();
        (await _rig.AgingAsync(_customer)).Should().Be(300m);
        (await _rig.OpenBalanceAsync(_customer)).Should().Be(300m);
    }

    [Fact]
    public async Task A_second_import_of_the_same_customer_and_reference_is_a_duplicate_and_writes_nothing()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");
        var first = await ApplyAsync(Row(2, "INV-1", "2026-06-15", "1000"), Row(3, null, "2026-06-15", "50"));

        var checks = await ValidateAsync(Row(2, "inv-1", "2026-06-16", "999"), Row(3, null, "2026-06-01", "1"), Row(4, "INV-NEW", "2026-06-01", "1"));
        checks[0].Errors.Should().ContainSingle(e => e.Code == "DUPLICATE_IN_DB");
        checks[0].ExistingEntityUuid.Should().Be(first[0].EntityUuid);
        checks[1].Errors.Should().ContainSingle(e => e.Code == "DUPLICATE_IN_DB", "a blank reference is one opening balance per customer");
        checks[2].Errors.Should().BeEmpty();

        var again = () => ApplyAsync(Row(2, "INV-NEW", "2026-06-01", "1"), Row(3, "INV-1", "2026-06-15", "1000"));
        await again.Should().ThrowAsync<BadRequestException>().WithMessage("Row 3:*already imported*");
        (await _rig.Desk.BooksAsync(_customer)).Invoices.Should().HaveCount(2);

        var update = () => _rig.CustomerTargetAsync(t => t.ApplyAsync(_rig.Org, 1,
            [new ImportApplyRow(2, Row(2, "INV-1", "2026-06-15", "1").Values, "UPDATE", first[0].EntityUuid)]));
        await update.Should().ThrowAsync<BadRequestException>().WithMessage("*never updated*");

        var inFile = await ValidateAsync(Row(2, "X-1", "2026-06-01", "1"), Row(3, "x-1", "2026-06-02", "2"));
        inFile[1].Errors.Should().ContainSingle(e => e.Code == "DUPLICATE_IN_FILE");
    }

    [Fact]
    public async Task One_bad_row_rolls_the_whole_batch_back()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");

        var act = () => ApplyAsync(Row(2, "A", "2026-06-15", "100"), Row(3, "B", "2026-07-01", "100"), Row(4, "C", "2026-06-15", "100"));
        await act.Should().ThrowAsync<BadRequestException>().WithMessage("Row 3: The date 2026-07-01 is after the opening balance date 2026-06-30.");

        var books = await _rig.Desk.BooksAsync(_customer);
        books.Invoices.Should().BeEmpty();
        books.Ledger.Should().BeEmpty();
    }

    [Fact]
    public async Task Rows_are_checked_against_customers_dates_amounts_and_currencies()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");
        _rig.Customer("C-INACT", active: false);
        _rig.Supplier("S-00001");

        var checks = await ValidateAsync(
            Row(2, "1", "2026-06-15", "10", code: "C-NOPE"),
            Row(3, "2", "2026-06-15", "10", code: "S-00001"),
            Row(4, "3", "2026-06-15", "10", code: "C-INACT"),
            Row(5, "4", "2026-02-30", "10"),
            Row(6, "5", "2026-06-15", "0"),
            Row(7, "6", "2026-06-15", "10.123"),
            Row(8, "7", "2026-06-15", "10", currency: "EUR"),
            Row(9, "8", "2026-06-15", "10", currency: "XYZ"),
            Row(10, "9", "2026-06-15", "10", due: "2026-06-01"),
            Row(11, "10", "2026-06-15", "10", currency: "usd"));

        checks.Select(c => c.Errors.Select(e => e.Code).FirstOrDefault()).Should().Equal(
            "LOOKUP_NOT_FOUND", FinanceImport.NotACustomer, FinanceImport.InactivePartner, "INVALID_DATE", "OUT_OF_RANGE",
            "INVALID_PRECISION", "LOOKUP_NOT_FOUND", "LOOKUP_NOT_FOUND", "INVALID_DATE", null);
    }

    [Fact]
    public async Task A_foreign_currency_balance_locks_the_rate_of_its_invoice_date_and_one_without_a_rate_is_refused()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");

        var outcome = await ApplyAsync(Row(2, "USD-1", "2026-06-15", "100", currency: "USD"));
        var invoice = (await _rig.Desk.BooksAsync(_customer)).Invoice(outcome[0].EntityUuid);
        (invoice.CurrencyCode, invoice.CurrencyId, invoice.ExchangeRate, invoice.BaseCurrencyCode, invoice.BaseGrandTotal)
            .Should().Be(("USD", FinanceImportRig.Usd, 280m, "PKR", 28000m));
        _rig.Currency.Locks.Should().Contain((_rig.Org, FinanceImportRig.Usd, new DateOnly(2026, 6, 15), TransactionDomain.Sale));
        (await _rig.Desk.BooksAsync(_customer)).Ledger.Single().CurrencyCode.Should().Be("USD");

        var checks = await ValidateAsync(Row(2, "USD-OLD", "2025-12-31", "100", currency: "USD"));
        checks.Single().Errors.Should().ContainSingle(e => e.Code == FinanceImport.NoExchangeRate && e.Message.Contains("2025-12-31"));
    }

    [Fact]
    public async Task Template_lists_carry_customer_codes_and_active_currencies_and_the_profile_is_an_opening_balance_without_updates()
    {
        var lists = await _rig.CustomerTargetAsync(t => t.GetTemplateListsAsync(_rig.Org));
        lists.Single(l => l.Key == C.CustomerList).Values.Should().Equal("C-00001");
        lists.Single(l => l.Key == C.CurrencyList).Values.Should().Equal("PKR", "USD");

        var profile = await _rig.CustomerTargetAsync(t => Task.FromResult(t.Describe()));
        (profile.IsOpeningBalance, profile.AllowUpdate).Should().Be((true, false));
        profile.MatchFields.Should().Equal(C.FCustomer, C.FReference);
    }

    [Fact]
    public async Task An_opening_invoice_is_never_sent_to_QuickBooks()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");
        var outcome = await ApplyAsync(Row(2, "INV-1", "2026-06-15", "1000"));

        var gateway = new RecordingQuickBooksGateway();
        await using var db = _rig.Db();
        var source = new SalesInvoiceQuickBooksSource(db, gateway, NullLogger<SalesInvoiceQuickBooksSource>.Instance);

        await source.PushAsync(SMS.Shared.Integration.QuickBooks.SyncKind.SalesInvoice, [outcome[0].EntityUuid.ToString()]);
        (await source.PushAllAsync(SMS.Shared.Integration.QuickBooks.SyncKind.SalesInvoice, null)).Should().Be(0);
        gateway.Calls.Should().BeEmpty();
    }
}

// ── SUPPLIER_OPENING_BALANCE ─────────────────────────────────────────────────

public sealed class SupplierOpeningBalanceImportTargetTests
{
    private readonly FinanceImportRig _rig = new();
    private readonly Guid _supplier;

    public SupplierOpeningBalanceImportTargetTests()
    {
        _supplier = _rig.Supplier("S-00001", "Steel Traders");
    }

    private static ImportApplyRow Row(int n, string? reference, string date, string amount, string? currency = null, string code = "S-00001") =>
        FinanceImportRig.Create(n, (S.FSupplier, code), (S.FReference, reference), (S.FDate, date), (S.FAmount, amount), (S.FCurrency, currency));

    private Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(params ImportApplyRow[] rows) =>
        _rig.SupplierTargetAsync(t => t.ApplyAsync(_rig.Org, FinanceImportRig.User, rows));

    [Fact]
    public async Task Each_row_becomes_an_approved_opening_bill_with_no_purchase_order_booked_on_the_supplier_and_master_ledgers()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");

        var outcome = await ApplyAsync(Row(2, "BILL-1", "2026-06-10", "8200"), Row(3, "BILL-2", "2026-06-12", "1800", currency: "USD"));

        await using var db = _rig.Db();
        var bills = await db.Invoices.AsNoTracking().Include(i => i.Lines).OrderBy(i => i.Id).ToListAsync();
        bills.Should().HaveCount(2);
        var b = bills[0];
        (b.UUID, b.SourceType, b.MatchStatus, b.PoUuid, b.GrnUuid, b.SupplierInvoiceNo, b.SupplierName, b.SupplierId)
            .Should().Be((outcome[0].EntityUuid, "OPENING", "Approved", (Guid?)null, (Guid?)null, "BILL-1", "Steel Traders", _supplier));
        (b.TotalAmount, b.PaidAmount, b.Currency, b.ExchangeRate, b.BaseTotalAmount).Should().Be((8200m, 0m, "PKR", 1m, 8200m));
        b.InvoiceDate.Should().Be(new DateTime(2026, 6, 10));
        b.DueDate.Should().Be(new DateTime(2026, 7, 10));
        (b.ApprovedBy, b.CreatedBy).Should().Be((FinanceImportRig.User, FinanceImportRig.User));
        b.Lines.Single().LineTotal.Should().Be(8200m);
        bills.Select(x => x.InvoiceNumber).Should().OnlyHaveUniqueItems();
        (bills[1].Currency, bills[1].ExchangeRate, bills[1].BaseTotalAmount).Should().Be(("USD", 280m, 504000m));
        _rig.Currency.Locks.Should().Contain((_rig.Org, FinanceImportRig.Usd, new DateOnly(2026, 6, 12), TransactionDomain.Purchase));

        var ledger = await db.SupplierLedgerEntries.AsNoTracking().Where(e => e.SupplierId == _supplier).OrderBy(e => e.SequenceNo).ToListAsync();
        ledger.Select(e => (e.SequenceNo, e.TransactionType, e.ReferenceId, e.DebitAmount, e.BalanceAfter)).Should().Equal(
            (1, "INVOICE_APPROVED", outcome[0].EntityUuid, 8200m, 8200m), (2, "INVOICE_APPROVED", outcome[1].EntityUuid, 1800m, 10000m));
        ledger[0].EntryDate.Should().Be(new DateTime(2026, 6, 10));

        var master = await db.MasterFinancialLedgers.AsNoTracking().OrderBy(e => e.SequenceNo).ToListAsync();
        master.Select(e => (e.SequenceNo, e.TransactionType, e.DebitAmount, e.BalanceAfter, e.SupplierName))
            .Should().Equal((1, "INVOICE_APPROVED", 8200m, 8200m, "Steel Traders"), (2, "INVOICE_APPROVED", 1800m, 10000m, "Steel Traders"));
    }

    [Fact]
    public async Task An_opening_bill_is_paid_by_a_supplier_payment_like_any_approved_invoice()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");
        var outcome = await ApplyAsync(Row(2, "BILL-1", "2026-06-10", "5000"));

        await _rig.PaySupplierAsync(_supplier, outcome[0].EntityUuid, 5000m);

        await using var db = _rig.Db();
        var bill = await db.Invoices.AsNoTracking().SingleAsync();
        (bill.PaidAmount, bill.PaymentStatus).Should().Be((5000m, "FULLY_PAID"));
        var balance = await new SupplierLedgerService(db).GetBalanceAsync(_supplier);
        balance.NetBalance.Should().Be(0m);
    }

    [Fact]
    public async Task Duplicates_non_vendors_and_late_dates_are_refused_and_the_batch_writes_nothing()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");
        var first = await ApplyAsync(Row(2, "BILL-1", "2026-06-10", "5000"));
        _rig.Customer("C-00009");

        var checks = await _rig.SupplierTargetAsync(t => t.ValidateAsync(_rig.Org,
        [
            FinanceImportRig.Input(2, (S.FSupplier, "s-00001"), (S.FReference, "bill-1"), (S.FDate, "2026-06-10"), (S.FAmount, "1")),
            FinanceImportRig.Input(3, (S.FSupplier, "C-00009"), (S.FReference, "X"), (S.FDate, "2026-06-10"), (S.FAmount, "1")),
            FinanceImportRig.Input(4, (S.FSupplier, "S-00001"), (S.FReference, "Y"), (S.FDate, "2026-07-10"), (S.FAmount, "1"))
        ]));
        checks[0].Errors.Should().ContainSingle(e => e.Code == "DUPLICATE_IN_DB");
        checks[0].ExistingEntityUuid.Should().Be(first[0].EntityUuid);
        checks[1].Errors.Should().ContainSingle(e => e.Code == FinanceImport.NotAVendor);
        checks[2].Errors.Should().ContainSingle(e => e.Code == FinanceImport.AfterOpeningDate);

        var act = () => ApplyAsync(Row(2, "BILL-2", "2026-06-11", "10"), Row(3, "BILL-1", "2026-06-10", "5000"));
        await act.Should().ThrowAsync<BadRequestException>().WithMessage("Row 3:*");

        await using var db = _rig.Db();
        (await db.Invoices.CountAsync()).Should().Be(1);
        (await db.SupplierLedgerEntries.CountAsync()).Should().Be(1);
        (await db.MasterFinancialLedgers.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_supplier_invoice_list_and_detail_say_it_is_an_opening_balance()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");
        var outcome = await ApplyAsync(Row(2, "BILL-1", "2026-06-10", "5000"));

        await using var db = _rig.Db();
        var demand = new SMS.Modules.Demand.Data.DemandDbContext(
            new DbContextOptionsBuilder<SMS.Modules.Demand.Data.DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = _rig.Org });
        var wh = new SMS.Modules.Warehouse.Data.WarehouseDbContext(
            new DbContextOptionsBuilder<SMS.Modules.Warehouse.Data.WarehouseDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = _rig.Org });
        var repo = new SMS.Modules.Finance.Repositories.InvoiceRepository(db, demand, wh, new SupplierLedgerService(db), new FakeSupplierNameLookup());

        var list = await repo.GetListAsync(new SMS.Modules.Finance.Models.InvoiceFilter());
        list.Data.Single().SourceType.Should().Be("OPENING");
        (await repo.GetByUuidAsync(outcome[0].EntityUuid))!.SourceType.Should().Be("OPENING");
    }

    [Fact]
    public async Task An_opening_bill_is_never_sent_to_QuickBooks()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");
        await ApplyAsync(Row(2, "BILL-1", "2026-06-10", "5000"));

        var gateway = new RecordingQuickBooksGateway();
        await using var db = _rig.Db();
        var source = new BillQuickBooksSource(db, new NoPoLines(), gateway, NullLogger<BillQuickBooksSource>.Instance);
        (await source.PushAllAsync(SMS.Shared.Integration.QuickBooks.SyncKind.Bill, null)).Should().Be(0);
        gateway.Calls.Should().BeEmpty();
    }

    private sealed class NoPoLines : IPurchaseOrderLineVariants
    {
        public Task<IReadOnlyDictionary<Guid, Guid?>> GetAsync(IReadOnlyCollection<Guid> poLineUuids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Guid?>>(new Dictionary<Guid, Guid?>());
    }
}
