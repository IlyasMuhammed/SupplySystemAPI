using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

using static SMS.Modules.Finance.Tests.FinanceSetup.SetupDesk;

namespace SMS.Modules.Finance.Tests.FinanceSetup;

/// <summary>
/// What the in-memory provider cannot show: the real unique indexes (one code per organization; one live
/// rate per pair per day, freed by a soft delete) refusing a race the services' own checks lost and the
/// services turning that into a 409, the date column, and that every query here translates to SQL.
/// </summary>
public class FinanceSetupSqlServerTests
{
    private static ILookupsService Lookups()
    {
        var lookups = new Mock<ILookupsService>();
        lookups.Setup(l => l.GetCurrencies()).Returns(
        [
            new CurrencyModel { Id = SetupWorld.PkrId, Name = "Pakistani Rupee", Code = "PKR" },
            new CurrencyModel { Id = SetupWorld.UsdId, Name = "US Dollar",       Code = "USD" }
        ]);
        return lookups.Object;
    }

    [FinanceSqlServerFact]
    public async Task The_service_turns_a_lost_race_for_a_code_into_a_409()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true);
        var org = Guid.NewGuid();

        var interceptor = new InsertFirst(harness, org, "tax_codes", other =>
            other.TaxCodes.Add(new TaxCode { Uuid = Guid.NewGuid(), Code = "GST17", Name = "theirs", RatePercent = 17m, Usage = "SALES", CreatedBy = 2 }));
        await using var racing = harness.NewContext(org, interceptor);
        await using var demand = harness.NewDemandContext(org);

        var act = () => new TaxCodeService(racing, demand).CreateAsync(Code("GST17", 18m), 1);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*already a tax code GST17*");
        interceptor.Fired.Should().BeTrue();
        await using var check = harness.NewContext(org);
        (await check.TaxCodes.SingleAsync()).Name.Should().Be("theirs");
    }

    /// <summary>Just before the first INSERT into <c>table</c>, commits a competing row on its own connection.</summary>
    private sealed class InsertFirst : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private readonly FinanceSqlServerHarness _harness;
        private readonly Guid _org;
        private readonly string _table;
        private readonly Action<FinanceDbContext> _compete;

        public InsertFirst(FinanceSqlServerHarness harness, Guid org, string table, Action<FinanceDbContext> compete)
        {
            _harness = harness;
            _org     = org;
            _table   = table;
            _compete = compete;
        }

        public bool Fired { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken ct = default)
        {
            if (!Fired && command.CommandText.Contains($"INSERT INTO [finance].[{_table}]", StringComparison.OrdinalIgnoreCase))
            {
                Fired = true;
                await using var other = _harness.NewContext(_org);
                _compete(other);
                await other.SaveChangesAsync(ct);
            }
            return await base.ReaderExecutingAsync(command, eventData, result, ct);
        }
    }

    [FinanceSqlServerFact]
    public async Task Codes_are_unique_per_organization_only_and_a_duplicate_is_refused_by_the_index_and_by_the_service()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true);
        var acme   = Guid.NewGuid();
        var globex = Guid.NewGuid();

        await using (var db = harness.NewContext(acme))
        await using (var demand = harness.NewDemandContext(acme))
        {
            await new TaxCodeService(db, demand).CreateAsync(Code("GST17", 17m, "SALES", isDefault: true), 1);
        }
        await using (var db = harness.NewContext(globex))
        await using (var demand = harness.NewDemandContext(globex))
        {
            (await new TaxCodeService(db, demand).CreateAsync(Code("GST17", 16m), 1)).TaxCode.Code.Should().Be("GST17");
        }

        // The index refuses a second GST17 in Acme that slipped past the service's check.
        await using (var db = harness.NewContext(acme))
        {
            db.TaxCodes.Add(new TaxCode { Uuid = Guid.NewGuid(), Code = "GST17", Name = "dup", RatePercent = 1m, Usage = "SALES", CreatedBy = 1 });
            var act = () => db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }

        await using (var db = harness.NewContext(acme))
        await using (var demand = harness.NewDemandContext(acme))
        {
            var act = () => new TaxCodeService(db, demand).CreateAsync(Code("gst17", 18m), 1);
            await act.Should().ThrowAsync<ConflictException>();
        }
    }

    [FinanceSqlServerFact]
    public async Task Codes_from_rates_in_use_lookup_rename_guard_and_provider_run_on_sql_server()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true);
        var org = Guid.NewGuid();

        Guid lineUuid;
        await using (var demand = harness.NewDemandContext(org))
        {
            var order = new SaleOrder
            {
                SoNumber = "SO-2026-00001", TraceId = Guid.NewGuid(), PartnerId = Guid.NewGuid(), OrderDate = new DateTime(2026, 9, 1),
                CurrencyId = SetupWorld.PkrId, Status = "CONFIRMED", DeliveryMode = "SHIP", CreatedBy = 1
            };
            foreach (var rate in new[] { 17m, 7.5m, 17m })
                order.Lines.Add(new SaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 1m, UnitPrice = 10m, TaxPercent = rate, LineTotal = 10m, Status = "OPEN" });
            demand.SaleOrders.Add(order);
            await demand.SaveChangesAsync();
            lineUuid = order.Lines.First().UUID;
        }

        Guid tax17;
        await using (var db = harness.NewContext(org))
        await using (var demand = harness.NewDemandContext(org))
        {
            var svc     = new TaxCodeService(db, demand);
            var outcome = await svc.CreateFromRatesInUseAsync(1);
            outcome.Result.Created.Select(c => c.Code).Should().Equal("TAX7_5", "TAX17");
            tax17 = outcome.Result.Created.Single(c => c.Code == "TAX17").Uuid;

            (await svc.CreateFromRatesInUseAsync(1)).Result.Created.Should().BeEmpty();
        }

        await using (var demand = harness.NewDemandContext(org))
        {
            var line = await demand.SaleOrderLines.SingleAsync(l => l.UUID == lineUuid);
            line.TaxCodeUuid = tax17;
            await demand.SaveChangesAsync();
        }

        await using (var db = harness.NewContext(org))
        await using (var demand = harness.NewDemandContext(org))
        {
            var model = (await new TaxCodeService(db, demand).ListAsync("SALES", false)).Single(c => c.Uuid == tax17);
            var req   = From(model);
            req.Code  = "GST17";
            var act   = () => new TaxCodeService(db, demand).UpdateAsync(tax17, req, 1);
            await act.Should().ThrowAsync<ConflictException>();

            var lookup = new TaxCodeLookup(db);
            (await lookup.ListActiveAsync(TaxCodeUsage.Sales)).Select(c => c.Code).Should().Equal("TAX17", "TAX7_5");
            (await lookup.GetAsync(tax17))!.RatePercent.Should().Be(17m);
            (await lookup.GetDefaultAsync(TaxCodeUsage.Sales)).Should().BeNull();
        }

        await using (var db = harness.NewContext(org))
        {
            // A35-E-01: the provider reads finance.currency_rates (PKR is the rate currency).
            db.OrgCurrencies.AddRange(
                new OrgCurrency { OrganizationId = org, CurrencyId = SetupWorld.PkrId, Code = "PKR", Name = "Pakistani Rupee", Symbol = "Rs", DisplayOrder = 1 },
                new OrgCurrency { OrganizationId = org, CurrencyId = SetupWorld.UsdId, Code = "USD", Name = "US Dollar", Symbol = "$", DisplayOrder = 2 });
            db.CurrencyRates.AddRange(
                new CurrencyRate { OrganizationId = org, CurrencyId = SetupWorld.PkrId, CurrencyCode = "PKR", Rate = 1m, InverseRate = 1m, EffectiveFrom = CurrencyConventions.SystemStart, EffectiveTo = CurrencyConventions.OpenEnd, Source = "SYSTEM" },
                new CurrencyRate { OrganizationId = org, CurrencyId = SetupWorld.UsdId, CurrencyCode = "USD", Rate = 278m, InverseRate = 0.0035971223m, EffectiveFrom = new DateOnly(2026, 9, 1), EffectiveTo = new DateOnly(2026, 9, 30) },
                new CurrencyRate { OrganizationId = org, CurrencyId = SetupWorld.UsdId, CurrencyCode = "USD", Rate = 280m, InverseRate = 0.0035714286m, EffectiveFrom = new DateOnly(2026, 10, 1), EffectiveTo = CurrencyConventions.OpenEnd });
            await db.SaveChangesAsync();
        }
        await using (var db = harness.NewContext(org))
        {
            var provider = new ExchangeRateProvider(db);
            (await provider.GetRateAsync("usd", "pkr", new DateTime(2026, 9, 30, 23, 59, 0)))!.Rate.Should().Be(278m);
            (await provider.GetRateAsync("PKR", "USD", new DateTime(2026, 10, 1)))!.Should().Be(
                new ExchangeRateQuote("PKR", "USD", 0.0035714286m, new DateTime(2026, 10, 1), Inverted: false));
        }

        // The currency reference checker's queries translate too (UPPER() on SQL Server).
        await using (var db = harness.NewContext(org))
        {
            var checker = new FinanceCurrencyReferenceChecker(db, Lookups());
            checker.IsValueReferenced(SetupWorld.UsdId).Should().BeTrue();
            checker.IsValueReferenced(Guid.NewGuid()).Should().BeFalse();
        }
    }
}