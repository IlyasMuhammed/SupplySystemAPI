using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Finance.Tests.MultiCurrency.CurrencyWorld;

namespace SMS.Modules.Finance.Tests.MultiCurrency;

/// <summary>
/// A35 on a real SQL Server (LocalDB, throwaway database): T-C2-10 (two concurrent inserts for the same currency and day →
/// one succeeds, one 409; never an overlap) and the bootstrapper run by two "instances" at once (applock per org: one set
/// of rows, participants after the legacy conversion every time).
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class CurrencySqlServerTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A35_FinCur_{Guid.NewGuid():N};";
    private readonly CurrencyWorld _w = new();

    private FinanceDbContext Db(Guid org) => new(
        new DbContextOptionsBuilder<FinanceDbContext>()
            .UseSqlServer(_connection, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(200), null)).Options,
        new StaticTenantContext { OrganizationId = org });

    public async Task InitializeAsync()
    {
        await using var db = Db(Guid.Empty);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await using var db = Db(Guid.Empty);
        await db.Database.EnsureDeletedAsync();
    }

    private async Task<Guid> OrgAsync()
    {
        var org = Guid.NewGuid();
        _w.Settings.Set(org, Pkr, Pkr, Pkr, Pkr);
        await using var db = Db(org);
        await new CurrencyBootstrapper(db, _w.Lookups().Object, [], _w.Settings).EnsureAsync(org);
        return org;
    }

    [Fact]
    public async Task T_C2_10_two_concurrent_inserts_for_the_same_day_one_wins_one_409()
    {
        var org = await OrgAsync();

        for (var round = 0; round < 5; round++)
        {
            var day = new DateOnly(2026, 10, 1).AddDays(round);
            var attempts = Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
            {
                await using var db = Db(org);
                try
                {
                    await new CurrencyRateService(db, _w.Settings).InsertAsync(new SaveCurrencyRateRequest
                    {
                        CurrencyId = Usd, Rate = 278m + i, EffectiveFrom = day.ToString("yyyy-MM-dd")
                    }, User);
                    return "ok";
                }
                catch (ConflictException)
                {
                    return "409";
                }
            })).ToArray();

            var outcomes = await Task.WhenAll(attempts);
            outcomes.Should().BeEquivalentTo(new[] { "ok", "409" }, $"round {round}");
        }

        await using var check = Db(org);
        var rows = await check.CurrencyRates.Where(r => r.OrganizationId == org && r.CurrencyId == Usd)
            .OrderBy(r => r.EffectiveFrom).ToListAsync();
        rows.Should().HaveCount(5);
        for (var i = 0; i < rows.Count - 1; i++)
            rows[i].EffectiveTo.Should().Be(rows[i + 1].EffectiveFrom.AddDays(-1), "no gaps, no overlaps");
        rows[^1].EffectiveTo.Should().Be(CurrencyConventions.OpenEnd);
    }

    private sealed class SlowRecorder : ICurrencyRatesReadyParticipant
    {
        private readonly Func<FinanceDbContext> _db;
        public SlowRecorder(Func<FinanceDbContext> db) => _db = db;
        public List<int> RatesSeen { get; } = [];
        public async Task OnCurrencyRatesReadyAsync(Guid organizationId, CancellationToken ct = default)
        {
            await using var db = _db();
            var n = await db.CurrencyRates.CountAsync(r => r.OrganizationId == organizationId, ct);
            lock (RatesSeen) RatesSeen.Add(n);
            await Task.Delay(300, ct);
        }
    }

    [Fact]
    public async Task Two_instances_bootstrapping_at_once_produce_one_set_of_rows_and_participants_follow_the_conversion()
    {
        var org = Guid.NewGuid();
        _w.Settings.Set(org, Pkr, Pkr, Pkr, Pkr);
        await using (var seed = Db(org))
        {
            seed.ExchangeRates.AddRange(
                new ExchangeRate { OrganizationId = org, FromCurrencyCode = "USD", ToCurrencyCode = "PKR", Rate = 277m, EffectiveDate = new DateTime(2026, 9, 1), CreatedBy = 1 },
                new ExchangeRate { OrganizationId = org, FromCurrencyCode = "EUR", ToCurrencyCode = "PKR", Rate = 300m, EffectiveDate = new DateTime(2026, 9, 1), CreatedBy = 1 });
            await seed.SaveChangesAsync();
        }

        var recorder = new SlowRecorder(() => Db(org));
        var instances = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await using var db = Db(org);
            await new CurrencyBootstrapper(db, _w.Lookups().Object, [recorder], _w.Settings).EnsureForAllAsync([org]);
        })).ToArray();
        await Task.WhenAll(instances);

        await using var check = Db(org);
        (await check.OrgCurrencies.CountAsync(c => c.OrganizationId == org)).Should().Be(6);
        (await check.CurrencyRates.CountAsync(r => r.OrganizationId == org)).Should().Be(3, "SYSTEM + USD + EUR, once");
        recorder.RatesSeen.Should().Equal(3, 3);
    }
}
