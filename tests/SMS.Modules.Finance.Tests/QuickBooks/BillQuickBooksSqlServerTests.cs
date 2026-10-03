using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Finance.Tests.QuickBooks;

/// <summary>Counts every command a context sends to SQL Server.</summary>
internal sealed class CommandCounter : DbCommandInterceptor
{
    public List<string> Commands { get; } = [];

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    {
        Commands.Add(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, ct);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Commands.Add(command.CommandText);
        return base.ReaderExecuting(command, eventData, result);
    }
}

/// <summary>
/// What the in-memory provider cannot show: that a push of several supplier invoices resolves every one of
/// their purchase order lines in a single SQL command against Demand, and loads the invoices with their
/// lines in a single command against Finance — no query per invoice or per line.
/// </summary>
public class BillQuickBooksSqlServerTests
{
    [FinanceSqlServerFact]
    public async Task Pushing_three_invoices_of_six_PO_lines_is_one_command_to_Demand_and_one_to_Finance()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true);
        var org = Guid.NewGuid();
        var supplier = Guid.NewGuid();
        var variants = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();

        var po = new PurchaseOrder
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), PoNumber = "PO-2026-00001", SupplierId = supplier, SupplierName = "Karachi Steel",
            Status = "RECEIVED", IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        for (var i = 0; i < variants.Count; i++)
            po.Lines.Add(new PurchaseOrderLine
            {
                UUID = Guid.NewGuid(), LineNo = i + 1, VariantUuid = variants[i], ItemDescription = $"Item {i + 1}",
                UnitOfMeasure = "PC", Quantity = 10m, UnitPrice = 100m, LineTotal = 1000m
            });
        po.TotalAmount = po.Lines.Sum(l => l.LineTotal);
        await using (var demand = harness.NewDemandContext(org))
        {
            demand.PurchaseOrders.Add(po);
            await demand.SaveChangesAsync();
        }
        var poLines = po.Lines.OrderBy(l => l.LineNo).ToList();

        var invoices = Enumerable.Range(0, 3).Select(n =>
        {
            var invoice = new Invoice
            {
                UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), InvoiceNumber = $"INV-2026-0000{n + 1}", SupplierId = supplier,
                SupplierName = "Karachi Steel", PoUuid = po.UUID, PoNumber = po.PoNumber,
                InvoiceDate = new DateTime(2026, 9, 1), ReceivedDate = new DateTime(2026, 9, 1), DueDate = new DateTime(2026, 10, 1),
                Currency = "PKR", Subtotal = 2000m, TotalAmount = 2000m, MatchStatus = "Approved", PaymentStatus = "Unpaid",
                CreatedBy = 1, CreatedDate = DateTime.UtcNow
            };
            for (var l = 0; l < 2; l++)
                invoice.Lines.Add(new InvoiceLine
                {
                    UUID = Guid.NewGuid(), LineNo = l + 1, PoLineUuid = poLines[n * 2 + l].UUID, ItemDescription = $"Item {n * 2 + l + 1}",
                    QtyInvoiced = 10m, UnitPrice = 100m, LineTotal = 1000m
                });
            return invoice;
        }).ToList();
        await using (var finance = harness.NewContext(org))
        {
            finance.Invoices.AddRange(invoices);
            await finance.SaveChangesAsync();
        }

        var financeCommands = new CommandCounter();
        var demandCommands  = new CommandCounter();
        await using var financeDb = harness.NewContext(org, financeCommands);
        await using var demandDb  = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseSqlServer(harness.ConnectionString).AddInterceptors(demandCommands).Options,
            new StaticTenantContext { OrganizationId = org });
        var gateway = new RecordingQuickBooksGateway();
        var source  = new BillQuickBooksSource(financeDb, new DemandPurchaseOrderLineVariants(demandDb), gateway, new ListLogger<BillQuickBooksSource>());

        await source.PushAsync(SyncKind.Bill, invoices.Select(i => i.UUID.ToString()).ToList());

        gateway.Bills.Should().HaveCount(3);
        gateway.Bills.SelectMany(b => b.Lines).Select(l => l.ItemExternalId).Should().BeEquivalentTo(variants.Select(v => v.ToString()));
        demandCommands.Commands.Should().ContainSingle("every PO line of every invoice is resolved in one query");
        financeCommands.Commands.Should().ContainSingle("the invoices come with their lines in one query");

        demandCommands.Commands.Clear();
        financeCommands.Commands.Clear();
        (await source.PushAllAsync(SyncKind.Bill, null)).Should().Be(3);
        demandCommands.Commands.Should().ContainSingle("one batch, one PO-line query");
        financeCommands.Commands.Should().ContainSingle("one batch of invoices with their lines");
    }
}
