using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Repositories;
using SMS.Modules.Material.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Material.Tests;

// ── Test builder ──────────────────────────────────────────────────────────────

file static class Build
{
    internal static (MirRepository repo, MaterialDbContext material, DemandDbContext demand, InventoryDbContext inventory) NewMirRepo()
    {
        var material = new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        var demand = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        return (new MirRepository(material, inventory, demand, NullLogger<MirRepository>.Instance), material, demand, inventory);
    }

    internal static Product SeedProduct(InventoryDbContext db, Guid uuid, string name = "Lenovo Laptop")
    {
        var product = new Product
        {
            Uuid     = uuid,
            Sku      = $"SKU-{uuid:N}"[..10],
            Name     = name,
            UomCode  = "PC",
            UnitCost = 1000m,
            IsActive = true
        };
        db.Products.Add(product);
        db.SaveChanges();
        return product;
    }

    internal static (PurchaseRequisition pr, PrLine line) SeedPrLine(
        DemandDbContext db, Guid productUuid, decimal qty = 10m, string prStatus = "APPROVED", string itemDescription = "Lenovo Laptop")
    {
        var pr = new PurchaseRequisition
        {
            UUID          = Guid.NewGuid(),
            TraceId       = Guid.NewGuid(),
            PrNumber      = $"PR-{Guid.NewGuid():N}"[..12],
            PrTitle       = "Test Requisition",
            RequesterId   = 1,
            RequestedDate = DateTime.UtcNow,
            Status        = prStatus,
            CreatedBy     = 1,
            CreatedDate   = DateTime.UtcNow
        };
        var line = new PrLine
        {
            UUID            = Guid.NewGuid(),
            LineNo          = 1,
            ProductId       = productUuid,
            ItemDescription = itemDescription,
            Quantity        = qty
        };
        pr.Lines.Add(line);
        db.PurchaseRequisitions.Add(pr);
        db.SaveChanges();
        return (pr, line);
    }

    internal static CreateMirRequest DeptRequest(params CreateMirLineRequest[] lines) => new()
    {
        RequestType = "DEPARTMENT",
        Department  = "IT",
        Priority    = "MEDIUM",
        Lines       = [.. lines]
    };

    internal static CreateMirLineRequest Line(Guid productUuid, decimal qty = 2m, Guid? prLineId = null) => new()
    {
        ProductUuid  = productUuid,
        RequestedQty = qty,
        PrLineId     = prLineId
    };

    internal static (MirRepository repo, MaterialDbContext material, DemandDbContext demand, InventoryDbContext inventory, RecordingLogger<MirRepository> logger) NewMirRepoWithLogger()
    {
        var material = new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        var demand = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var logger = new RecordingLogger<MirRepository>();

        return (new MirRepository(material, inventory, demand, logger), material, demand, inventory, logger);
    }
}

// Minimal ILogger test double that records Warning-level messages for assertion.
file sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Warnings { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
    }

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

// ── TL-005: trace_id ─────────────────────────────────────────────────────────

public class Mir_TraceId_Tests
{
    [Fact]
    public async Task Creates_Mir_With_System_Generated_TraceId()
    {
        var (repo, material, _, inventory) = Build.NewMirRepo();
        var product = Build.SeedProduct(inventory, Guid.NewGuid());

        var uuid = await repo.CreateAsync(Build.DeptRequest(Build.Line(product.Uuid)), createdBy: 1);

        var mir = await material.MaterialIssueRequests.FirstAsync(m => m.UUID == uuid);
        mir.TraceId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task TraceId_Is_Present_In_GetByUuid_Response()
    {
        var (repo, _, _, inventory) = Build.NewMirRepo();
        var product = Build.SeedProduct(inventory, Guid.NewGuid());
        var uuid = await repo.CreateAsync(Build.DeptRequest(Build.Line(product.Uuid)), createdBy: 1);

        var detail = await repo.GetByUuidAsync(uuid);

        detail.Should().NotBeNull();
        detail!.TraceId.Should().NotBe(Guid.Empty);
    }

    // ── TL-006: propagation ─────────────────────────────────────────────────────

    [Fact]
    public async Task Mir_Linked_To_Pr_Line_Inherits_Pr_TraceId()
    {
        var (repo, material, demand, inventory) = Build.NewMirRepo();
        var product = Build.SeedProduct(inventory, Guid.NewGuid());
        var (pr, prLine) = Build.SeedPrLine(demand, product.Uuid);

        var uuid = await repo.CreateAsync(Build.DeptRequest(Build.Line(product.Uuid, prLineId: prLine.UUID)), createdBy: 1);

        var mir = await material.MaterialIssueRequests.FirstAsync(m => m.UUID == uuid);
        mir.TraceId.Should().Be(pr.TraceId);
    }

    [Fact]
    public async Task Mir_With_No_Linkage_Gets_Unique_TraceId()
    {
        var (repo, material, _, inventory) = Build.NewMirRepo();
        var product = Build.SeedProduct(inventory, Guid.NewGuid());

        var uuid = await repo.CreateAsync(Build.DeptRequest(Build.Line(product.Uuid, prLineId: null)), createdBy: 1);

        var mir = await material.MaterialIssueRequests.FirstAsync(m => m.UUID == uuid);
        mir.TraceId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Second_Mir_Against_Same_Pr_Line_Also_Inherits_TraceId()
    {
        var (repo, material, demand, inventory) = Build.NewMirRepo();
        var product = Build.SeedProduct(inventory, Guid.NewGuid());
        var (pr, prLine) = Build.SeedPrLine(demand, product.Uuid, qty: 100m);

        var uuid1 = await repo.CreateAsync(Build.DeptRequest(Build.Line(product.Uuid, qty: 1m, prLineId: prLine.UUID)), createdBy: 1);
        var uuid2 = await repo.CreateAsync(Build.DeptRequest(Build.Line(product.Uuid, qty: 1m, prLineId: prLine.UUID)), createdBy: 1);

        var mir1 = await material.MaterialIssueRequests.FirstAsync(m => m.UUID == uuid1);
        var mir2 = await material.MaterialIssueRequests.FirstAsync(m => m.UUID == uuid2);
        mir1.TraceId.Should().Be(pr.TraceId);
        mir2.TraceId.Should().Be(pr.TraceId);
    }

    [Fact]
    public async Task Mir_With_Lines_From_Two_Different_Chains_Uses_First_And_Logs_Warning()
    {
        var (repo, material, demand, inventory, logger) = Build.NewMirRepoWithLogger();
        var product1 = Build.SeedProduct(inventory, Guid.NewGuid(), "Lenovo Laptop");
        var product2 = Build.SeedProduct(inventory, Guid.NewGuid(), "Dell Monitor");
        var (pr1, prLine1) = Build.SeedPrLine(demand, product1.Uuid, itemDescription: "Lenovo Laptop");
        var (pr2, prLine2) = Build.SeedPrLine(demand, product2.Uuid, itemDescription: "Dell Monitor");

        var req = Build.DeptRequest(
            Build.Line(product1.Uuid, qty: 1m, prLineId: prLine1.UUID),
            Build.Line(product2.Uuid, qty: 1m, prLineId: prLine2.UUID));

        var uuid = await repo.CreateAsync(req, createdBy: 1);

        var mir = await material.MaterialIssueRequests.FirstAsync(m => m.UUID == uuid);
        mir.TraceId.Should().Be(pr1.TraceId, "the first linked line's PR trace_id wins");
        mir.TraceId.Should().NotBe(pr2.TraceId);
        logger.Warnings.Should().ContainSingle(w => w.Contains("different PR trace chains"));
    }
}

// ── Saving MIR lines with pr_line_id ───────────────────────────────────────────

public class MirLine_PrLineId_Tests
{
    [Fact]
    public async Task Saving_Line_With_Valid_PrLineId_Succeeds()
    {
        var (repo, material, demand, inventory) = Build.NewMirRepo();
        var product = Build.SeedProduct(inventory, Guid.NewGuid());
        var (_, prLine) = Build.SeedPrLine(demand, product.Uuid);

        var uuid = await repo.CreateAsync(Build.DeptRequest(Build.Line(product.Uuid, prLineId: prLine.UUID)), createdBy: 1);

        var mir = await material.MaterialIssueRequests.Include(m => m.Lines).FirstAsync(m => m.UUID == uuid);
        mir.Lines.Single().PrLineId.Should().Be(prLine.Id);
    }

    [Fact]
    public async Task Saving_Line_With_PrLineId_On_NonApproved_Pr_Throws_BadRequest()
    {
        var (repo, _, demand, inventory) = Build.NewMirRepo();
        var product = Build.SeedProduct(inventory, Guid.NewGuid());
        var (_, prLine) = Build.SeedPrLine(demand, product.Uuid, prStatus: "DRAFT");

        var act = () => repo.CreateAsync(Build.DeptRequest(Build.Line(product.Uuid, prLineId: prLine.UUID)), createdBy: 1);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*APPROVED*");
    }

    [Fact]
    public async Task Saving_Line_With_Unknown_PrLineId_Throws_BadRequest()
    {
        var (repo, _, _, inventory) = Build.NewMirRepo();
        var product = Build.SeedProduct(inventory, Guid.NewGuid());

        var act = () => repo.CreateAsync(Build.DeptRequest(Build.Line(product.Uuid, prLineId: Guid.NewGuid())), createdBy: 1);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Saving_Line_With_Null_PrLineId_Succeeds()
    {
        var (repo, material, _, inventory) = Build.NewMirRepo();
        var product = Build.SeedProduct(inventory, Guid.NewGuid());

        var uuid = await repo.CreateAsync(Build.DeptRequest(Build.Line(product.Uuid, prLineId: null)), createdBy: 1);

        var mir = await material.MaterialIssueRequests.Include(m => m.Lines).FirstAsync(m => m.UUID == uuid);
        mir.Lines.Single().PrLineId.Should().BeNull();
    }

    [Fact]
    public async Task Changing_PrLineId_On_Submitted_Mir_Throws_UnprocessableEntity()
    {
        var (repo, material, demand, inventory) = Build.NewMirRepo();
        var product = Build.SeedProduct(inventory, Guid.NewGuid());
        var (_, prLine) = Build.SeedPrLine(demand, product.Uuid);

        var uuid = await repo.CreateAsync(Build.DeptRequest(Build.Line(product.Uuid)), createdBy: 1);

        var mir = await material.MaterialIssueRequests.FirstAsync(m => m.UUID == uuid);
        mir.Status = "PENDING_APPROVAL";
        await material.SaveChangesAsync();

        var patchReq = new PatchMirRequest { Lines = [Build.Line(product.Uuid, prLineId: prLine.UUID)] };

        var act = () => repo.PatchAsync(uuid, patchReq, modifiedBy: 1);

        await act.Should().ThrowAsync<UnprocessableEntityException>()
            .WithMessage("*DRAFT*");
    }

    [Fact]
    public async Task Clearing_PrLineId_While_Draft_Succeeds()
    {
        var (repo, material, demand, inventory) = Build.NewMirRepo();
        var product = Build.SeedProduct(inventory, Guid.NewGuid());
        var (_, prLine) = Build.SeedPrLine(demand, product.Uuid);

        var uuid = await repo.CreateAsync(Build.DeptRequest(Build.Line(product.Uuid, prLineId: prLine.UUID)), createdBy: 1);

        await repo.PatchAsync(uuid, new PatchMirRequest { Lines = [Build.Line(product.Uuid, prLineId: null)] }, modifiedBy: 1);

        var mir = await material.MaterialIssueRequests.Include(m => m.Lines).FirstAsync(m => m.UUID == uuid);
        mir.Lines.Single().PrLineId.Should().BeNull();
    }
}

// ── GET /api/purchase-requisitions/search ──────────────────────────────────────

public class PrLookupService_Tests
{
    [Fact]
    public async Task Search_Returns_Only_Approved_Pr_Lines_Matching_Product()
    {
        var (_, material, demand, _) = Build.NewMirRepo();
        var productUuid = Guid.NewGuid();
        var (_, approvedLine) = Build.SeedPrLine(demand, productUuid, qty: 10m, prStatus: "APPROVED");
        Build.SeedPrLine(demand, productUuid, qty: 5m, prStatus: "DRAFT");
        Build.SeedPrLine(demand, Guid.NewGuid(), qty: 5m, prStatus: "APPROVED");

        var service = new PrLookupService(demand, material);
        var results = await service.SearchAsync(productUuid, "APPROVED");

        results.Should().ContainSingle();
        results[0].PrLineId.Should().Be(approvedLine.UUID);
        results[0].RemainingUndisbursedQty.Should().Be(10m);
    }

    [Fact]
    public async Task Search_Excludes_Fully_Disbursed_Lines()
    {
        var (_, material, demand, _) = Build.NewMirRepo();
        var productUuid = Guid.NewGuid();
        var (_, line) = Build.SeedPrLine(demand, productUuid, qty: 10m);

        material.MaterialIssueRequests.Add(new MaterialIssueRequest
        {
            UUID        = Guid.NewGuid(),
            RequestNo   = "MIR-2026-00001",
            RequestType = "DEPARTMENT",
            Department  = "IT",
            RequestedBy = 1,
            Status      = "APPROVED",
            CreatedBy   = 1,
            CreatedDate = DateTime.UtcNow,
            Lines =
            [
                new MaterialIssueRequestDetail
                {
                    UUID            = Guid.NewGuid(),
                    LineNo          = 1,
                    ProductUuid     = productUuid,
                    ItemDescription = "Lenovo Laptop",
                    RequestedQty    = 10m,
                    PrLineId        = line.Id
                }
            ]
        });
        await material.SaveChangesAsync();

        var service = new PrLookupService(demand, material);
        var results = await service.SearchAsync(productUuid, "APPROVED");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_Returns_Remaining_Qty_For_Partially_Disbursed_Lines()
    {
        var (_, material, demand, _) = Build.NewMirRepo();
        var productUuid = Guid.NewGuid();
        var (_, line) = Build.SeedPrLine(demand, productUuid, qty: 10m);

        material.MaterialIssueRequests.Add(new MaterialIssueRequest
        {
            UUID        = Guid.NewGuid(),
            RequestNo   = "MIR-2026-00002",
            RequestType = "DEPARTMENT",
            Department  = "IT",
            RequestedBy = 1,
            Status      = "APPROVED",
            CreatedBy   = 1,
            CreatedDate = DateTime.UtcNow,
            Lines =
            [
                new MaterialIssueRequestDetail
                {
                    UUID            = Guid.NewGuid(),
                    LineNo          = 1,
                    ProductUuid     = productUuid,
                    ItemDescription = "Lenovo Laptop",
                    RequestedQty    = 4m,
                    PrLineId        = line.Id
                }
            ]
        });
        await material.SaveChangesAsync();

        var service = new PrLookupService(demand, material);
        var results = await service.SearchAsync(productUuid, "APPROVED");

        results.Should().ContainSingle();
        results[0].RemainingUndisbursedQty.Should().Be(6m);
    }

    [Fact]
    public async Task Search_Ignores_Disbursement_From_Cancelled_Mir_Lines()
    {
        var (_, material, demand, _) = Build.NewMirRepo();
        var productUuid = Guid.NewGuid();
        var (_, line) = Build.SeedPrLine(demand, productUuid, qty: 10m);

        material.MaterialIssueRequests.Add(new MaterialIssueRequest
        {
            UUID        = Guid.NewGuid(),
            RequestNo   = "MIR-2026-00003",
            RequestType = "DEPARTMENT",
            Department  = "IT",
            RequestedBy = 1,
            Status      = "CANCELLED",
            CreatedBy   = 1,
            CreatedDate = DateTime.UtcNow,
            Lines =
            [
                new MaterialIssueRequestDetail
                {
                    UUID            = Guid.NewGuid(),
                    LineNo          = 1,
                    ProductUuid     = productUuid,
                    ItemDescription = "Lenovo Laptop",
                    RequestedQty    = 10m,
                    PrLineId        = line.Id
                }
            ]
        });
        await material.SaveChangesAsync();

        var service = new PrLookupService(demand, material);
        var results = await service.SearchAsync(productUuid, "APPROVED");

        results.Should().ContainSingle();
        results[0].RemainingUndisbursedQty.Should().Be(10m);
    }
}
