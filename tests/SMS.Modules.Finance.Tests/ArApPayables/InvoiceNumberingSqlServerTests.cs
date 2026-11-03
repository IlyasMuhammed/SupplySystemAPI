using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests.ArApPayables;

/// <summary>
/// A48 QA-2 — supplier invoices created at once (a person and the GRN's automatic invoice) used to take the same
/// INV-YYYY-NNNNN and the loser failed on the unique index (500). On LocalDB with the retrying strategy, as in the API.
/// </summary>
public class InvoiceNumberingSqlServerTests
{
    [FinanceSqlServerFact]
    public async Task Supplier_invoices_created_at_once_get_distinct_numbers_and_none_fails()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(retryOnFailure: true);
        var org = Guid.NewGuid();
        var supplier = Guid.NewGuid();
        const int racers = 8;

        var contexts = Enumerable.Range(0, racers).Select(_ => h.NewContext(org)).ToList();
        var demands  = Enumerable.Range(0, racers).Select(_ => h.NewDemandContext(org)).ToList();
        try
        {
            var creates = contexts.Select((fin, i) =>
            {
                var warehouse = new WarehouseDbContext(
                    new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
                    new StaticTenantContext { OrganizationId = org });
                var repo = new InvoiceRepository(fin, demands[i], warehouse, new SupplierLedgerService(fin), new FakeSupplierNameLookup());
                return Task.Run(() => repo.CreateAsync(new CreateInvoiceRequest
                {
                    SupplierId = supplier, SupplierInvoiceNo = $"RACE-{i}", Subtotal = 100m + i,
                    InvoiceDate = DateTime.UtcNow.Date, ReceivedDate = DateTime.UtcNow.Date, DueDate = DateTime.UtcNow.Date.AddDays(30)
                }, 7));
            }).ToList();

            var uuids = await Task.WhenAll(creates);

            uuids.Should().OnlyHaveUniqueItems().And.HaveCount(racers);
            await using var check = h.NewContext(org);
            var numbers = await check.Invoices.AsNoTracking().Select(i => i.InvoiceNumber).ToListAsync();
            numbers.Should().HaveCount(racers).And.OnlyHaveUniqueItems();
            numbers.Should().OnlyContain(n => n.StartsWith($"INV-{DateTime.UtcNow.Year}-") && n.Length == 14, "the format is unchanged");
            numbers.Order().Should().Equal(Enumerable.Range(1, racers).Select(n => $"INV-{DateTime.UtcNow.Year}-{n:D5}"));
        }
        finally
        {
            foreach (var c in contexts) await c.DisposeAsync();
            foreach (var d in demands) await d.DisposeAsync();
        }
    }
}
