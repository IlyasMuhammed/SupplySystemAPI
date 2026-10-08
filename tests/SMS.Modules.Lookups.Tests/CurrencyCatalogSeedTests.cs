using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Lookups.Data;
using SMS.Modules.Lookups.Domain;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Lookups.Tests;

/// <summary>
/// A35 P1-13 / D-1 — the global catalog gains whichever of the 18 spec seed codes (§2.3) it lacks, idempotently and
/// without touching existing rows; a same-named row with no code gets the code instead of a duplicate.
/// </summary>
public class CurrencyCatalogSeedTests
{
    private static LookupsDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<LookupsDbContext>().UseInMemoryDatabase(name).Options, new StaticTenantContext());

    [Fact]
    public async Task Missing_seed_codes_are_added_once_and_existing_rows_are_kept()
    {
        var name = Guid.NewGuid().ToString();
        var usdId = Guid.NewGuid();
        await using (var db = NewDb(name))
        {
            db.Currencies.AddRange(
                new Currency { Id = usdId, Name = "Dollar", Code = "usd", Symbol = "$" },
                new Currency { Id = Guid.NewGuid(), Name = "Euro", Code = null, Symbol = null });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb(name)) await new LookupsDataSeeder(db).SeedCurrenciesAsync();
        await using (var db = NewDb(name)) await new LookupsDataSeeder(db).SeedCurrenciesAsync();

        await using var check = NewDb(name);
        var rows = await check.Currencies.ToListAsync();
        rows.Should().HaveCount(18);
        rows.Single(c => c.Id == usdId).Should().BeEquivalentTo(new { Name = "Dollar", Code = "usd" }, "an existing row is never changed");
        rows.Single(c => c.Name == "Euro").Code.Should().Be("EUR", "a same-named row without a code gets it");
        rows.Single(c => c.Code == "CHF").Symbol.Should().Be("CHF");
        rows.Single(c => c.Code == "JPY").Name.Should().Be("Japanese Yen");
    }
}
