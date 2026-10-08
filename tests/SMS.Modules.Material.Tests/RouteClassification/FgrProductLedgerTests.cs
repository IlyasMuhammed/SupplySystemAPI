using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Material.Tests.RouteClassification;

/// <summary>
/// A34 D-30 — every confirmed finished goods receipt (make-to-order, A30 make-to-shortage, standalone alike) puts its
/// quantity on the A29 product ledger as ADJUSTMENT / IN, at the unit cost the receipt books on the inventory ledger, inside
/// the receipt's own transaction. Without it a manufactured good's ledger holds nothing and its sales invoice is refused.
/// </summary>
public class FgrProductLedgerTests
{
    private static readonly DateTime Today = A34Harness.Today;

    [Fact]
    public async Task Each_confirmed_receipt_posts_an_adjustment_in_at_the_receipts_unit_cost_with_fgr_and_po_references()
    {
        var ledger = new Mock<IProductLedgerService>();
        var postings = new List<ProductLedgerPosting>();
        ledger.Setup(l => l.AppendEntryAsync(It.IsAny<ProductLedgerPosting>(), It.IsAny<System.Data.Common.DbTransaction?>()))
              .Callback((ProductLedgerPosting p, System.Data.Common.DbTransaction? _) => postings.Add(p))
              .ReturnsAsync((ProductLedgerPosting p, System.Data.Common.DbTransaction? _) =>
                  new ProductLedgerPosted(Guid.NewGuid(), 1, p.VariantUuid, p.ProductUuid ?? Guid.Empty, p.Direction, p.Quantity, p.UnitCost ?? 0m, 0m, 0m, 0m, 0m));
        var h = new A34Harness(s => s.AddSingleton(ledger.Object));
        var (kit, kitV) = h.Product("Bolt Kit", manufactured: true);
        var (_, rodV)   = h.Product("Steel Rod", manufactured: false);
        await h.ActiveBomAsync(kit, 1m, (rodV, 1m, 0m));
        h.Stock(rodV, h.Plant, 100m);
        h.Stock(kitV, h.Plant, 1m);   // the output row exists with unit cost 3, which the receipt books

        var po = await h.Orders.CreateAsync(new CreateProductionOrderRequest
        {
            ProductUuid = kit, PlannedQuantity = 5m, WarehouseUuid = h.Plant, RequiredDate = Today.AddDays(3), Plan = true
        }, A34Harness.Author);
        await h.Orders.StartAsync(po, A34Harness.Author);
        await h.Orders.ReportOutputAsync(po, new ReportOutputRequest { Quantity = 5m }, A34Harness.Author);
        await h.Orders.CompleteAsync(po, A34Harness.Author);
        await h.Qi.CreateAsync(po, new CreateQualityInspectionRequest
        {
            Lines = [new CreateQualityInspectionLineRequest { CheckName = "Visual", Result = "PASS", QuantityChecked = 5m }]
        }, A34Harness.Operator);

        var draft = await h.Fgr.CreateAsync(po, new CreateFinishedGoodsReceiptRequest { Quantity = 2m }, A34Harness.Operator);
        postings.Should().BeEmpty("a draft receipt moves nothing");
        await h.Fgr.ConfirmAsync(draft, A34Harness.Operator);
        await h.Fgr.CreateAsync(po, new CreateFinishedGoodsReceiptRequest { Quantity = 3m, Confirm = true }, A34Harness.Operator);

        var order = await h.Orders.GetByUuidAsync(po);
        var fgrs  = await h.Fgr.GetForOrderAsync(po);
        postings.Should().HaveCount(2);
        postings.Select(p => p.Quantity).Should().Equal(2m, 3m);
        postings.Should().OnlyContain(p =>
            p.VariantUuid == kitV && p.ProductUuid == kit &&
            p.EntryType == ProductLedgerEntryTypes.Adjustment && p.Direction == ProductLedgerDirections.In &&
            p.UnitCost == 3m && p.ReferenceType == "FGR" && p.CreatedBy == A34Harness.Operator && p.PartnerId == null &&
            p.Narration!.Contains(order!.ProductionNumber));
        postings.Select(p => p.ReferenceNumber).Should().BeEquivalentTo(fgrs.Select(f => f.FgrNumber));
        postings.Select(p => p.ReferenceId).Should().BeEquivalentTo(fgrs.Select(f => f.UUID));
    }

    [Fact]
    public async Task A_new_output_row_with_no_cost_yet_posts_at_zero_and_a_host_without_finance_still_receives()
    {
        var ledger = new Mock<IProductLedgerService>();
        var h = new A34Harness(s => s.AddSingleton(ledger.Object));
        var plain = new A34Harness();   // no IProductLedgerService registered
        foreach (var harness in new[] { h, plain })
        {
            var (kit, _)    = harness.Product("Bolt Kit", manufactured: true);
            var (_, rodV)   = harness.Product("Steel Rod", manufactured: false);
            await harness.ActiveBomAsync(kit, 1m, (rodV, 1m, 0m));
            harness.Stock(rodV, harness.Plant, 100m);
            var po = await harness.Orders.CreateAsync(new CreateProductionOrderRequest
            {
                ProductUuid = kit, PlannedQuantity = 1m, WarehouseUuid = harness.Plant, RequiredDate = Today.AddDays(3), Plan = true
            }, A34Harness.Author);
            await harness.Orders.StartAsync(po, A34Harness.Author);
            await harness.Orders.ReportOutputAsync(po, new ReportOutputRequest { Quantity = 1m }, A34Harness.Author);
            await harness.Orders.CompleteAsync(po, A34Harness.Author);
            await harness.Qi.CreateAsync(po, new CreateQualityInspectionRequest
            {
                Lines = [new CreateQualityInspectionLineRequest { CheckName = "Visual", Result = "PASS", QuantityChecked = 1m }]
            }, A34Harness.Operator);
            await harness.Fgr.CreateAsync(po, new CreateFinishedGoodsReceiptRequest { Quantity = 1m, Confirm = true }, A34Harness.Operator);
            (await harness.Orders.GetByUuidAsync(po))!.Status.Should().Be("COMPLETED");
        }

        ledger.Verify(l => l.AppendEntryAsync(It.Is<ProductLedgerPosting>(p => p.UnitCost == 0m && p.Quantity == 1m), null), Times.Once);
    }
}
