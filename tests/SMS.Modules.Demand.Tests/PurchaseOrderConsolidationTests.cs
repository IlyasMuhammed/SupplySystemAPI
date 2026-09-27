using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Repositories;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

// A31-C3/BR-C3-07 — the Supply Requirement Engine's own "raise a PO for a purchased shortage" path
// consolidates onto whatever open PRODUCTION-sourced Draft PO already exists for the supplier,
// rather than raising a second PO every time. Exercised directly against the repository, the same
// way PurchaseOrderTests.cs already covers every other PO creation path.
public class PurchaseOrderConsolidationTests
{
    private static (PurchaseOrderRepository repo, DemandDbContext db) NewRepo()
    {
        var opts = new DbContextOptionsBuilder<DemandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new DemandDbContext(opts, new StaticTenantContext());
        return (new PurchaseOrderRepository(db, NullLogger<PurchaseOrderRepository>.Instance, MockNumbers()), db);
    }

    private static IDocumentNumberGenerator MockNumbers()
    {
        var seq = 0;
        var numbers = new Mock<IDocumentNumberGenerator>();
        numbers.Setup(n => n.NextAsync(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => $"PO-2026-{(++seq):D5}");
        return numbers.Object;
    }

    private static CreatePoRequest ProductionRequest(Guid supplierId, Guid variantUuid, decimal qty, string desc = "Steel Rod") => new()
    {
        SupplierId = supplierId, SupplierName = "Acme Supplies", Source = "PRODUCTION",
        Lines = [new CreatePoLineRequest { VariantUuid = variantUuid, ItemDescription = desc, UnitOfMeasure = "PCS", Quantity = qty, UnitPrice = 5m }]
    };

    [Fact]
    public async Task NoExistingDraft_CreatesANewPoSourcedAsProduction()
    {
        var (repo, db) = NewRepo();
        var supplierId = Guid.NewGuid();
        var variantUuid = Guid.NewGuid();

        var result = await repo.AddOrIncreaseProductionLineAsync(ProductionRequest(supplierId, variantUuid, 10m), createdBy: 1);

        result.IsNewPo.Should().BeTrue();
        result.LineQuantity.Should().Be(10m);
        var po = await db.PurchaseOrders.Include(p => p.Lines).SingleAsync(p => p.UUID == result.PoUuid);
        po.Source.Should().Be("PRODUCTION");
        po.Status.Should().Be("DRAFT");
        po.Lines.Single().UUID.Should().Be(result.LineUuid);
    }

    [Fact]
    public async Task ASecondShortage_ForTheSameSupplierAndVariant_BumpsTheSameLineInPlace()
    {
        var (repo, db) = NewRepo();
        var supplierId = Guid.NewGuid();
        var variantUuid = Guid.NewGuid();

        var first  = await repo.AddOrIncreaseProductionLineAsync(ProductionRequest(supplierId, variantUuid, 10m), createdBy: 1);
        var second = await repo.AddOrIncreaseProductionLineAsync(ProductionRequest(supplierId, variantUuid, 4m), createdBy: 1);

        second.IsNewPo.Should().BeFalse("the same open draft should be reused");
        second.PoUuid.Should().Be(first.PoUuid);
        second.LineUuid.Should().Be(first.LineUuid, "bumping in place preserves the line's own uuid, so an earlier shortage's own SupplySourceLineUuid link stays valid");
        second.LineQuantity.Should().Be(14m, "10 from the first shortage plus 4 from the second");

        var po = await db.PurchaseOrders.Include(p => p.Lines).SingleAsync(p => p.UUID == first.PoUuid);
        po.Lines.Should().ContainSingle();
        po.TotalAmount.Should().Be(14m * 5m);
    }

    [Fact]
    public async Task ASecondShortage_ForTheSameSupplierButADifferentVariant_AddsALineToTheSamePo()
    {
        var (repo, db) = NewRepo();
        var supplierId = Guid.NewGuid();
        var variantA = Guid.NewGuid();
        var variantB = Guid.NewGuid();

        var first  = await repo.AddOrIncreaseProductionLineAsync(ProductionRequest(supplierId, variantA, 10m, "Steel Rod"), createdBy: 1);
        var second = await repo.AddOrIncreaseProductionLineAsync(ProductionRequest(supplierId, variantB, 6m, "Packaging Box"), createdBy: 1);

        second.IsNewPo.Should().BeFalse();
        second.PoUuid.Should().Be(first.PoUuid);
        second.LineUuid.Should().NotBe(first.LineUuid);

        var po = await db.PurchaseOrders.Include(p => p.Lines).SingleAsync(p => p.UUID == first.PoUuid);
        po.Lines.Should().HaveCount(2);
        po.TotalAmount.Should().Be(10m * 5m + 6m * 5m);
    }

    [Fact]
    public async Task AShortage_ForADifferentSupplier_GetsItsOwnSeparatePo()
    {
        var (repo, db) = NewRepo();
        var variantUuid = Guid.NewGuid();

        var first  = await repo.AddOrIncreaseProductionLineAsync(ProductionRequest(Guid.NewGuid(), variantUuid, 10m), createdBy: 1);
        var second = await repo.AddOrIncreaseProductionLineAsync(ProductionRequest(Guid.NewGuid(), variantUuid, 10m), createdBy: 1);

        second.IsNewPo.Should().BeTrue();
        second.PoUuid.Should().NotBe(first.PoUuid);
        (await db.PurchaseOrders.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task AManuallyCreatedDraftForTheSameSupplier_IsNotAppendedTo()
    {
        // A hand-made MANUAL draft for the same supplier must not silently gain a production line —
        // only a PO this same mechanism raised (Source=PRODUCTION) is a valid consolidation target.
        var (repo, db) = NewRepo();
        var supplierId = Guid.NewGuid();
        var manual = await repo.CreateAsync(new CreatePoRequest
        {
            SupplierId = supplierId, SupplierName = "Acme Supplies",
            Lines = [new CreatePoLineRequest { VariantUuid = Guid.NewGuid(), ItemDescription = "Hand-picked item", UnitOfMeasure = "PCS", Quantity = 1, UnitPrice = 1m }]
        }, createdBy: 1);

        var result = await repo.AddOrIncreaseProductionLineAsync(ProductionRequest(supplierId, Guid.NewGuid(), 10m), createdBy: 1);

        result.IsNewPo.Should().BeTrue();
        result.PoUuid.Should().NotBe(manual);
        (await db.PurchaseOrders.CountAsync()).Should().Be(2);
    }

    // ── GetLastSupplierForVariantAsync (A31-C3 §5.4 tier 2) ──────────────────

    [Fact]
    public async Task NoPurchaseHistory_ReturnsNull()
    {
        var (repo, _) = NewRepo();
        (await repo.GetLastSupplierForVariantAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task OnlyADraftPo_DoesNotCountAsPurchaseHistory()
    {
        var (repo, _) = NewRepo();
        var variantUuid = Guid.NewGuid();
        await repo.CreateAsync(new CreatePoRequest
        {
            SupplierId = Guid.NewGuid(), SupplierName = "Never Sent",
            Lines = [new CreatePoLineRequest { VariantUuid = variantUuid, ItemDescription = "X", UnitOfMeasure = "PCS", Quantity = 1, UnitPrice = 1m }]
        }, createdBy: 1);

        (await repo.GetLastSupplierForVariantAsync(variantUuid)).Should().BeNull("a Draft PO may never actually be sent — it is not real purchase history yet");
    }

    [Fact]
    public async Task AnApprovedPo_IsReturnedAsTheLastSupplier()
    {
        var (repo, db) = NewRepo();
        var variantUuid = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var poUuid = await repo.CreateAsync(new CreatePoRequest
        {
            SupplierId = supplierId, SupplierName = "Reliable Supplies",
            Lines = [new CreatePoLineRequest { VariantUuid = variantUuid, ItemDescription = "X", UnitOfMeasure = "PCS", Quantity = 1, UnitPrice = 1m }]
        }, createdBy: 1);
        (await db.PurchaseOrders.SingleAsync(p => p.UUID == poUuid)).Status = "APPROVED";
        await db.SaveChangesAsync();

        (await repo.GetLastSupplierForVariantAsync(variantUuid)).Should().Be(supplierId);
    }

    [Fact]
    public async Task ACancelledPo_IsExcludedEvenThoughItOnceExisted()
    {
        var (repo, db) = NewRepo();
        var variantUuid = Guid.NewGuid();
        var poUuid = await repo.CreateAsync(new CreatePoRequest
        {
            SupplierId = Guid.NewGuid(), SupplierName = "Cancelled Deal",
            Lines = [new CreatePoLineRequest { VariantUuid = variantUuid, ItemDescription = "X", UnitOfMeasure = "PCS", Quantity = 1, UnitPrice = 1m }]
        }, createdBy: 1);
        (await db.PurchaseOrders.SingleAsync(p => p.UUID == poUuid)).Status = "CANCELLED";
        await db.SaveChangesAsync();

        (await repo.GetLastSupplierForVariantAsync(variantUuid)).Should().BeNull();
    }
}
