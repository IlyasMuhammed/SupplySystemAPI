using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Import;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests.Import;

/// <summary>A40 QA-R1 (TFIN) — the latest posted opening stock date of an organization, for Finance's opening-date guard.</summary>
public sealed class OpeningStockDateSourceTests
{
    private static InventoryLedgerEntry Entry(Guid org, string type, DateTime date) => new()
    {
        LedgerId = Guid.NewGuid(), OrganizationId = org, VariantId = 1, WarehouseId = 1, TransactionDate = date,
        TransactionType = type, ReferenceType = type, ReferenceId = Guid.NewGuid(), ReferenceNumber = "OPENING",
        QuantityIn = 1m, BalanceAfter = 1m, CreatedBy = 1, CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task It_answers_the_latest_OPENING_STOCK_date_of_that_organization_only_and_null_when_there_is_none()
    {
        var org   = Guid.NewGuid();
        var other = Guid.NewGuid();
        var name  = Guid.NewGuid().ToString();
        InventoryDbContext Db(Guid tenant) => new(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(name).Options,
            new StaticTenantContext { OrganizationId = tenant });

        await using (var seed = Db(org))
        {
            seed.InventoryLedgerEntries.AddRange(
                Entry(org, "OPENING_STOCK", new DateTime(2026, 6, 30)),
                Entry(org, "OPENING_STOCK", new DateTime(2026, 5, 31)),
                Entry(org, "GRN_RECEIPT", new DateTime(2026, 9, 1)),
                Entry(other, "OPENING_STOCK", new DateTime(2026, 12, 31)));
            await seed.SaveChangesAsync();
        }

        // Asked from a context of a different tenant: the organization given is what counts.
        await using var db = Db(Guid.NewGuid());
        IOpeningStockDateSource source = new OpeningStockDateSource(db);
        (await source.GetLatestAsync(org)).Should().Be(new DateOnly(2026, 6, 30));
        (await source.GetLatestAsync(other)).Should().Be(new DateOnly(2026, 12, 31));
        (await source.GetLatestAsync(Guid.NewGuid())).Should().BeNull();
    }
}
