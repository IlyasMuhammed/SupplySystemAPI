using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Finance.Controllers;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests.MultiCurrency;

/// <summary>
/// A35 P4-02 / BR-C7-04 / T-C8-06..08 — the unrealized revaluation: open foreign receivables and payables revalued at the
/// rate on the date vs the booked rate, into each document's own stored base; same-currency skipped; a missing rate skips
/// that document only; re-running a date replaces its rows; other organizations untouched; the job runs every organization.
/// </summary>
public class ExchangeRevaluationTests
{
    private static readonly Guid Pkr = ReceivablesCurrencyTests.Pkr;
    private static readonly Guid Usd = ReceivablesCurrencyTests.Usd;
    private static readonly Guid Eur = ReceivablesCurrencyTests.Eur;
    private static readonly Guid Aed = ReceivablesCurrencyTests.Aed;
    private static readonly DateOnly MonthEnd = new(2026, 10, 31);

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly FakeCurrencyService _fx = new FakeCurrencyService(Pkr, "PKR").Currency(Usd, "USD").Currency(Eur, "EUR").Currency(Aed, "AED");
    private readonly Mock<IOrganizationCurrencyService> _settings = new();

    public ExchangeRevaluationTests()
    {
        _settings.Setup(s => s.GetSettingsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((Guid org, CancellationToken _) => new OrgCurrencySettingsSnapshot(org, Pkr, Pkr, Pkr, Pkr, "7110", "7120", "7130", "7140", true));
    }

    private ExchangeRevaluationService Service(Guid org)
    {
        var db = Receivables.Db(org, _dbName);
        return new ExchangeRevaluationService(db, _fx, new ExchangeDifferenceWriter(db, _settings.Object));
    }

    private async Task ReceivableAsync(Guid org, string code, Guid currency, decimal balance, decimal bookedRate, Guid? baseId = null, string status = "ISSUED")
    {
        var i = Receivables.Invoice(org, Guid.NewGuid(), $"SINV-{Guid.NewGuid():N}"[..18], new DateTime(2026, 10, 1), balance, status: status, currency: code);
        i.CurrencyId = currency; i.ExchangeRate = bookedRate; i.BaseCurrencyId = baseId ?? Pkr; i.BaseCurrencyCode = baseId == Usd ? "USD" : "PKR";
        await using var db = Receivables.Db(org, _dbName);
        await Receivables.Seed(db, i);
    }

    private async Task PayableAsync(Guid org, string code, Guid currency, decimal total, decimal paid, decimal bookedRate, string status = "Approved")
    {
        var d = new DateTime(2026, 10, 1);
        await using var db = Receivables.Db(org, _dbName);
        await Receivables.Seed(db, new Invoice
        {
            UUID = Guid.NewGuid(), OrganizationId = org, InvoiceNumber = $"INV-{Guid.NewGuid():N}"[..16], SupplierName = "S", SupplierId = Guid.NewGuid(),
            Currency = code, CurrencyId = currency, TotalAmount = total, PaidAmount = paid, MatchStatus = status,
            ExchangeRate = bookedRate, BaseCurrencyId = Pkr, BaseCurrencyCode = "PKR",
            InvoiceDate = d, ReceivedDate = d, DueDate = d, CreatedDate = d
        });
    }

    private async Task<List<ExchangeDifference>> RowsAsync()
    {
        await using var db = Receivables.Auditor(_dbName);
        return await db.ExchangeDifferences.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
    }

    [Fact] // T-C8-06, T-C8-07, T-C8-08
    public async Task Open_foreign_documents_are_revalued_and_same_currency_ones_are_skipped()
    {
        _fx.Rate(Eur, 317.00m).Rate(Usd, 277.50m);
        await ReceivableAsync(_org, "EUR", Eur, 5_000m, 316.48m);
        await PayableAsync(_org, "USD", Usd, 10_000m, 0m, 278.05m);
        await ReceivableAsync(_org, "PKR", Pkr, 50_000m, 1m);

        var result = await Service(_org).RunAsync(_org, MonthEnd, userId: 7);

        (result.ReceivablesRevalued, result.PayablesRevalued, result.RowsWritten, result.RowsReplaced).Should().Be((1, 1, 2, 0));
        var rows = await RowsAsync();
        var ar = rows.Single(r => r.Side == "RECEIVABLE");
        (ar.Kind, ar.DifferenceBase, ar.AccountCode, ar.RevaluationDate, ar.SettlementRate, ar.PaymentType).Should().Be(
            ("UNREALIZED", 2_600m, "7130", (DateOnly?)MonthEnd, 317.00m, (string?)null));
        var ap = rows.Single(r => r.Side == "PAYABLE");
        (ap.DifferenceBase, ap.AccountCode).Should().Be((5_500m, "7130"), "owing USD that fell is a gain (see the T-C8-07 note)");
        result.Totals.Should().ContainSingle().Which.Should().Be(new RevaluationTotal("PKR", 8_100m, 0m, 8_100m));
    }

    [Fact] // §8.3 / BR-C7-04 — only what is still open is revalued
    public async Task Only_the_open_balance_is_revalued_and_closed_or_unlocked_documents_are_not()
    {
        _fx.Rate(Usd, 280m);
        await PayableAsync(_org, "USD", Usd, 1_000m, 600m, 278m);            // 400 open
        await PayableAsync(_org, "USD", Usd, 1_000m, 1_000m, 278m);          // paid
        await PayableAsync(_org, "USD", Usd, 1_000m, 0m, 278m, "Pending");   // not approved
        await ReceivableAsync(_org, "USD", Usd, 100m, 278m, status: "PAID");

        await Service(_org).RunAsync(_org, MonthEnd, null);

        var row = (await RowsAsync()).Single();
        (row.AmountCurrency, row.DifferenceBase).Should().Be((400m, -800m));
    }

    [Fact]
    public async Task Re_running_a_date_replaces_its_rows_and_another_date_keeps_its_own()
    {
        _fx.Rate(Eur, 317m);
        await ReceivableAsync(_org, "EUR", Eur, 5_000m, 316.48m);

        await Service(_org).RunAsync(_org, MonthEnd, null);
        _fx.Rate(Eur, 318m, new DateOnly(2026, 10, 15));
        var again = await Service(_org).RunAsync(_org, MonthEnd, null);
        await Service(_org).RunAsync(_org, new DateOnly(2026, 9, 30), null);

        again.RowsReplaced.Should().Be(1);
        var rows = await RowsAsync();
        rows.Where(r => r.RevaluationDate == MonthEnd).Should().ContainSingle().Which.DifferenceBase.Should().Be(7_600m);
        rows.Where(r => r.RevaluationDate == new DateOnly(2026, 9, 30)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_currency_with_no_rate_on_the_date_is_skipped_and_the_run_goes_on()
    {
        _fx.Rate(Usd, 280m);
        await ReceivableAsync(_org, "AED", Aed, 100m, 76m);
        await PayableAsync(_org, "USD", Usd, 100m, 0m, 278m);

        var result = await Service(_org).RunAsync(_org, MonthEnd, null);

        result.Skipped.Should().ContainSingle().Which.Reason.Should().StartWith("No exchange rate for AED on 2026-10-31");
        result.RowsWritten.Should().Be(1);
    }

    [Fact] // REV 2 — revalued into the document's own stored base, not the organization's current one
    public async Task A_document_is_revalued_into_its_own_booked_base()
    {
        _fx.Rate(Usd, 280m).Rate(Eur, 300m);
        await ReceivableAsync(_org, "EUR", Eur, 100m, 1.05m, baseId: Usd);   // booked in a USD base at 1.05 USD/EUR
        _fx.Bases[TransactionDomain.Sale] = Pkr;                              // today's sale base is PKR

        await Service(_org).RunAsync(_org, MonthEnd, null);

        var row = (await RowsAsync()).Single();
        (row.BaseCurrencyId, row.SettlementRate).Should().Be(((Guid?)Usd, Math.Round(300m / 280m, 10)));
        row.DifferenceBase.Should().Be(Math.Round(100m * Math.Round(300m / 280m, 10), 2) - 105m);
    }

    [Fact]
    public async Task Another_organizations_documents_are_never_revalued_into_this_one()
    {
        _fx.Rate(Eur, 317m);
        await ReceivableAsync(_other, "EUR", Eur, 5_000m, 316.48m);

        var result = await Service(_org).RunAsync(_org, MonthEnd, null);

        result.RowsWritten.Should().Be(0);
        (await RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_job_revalues_every_organization_under_its_own_tenant_scope()
    {
        _fx.Rate(Eur, 317m);
        await ReceivableAsync(_org, "EUR", Eur, 5_000m, 316.48m);
        await ReceivableAsync(_other, "EUR", Eur, 1_000m, 316.48m);
        var directory = new Mock<IOrganizationDirectory>();
        directory.Setup(d => d.GetOrganizationIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([_org, _other]);
        var scopes = new List<Guid?>();
        var revaluation = new Mock<IExchangeRevaluationService>();
        revaluation.Setup(r => r.RunAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), null, It.IsAny<CancellationToken>()))
                   .Returns((Guid org, DateOnly date, int? _, CancellationToken ct) =>
                   {
                       scopes.Add(HangfireTenantScope.OrganizationId);
                       return Service(org).RunAsync(org, date, null, ct);
                   });

        await new ExchangeRevaluationJob(revaluation.Object, NullLogger<ExchangeRevaluationJob>.Instance, directory.Object).RunAsync();

        scopes.Should().Equal(_org, _other);
        HangfireTenantScope.OrganizationId.Should().BeNull();
        (await RowsAsync()).Select(r => r.OrganizationId).Should().BeEquivalentTo([_org, _other]);
    }

    [Fact]
    public async Task The_register_lists_only_the_callers_organization_with_filters()
    {
        _fx.Rate(Eur, 317m);
        await ReceivableAsync(_org, "EUR", Eur, 5_000m, 316.48m);
        await ReceivableAsync(_other, "EUR", Eur, 1_000m, 316.48m);
        await Service(_org).RunAsync(_org, MonthEnd, null);
        await Service(_other).RunAsync(_other, MonthEnd, null);

        await using var db = new Data.FinanceDbContext(
            new DbContextOptionsBuilder<Data.FinanceDbContext>().UseInMemoryDatabase(_dbName).Options,
            new StaticTenantContext { OrganizationId = _org, IsSuperAdmin = true });
        var query = new ExchangeDifferenceQueryService(db);

        (await query.ListAsync(new ExchangeDifferenceFilter())).Data.Should().ContainSingle().Which.DifferenceBase.Should().Be(2_600m);
        (await query.ListAsync(new ExchangeDifferenceFilter { Kind = "realized" })).Data.Should().BeEmpty();
        (await query.ListAsync(new ExchangeDifferenceFilter { From = MonthEnd, To = MonthEnd, Side = "receivable" })).Data.Should().ContainSingle();
        (await query.ListAsync(new ExchangeDifferenceFilter { From = MonthEnd.AddDays(1) })).Data.Should().BeEmpty();
    }
}
