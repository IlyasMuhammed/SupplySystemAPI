using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

using C = SMS.Modules.Finance.Import.CustomerOpeningBalanceImportTarget;
using S = SMS.Modules.Finance.Import.SupplierOpeningBalanceImportTarget;

namespace SMS.Modules.Finance.Tests.DataImport;

// ── A42 D-11: the opening balance date is locked by IOpeningDateLockSource ─────

public sealed class OpeningDateLockTests
{
    private readonly FinanceImportRig _rig = new();

    private sealed class FakeLock(Guid org, DateOnly? locked, string description = "General ledger opening balances") : IOpeningDateLockSource
    {
        public int Calls { get; private set; }
        public string Description => description;
        public Task<DateOnly?> GetLockedDateAsync(Guid organizationId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(organizationId == org ? locked : null);
        }
    }

    private OpeningBalanceDateService Service(SMS.Modules.Finance.Data.FinanceDbContext db, params IOpeningDateLockSource[] locks) =>
        new(db, _rig.World.Clock, openingStock: null, lockSources: locks);

    [Fact]
    public async Task A_locked_date_cannot_be_cleared_or_moved_either_way_but_saving_the_same_date_is_fine()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");
        await using var db = _rig.Db();
        var service = Service(db, new FakeLock(_rig.Org, new DateOnly(2026, 6, 30)));

        const string message = "The opening balance date is locked by General ledger opening balances posted as at 2026-06-30.";
        await service.Invoking(s => s.SetAsync(null, 1)).Should().ThrowAsync<ConflictException>().WithMessage(message);
        await service.Invoking(s => s.SetAsync(new DateOnly(2026, 7, 31), 1)).Should().ThrowAsync<ConflictException>().WithMessage(message);
        await service.Invoking(s => s.SetAsync(new DateOnly(2026, 6, 1), 1)).Should().ThrowAsync<ConflictException>().WithMessage(message);
        (await service.SetAsync(new DateOnly(2026, 6, 30), 1)).Should().Be(new DateOnly(2026, 6, 30));
        (await service.GetAsync()).Should().Be(new DateOnly(2026, 6, 30), "nothing moved");
    }

    [Fact]
    public async Task Sources_that_lock_nothing_or_lock_another_organization_change_nothing()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");
        await using var db = _rig.Db();
        var service = Service(db, new FakeLock(_rig.Org, null), new FakeLock(Guid.NewGuid(), new DateOnly(2026, 6, 30)));

        (await service.SetAsync(new DateOnly(2026, 7, 31), 1)).Should().Be(new DateOnly(2026, 7, 31));
        (await service.SetAsync(null, 1)).Should().BeNull();
    }

    [Fact]
    public async Task Every_registered_source_is_consulted_and_the_one_that_locks_names_itself()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");
        await using var db = _rig.Db();
        var first  = new FakeLock(_rig.Org, null, "Something else");
        var second = new FakeLock(_rig.Org, new DateOnly(2026, 6, 30), "Fixed assets opening register");
        var service = Service(db, first, second);

        await service.Invoking(s => s.SetAsync(null, 1)).Should().ThrowAsync<ConflictException>()
            .WithMessage("The opening balance date is locked by Fixed assets opening register posted as at 2026-06-30.");
        first.Calls.Should().Be(1);
    }

    [Fact]
    public async Task The_existing_opening_document_checks_still_apply_alongside_a_lock_source()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");
        _rig.Customer("C-00001");
        await _rig.CustomerTargetAsync(t => t.ApplyAsync(_rig.Org, FinanceImportRig.User,
            [FinanceImportRig.Create(2, (C.FCustomer, "C-00001"), (C.FReference, "OLD-1"), (C.FDate, "2026-06-20"), (C.FAmount, "100"))]));

        await using var db = _rig.Db();
        var service = Service(db, new FakeLock(_rig.Org, null));
        await service.Invoking(s => s.SetAsync(null, 1)).Should().ThrowAsync<ConflictException>().WithMessage("*cannot be cleared*");
        await service.Invoking(s => s.SetAsync(new DateOnly(2026, 6, 19), 1)).Should().ThrowAsync<ConflictException>().WithMessage("*2026-06-20*");
    }
}

// ── A42 D-12: sub-ledger opening totals ──────────────────────────────────────

public sealed class OpeningSubledgerTotalsTests
{
    private readonly FinanceImportRig _rig = new();

    private async Task<OpeningSubledgerTotals> TotalsAsync(Guid? org = null)
    {
        await using var db = _rig.Db();
        IOpeningSubledgerTotals service = new OpeningSubledgerTotalsService(db, _rig.Currency, new OrgCurrencyLookup(db), _rig.World.Clock);
        return await service.GetAsync(org ?? _rig.Org);
    }

    [Fact]
    public async Task Nothing_imported_is_all_zero_in_the_sale_base_currency()
    {
        var totals = await TotalsAsync();
        totals.Should().Be(new OpeningSubledgerTotals(0m, 0m, 0m, "PKR"));
    }

    [Fact]
    public async Task Customer_total_is_the_original_base_amount_of_live_opening_invoices_whatever_has_been_paid_since()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");
        _rig.Customer("C-00001");
        await _rig.CustomerTargetAsync(t => t.ApplyAsync(_rig.Org, FinanceImportRig.User,
        [
            FinanceImportRig.Create(2, (C.FCustomer, "C-00001"), (C.FReference, "A"), (C.FDate, "2026-06-20"), (C.FAmount, "1000")),
            FinanceImportRig.Create(3, (C.FCustomer, "C-00001"), (C.FReference, "B"), (C.FDate, "2026-06-21"), (C.FAmount, "10"), (C.FCurrency, "USD")),
            FinanceImportRig.Create(4, (C.FCustomer, "C-00001"), (C.FReference, "C"), (C.FDate, "2026-06-22"), (C.FAmount, "500"))
        ]));

        await using (var db = _rig.Db())
        {
            var invoices = await db.SalesInvoices.OrderBy(i => i.Id).ToListAsync();
            invoices[0].AmountPaid = 400m; invoices[0].BalanceDue = 600m; invoices[0].Status = SalesInvoiceStatuses.PartiallyPaid;
            invoices[2].Status = SalesInvoiceStatuses.Cancelled;
            await db.SaveChangesAsync();
        }

        var totals = await TotalsAsync();
        totals.CustomerOpeningTotal.Should().Be(1000m + 2800m, "original amounts, USD 10 at the locked 280, cancelled excluded");
        totals.SupplierOpeningTotal.Should().Be(0m);
        totals.CurrencyCode.Should().Be("PKR");
        (await TotalsAsync(Guid.NewGuid())).CustomerOpeningTotal.Should().Be(0m, "another organization's are not counted");
    }

    [Fact]
    public async Task Supplier_total_is_the_original_base_amount_of_unreversed_opening_bills()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");
        var supplier = _rig.Supplier("S-00001");
        var outcome = await _rig.SupplierTargetAsync(t => t.ApplyAsync(_rig.Org, FinanceImportRig.User,
        [
            FinanceImportRig.Create(2, (S.FSupplier, "S-00001"), (S.FReference, "B1"), (S.FDate, "2026-06-10"), (S.FAmount, "8200")),
            FinanceImportRig.Create(3, (S.FSupplier, "S-00001"), (S.FReference, "B2"), (S.FDate, "2026-06-12"), (S.FAmount, "1800"), (S.FCurrency, "USD")),
            FinanceImportRig.Create(4, (S.FSupplier, "S-00001"), (S.FReference, "B3"), (S.FDate, "2026-06-13"), (S.FAmount, "999"))
        ]));
        await _rig.PaySupplierAsync(supplier, outcome[0].EntityUuid, 2000m);

        await using (var db = _rig.Db())
        {
            var reversed = await db.Invoices.SingleAsync(i => i.UUID == outcome[2].EntityUuid);
            reversed.MatchStatus = InvoiceMatchStatus.Reversed;
            await db.SaveChangesAsync();
        }

        (await TotalsAsync()).SupplierOpeningTotal.Should().Be(8200m + 504_000m, "original amounts; the payment and the reversed bill don't count");
    }

    [Fact]
    public async Task Supplier_bills_in_a_different_purchase_base_are_converted_to_the_sale_base_at_the_opening_date()
    {
        await _rig.SetOpeningDateAsync("2026-06-30");
        _rig.Supplier("S-00001");
        await _rig.SupplierTargetAsync(t => t.ApplyAsync(_rig.Org, FinanceImportRig.User,
            [FinanceImportRig.Create(2, (S.FSupplier, "S-00001"), (S.FReference, "B1"), (S.FDate, "2026-06-10"), (S.FAmount, "100"), (S.FCurrency, "USD"))]));

        // As if the purchase base were USD: the bill's base amount is USD 100.
        await using (var db = _rig.Db())
        {
            var bill = await db.Invoices.SingleAsync();
            (bill.BaseCurrencyId, bill.BaseCurrencyCode, bill.BaseTotalAmount, bill.ExchangeRate) = (FinanceImportRig.Usd, "USD", 100m, 1m);
            await db.SaveChangesAsync();
        }

        (await TotalsAsync()).SupplierOpeningTotal.Should().Be(28_000m, "USD 100 at 280 on 2026-06-30");
    }

    [Fact]
    public async Task Opening_stock_value_is_the_total_cost_of_OPENING_STOCK_product_ledger_entries_only()
    {
        var other = Guid.NewGuid();
        await using (var db = _rig.Db())
        {
            db.ProductLedgerEntries.AddRange(
                Entry(_rig.Org, 1, ProductLedgerEntryTypes.OpeningStock, 10m, 150m),
                Entry(_rig.Org, 2, ProductLedgerEntryTypes.OpeningStock, 3m, 83.5m),
                Entry(_rig.Org, 3, ProductLedgerEntryTypes.Purchase, 5m, 100m),
                Entry(other, 1, ProductLedgerEntryTypes.OpeningStock, 1m, 9999m));
            await db.SaveChangesAsync();
        }

        (await TotalsAsync()).OpeningStockValue.Should().Be(1500m + 250.5m);
    }

    private static ProductLedgerEntry Entry(Guid org, int seq, string type, decimal qty, decimal unitCost) => new()
    {
        UUID = Guid.NewGuid(), OrganizationId = org, VariantUuid = Guid.NewGuid(), ProductUuid = Guid.NewGuid(), SequenceNo = seq,
        EntryDate = new DateTime(2026, 6, 30), EntryType = type, ReferenceType = "OPENING_STOCK", ReferenceId = Guid.NewGuid(),
        ReferenceNumber = $"OS-{seq}", Quantity = qty, UnitCost = unitCost, TotalCost = qty * unitCost,
        Direction = ProductLedgerDirections.In, RunningQty = qty, RunningValue = qty * unitCost, CreatedDate = DateTime.UtcNow
    };
}
