using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

/// <summary>
/// Stands in for SMS.Modules.Inventory's resolver. Logistics only knows the SMS.Shared contract.
/// </summary>
internal sealed class FakeVariantResolver : IProductVariantResolver
{
    private readonly Dictionary<Guid, DefaultVariantResult> _byProduct = [];
    private readonly Dictionary<Guid, VariantDescription>   _byVariant = [];

    internal Guid AddProductWithDefaultVariant()
    {
        var productUuid = Guid.NewGuid();
        _byProduct[productUuid] = new DefaultVariantResult(
            productUuid, Guid.NewGuid(), "SKU-001", "Default");
        return productUuid;
    }

    /// <summary>A variant a sale order line can point at, as the catalogue would describe it.</summary>
    internal Guid AddVariant(
        string sku, string productName, string variantName = "Default", bool isDefault = true, string? uom = "EA")
    {
        var variantUuid = Guid.NewGuid();
        _byVariant[variantUuid] = new VariantDescription(
            variantUuid, Guid.NewGuid(), sku, variantName, productName, isDefault, uom);
        return variantUuid;
    }

    public Task<IReadOnlyDictionary<Guid, VariantDescription>> DescribeVariantsAsync(
        IReadOnlyList<Guid> variantUuids)
    {
        IReadOnlyDictionary<Guid, VariantDescription> found = variantUuids
            .Where(_byVariant.ContainsKey)
            .Distinct()
            .ToDictionary(id => id, id => _byVariant[id]);

        return Task.FromResult(found);
    }

    public Task<IReadOnlyDictionary<Guid, DefaultVariantResult>> ResolveDefaultVariantsAsync(
        IReadOnlyList<Guid> productUuids)
    {
        IReadOnlyDictionary<Guid, DefaultVariantResult> found = productUuids
            .Where(_byProduct.ContainsKey)
            .Distinct()
            .ToDictionary(id => id, id => _byProduct[id]);

        return Task.FromResult(found);
    }
}

// T-12 — deliveries from a supplier return, a material issue voucher, and warehouse transfers.
public class DeliveryFromSroMivTransferTests
{
    private const int User = 42;

    private sealed record Harness(
        LogisticsDbContext Db,
        WarehouseDbContext Warehouse,
        MaterialDbContext Material,
        FakeVariantResolver Variants,
        DeliveryFromSourceRepository Repo,
        DeliveryRepository Deliveries);

    private static Harness NewHarness()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };

        DbContextOptions<T> Options<T>() where T : DbContext =>
            new DbContextOptionsBuilder<T>().UseInMemoryDatabase(dbName).Options;

        var demand    = new DemandDbContext(Options<DemandDbContext>(), tenant);
        var warehouse = new WarehouseDbContext(Options<WarehouseDbContext>(), tenant);
        var material  = new MaterialDbContext(Options<MaterialDbContext>(), tenant);
        var db        = LogisticsTestDb.Open(dbName, tenant);

        var numbers   = new DocumentNumberGenerator(db, tenant);
        var addresses = new AddressNormalizer(new FakeCityLookup());
        var variants  = new FakeVariantResolver();

        return new Harness(db, warehouse, material, variants,
            new DeliveryFromSourceRepository(
                db, demand, warehouse, material, numbers, addresses, variants, new FakeStockReservationService()),
            new DeliveryRepository(db, numbers, addresses));
    }

    // ── SRO ──────────────────────────────────────────────────────────────────

    private static SupplierReturnOrder SeedSro(
        Harness h, string status = "APPROVED", Guid? productUuid = null, decimal qty = 15m)
    {
        var sro = new SupplierReturnOrder
        {
            UUID          = Guid.NewGuid(),
            ReturnNumber  = "SRO-2026-00007",
            SroType       = "POST_RECEIPT_DEFECT",
            SupplierId    = Guid.NewGuid(),
            SupplierName  = "Acme Supplies",
            WarehouseUuid = Guid.NewGuid(),
            ReturnReason  = "DAMAGED",
            Status        = status,
            CreatedBy     = 1,
            CreatedDate   = DateTime.UtcNow
        };

        sro.Lines.Add(new SupplierReturnOrderLine
        {
            UUID            = Guid.NewGuid(),
            LineNo          = 1,
            ProductUuid     = productUuid,
            ItemDescription = "4mm cable",
            UnitOfMeasure   = "M",
            QtyToReturn     = qty,
            ReturnReason    = "DAMAGED",
            UnitCost        = 250m
        });

        h.Warehouse.SupplierReturnOrders.Add(sro);
        h.Warehouse.SaveChanges();
        h.Warehouse.ChangeTracker.Clear();
        return sro;
    }

    private static CreateDeliveryFromSourceRequest Request(string type, Guid uuid) =>
        new() { SourceType = type, SourceUuid = uuid };

    [Fact]
    public async Task An_approved_supplier_return_becomes_an_outbound_delivery()
    {
        var h   = NewHarness();
        var sro = SeedSro(h, productUuid: h.Variants.AddProductWithDefaultVariant());

        var detail = await h.Deliveries.GetByUuidAsync(
            await h.Repo.CreateFromSourceAsync(Request("SRO", sro.UUID), User));

        detail!.Direction.Should().Be("OUTBOUND");
        detail.SourceType.Should().Be("SRO");
        detail.SourceNumber.Should().Be("SRO-2026-00007");
        detail.PostsGoodsIssue.Should().BeFalse(
            "SroRepository.DispatchAsync already writes a RETURN_DISPATCH movement");
        detail.Lines.Single().QtyOrdered.Should().Be(15m);
    }

    [Fact]
    public async Task A_supplier_return_ships_from_the_warehouse_it_was_raised_against()
    {
        var h   = NewHarness();
        var sro = SeedSro(h, productUuid: h.Variants.AddProductWithDefaultVariant());

        var uuid = await h.Repo.CreateFromSourceAsync(Request("SRO", sro.UUID), User);

        (await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid))
            .ShipFromWarehouseUuid.Should().Be(sro.WarehouseUuid);
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("REJECTED")]
    [InlineData("DISPATCHED")]
    [InlineData("SUPPLIER_RECEIVED")]
    public async Task A_supplier_return_that_is_not_approved_is_rejected(string status)
    {
        var h   = NewHarness();
        var sro = SeedSro(h, status, h.Variants.AddProductWithDefaultVariant());

        var act = async () => await h.Repo.CreateFromSourceAsync(Request("SRO", sro.UUID), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage($"*{status}*")
            .WithMessage("*APPROVED*");
    }

    // ── F8 — product-scoped SRO lines against variant-level stock ────────────

    [Fact]
    public async Task A_supplier_return_line_is_resolved_to_the_products_default_variant()
    {
        // SRO lines carry a ProductUuid while PO and MIV lines carry a VariantUuid. Stock is
        // variant-level, so the delivery resolves it the same way SroRepository.DispatchAsync
        // does — otherwise the delivery and the dispatch could disagree about which variant moved.
        var h           = NewHarness();
        var productUuid = h.Variants.AddProductWithDefaultVariant();
        var sro         = SeedSro(h, productUuid: productUuid);

        var detail = await h.Deliveries.GetByUuidAsync(
            await h.Repo.CreateFromSourceAsync(Request("SRO", sro.UUID), User));

        var line = detail!.Lines.Single();
        line.ProductUuid.Should().Be(productUuid, "the product is kept as well as the variant");
        line.VariantUuid.Should().NotBeNull().And.NotBe(Guid.Empty);
    }

    [Fact]
    public async Task A_product_with_no_default_variant_leaves_the_variant_explicitly_unset()
    {
        // Not silently null: the product is still on the line, so picking (T-24) can report a
        // line it cannot resolve instead of quietly skipping it — which is how SroRepository's
        // own `if (variant is null) continue` loses a movement today.
        var h   = NewHarness();
        var sro = SeedSro(h, productUuid: Guid.NewGuid()); // never registered with the resolver

        var detail = await h.Deliveries.GetByUuidAsync(
            await h.Repo.CreateFromSourceAsync(Request("SRO", sro.UUID), User));

        var line = detail!.Lines.Single();
        line.VariantUuid.Should().BeNull();
        line.ProductUuid.Should().NotBeNull("the line must still say what it is");
        line.QtyOrdered.Should().Be(15m, "the quantity is not lost");
    }

    [Fact]
    public async Task A_supplier_return_with_nothing_to_return_is_rejected()
    {
        var h   = NewHarness();
        var sro = SeedSro(h, productUuid: h.Variants.AddProductWithDefaultVariant(), qty: 0m);

        var act = async () => await h.Repo.CreateFromSourceAsync(Request("SRO", sro.UUID), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*no lines*");
    }

    [Fact]
    public async Task An_unknown_supplier_return_is_not_found()
    {
        var h = NewHarness();

        var act = async () => await h.Repo.CreateFromSourceAsync(Request("SRO", Guid.NewGuid()), User);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ── MIV ──────────────────────────────────────────────────────────────────

    private static MaterialIssueVoucher SeedMiv(Harness h, string status = "POSTED", decimal qty = 25m)
    {
        var mir = new MaterialIssueRequest
        {
            UUID        = Guid.NewGuid(),
            TraceId     = Guid.NewGuid(),
            RequestNo   = "MIR-2026-00003",
            RequestType = "PROJECT",
            RequestedBy = 1,
            Status      = "APPROVED",
            CreatedBy   = 1,
            CreatedDate = DateTime.UtcNow
        };
        h.Material.MaterialIssueRequests.Add(mir);
        h.Material.SaveChanges();

        var miv = new MaterialIssueVoucher
        {
            UUID        = Guid.NewGuid(),
            IssueNo     = "MIV-2026-00011",
            MirId       = mir.Id,
            Status      = status,
            IssuedTo    = "Site A",
            IssueDate   = new DateTime(2026, 9, 1),
            CreatedBy   = 1,
            CreatedDate = DateTime.UtcNow
        };

        miv.Lines.Add(new MaterialIssueVoucherLine
        {
            UUID            = Guid.NewGuid(),
            InventoryItemId = 1,
            VariantUuid     = Guid.NewGuid(),
            ItemDescription = "Junction box",
            UnitOfMeasure   = "EA",
            IssuedQty       = qty,
            UnitCost        = 400m,
            LineValue       = qty * 400m
        });

        h.Material.MaterialIssueVouchers.Add(miv);
        h.Material.SaveChanges();
        h.Material.ChangeTracker.Clear();
        return miv;
    }

    [Fact]
    public async Task A_posted_material_issue_becomes_an_outbound_delivery_to_site()
    {
        var h   = NewHarness();
        var miv = SeedMiv(h);

        var detail = await h.Deliveries.GetByUuidAsync(
            await h.Repo.CreateFromSourceAsync(Request("MIV", miv.UUID), User));

        detail!.Direction.Should().Be("OUTBOUND");
        detail.SourceType.Should().Be("MIV");
        detail.SourceNumber.Should().Be("MIV-2026-00011");
        detail.PostsGoodsIssue.Should().BeFalse("the MIV already deducted stock when it was posted");
        detail.Lines.Single().QtyOrdered.Should().Be(25m);
        detail.Lines.Single().VariantUuid.Should().NotBeNull("MIV lines are already variant-scoped");
    }

    [Fact]
    public async Task A_material_issue_delivery_traces_back_to_its_request()
    {
        var h   = NewHarness();
        var miv = SeedMiv(h);

        var mir = await h.Material.MaterialIssueRequests.SingleAsync();
        var detail = await h.Deliveries.GetByUuidAsync(
            await h.Repo.CreateFromSourceAsync(Request("MIV", miv.UUID), User));

        detail!.TraceId.Should().Be(mir.TraceId);
        detail.RequestedDate.Should().Be(new DateTime(2026, 9, 1));
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("CANCELLED")]
    public async Task A_material_issue_that_has_not_been_posted_is_rejected(string status)
    {
        var h   = NewHarness();
        var miv = SeedMiv(h, status);

        var act = async () => await h.Repo.CreateFromSourceAsync(Request("MIV", miv.UUID), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage($"*{status}*")
            .WithMessage("*POSTED*");
    }

    [Fact]
    public async Task An_unknown_material_issue_is_not_found()
    {
        var h = NewHarness();

        var act = async () => await h.Repo.CreateFromSourceAsync(Request("MIV", Guid.NewGuid()), User);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ── TRANSFER ─────────────────────────────────────────────────────────────

    private static CreateDeliveryRequest TransferRequest(Guid? from, Guid? to) => new()
    {
        SourceType            = "TRANSFER",
        ShipFromWarehouseUuid = from,
        ShipToWarehouseUuid   = to,
        Lines = [new CreateDeliveryLineRequest
        {
            ItemDescription = "4mm cable", UnitOfMeasure = "M",
            QtyOrdered = 50m, VariantUuid = Guid.NewGuid()
        }]
    };

    [Fact]
    public async Task A_transfer_between_two_warehouses_posts_its_own_movement()
    {
        var h    = NewHarness();
        var from = Guid.NewGuid();
        var to   = Guid.NewGuid();

        var uuid   = await h.Deliveries.CreateAsync(TransferRequest(from, to), User);
        var detail = await h.Deliveries.GetByUuidAsync(uuid);

        detail!.Direction.Should().Be("TRANSFER");
        detail.PostsGoodsIssue.Should().BeTrue("nothing else records a warehouse transfer");

        var stored = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
        stored.ShipFromWarehouseUuid.Should().Be(from);
        stored.ShipToWarehouseUuid.Should().Be(to);
    }

    [Fact]
    public async Task A_transfer_to_the_same_warehouse_is_rejected()
    {
        var h    = NewHarness();
        var same = Guid.NewGuid();

        var act = async () => await h.Deliveries.CreateAsync(TransferRequest(same, same), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*must differ*");
    }

    [Theory]
    [InlineData(true,  false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task A_transfer_missing_either_warehouse_is_rejected(bool hasFrom, bool hasTo)
    {
        var h = NewHarness();

        var act = async () => await h.Deliveries.CreateAsync(
            TransferRequest(hasFrom ? Guid.NewGuid() : null, hasTo ? Guid.NewGuid() : null), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*both*");
    }

    [Fact]
    public async Task A_non_transfer_delivery_does_not_require_warehouses()
    {
        var h = NewHarness();
        var req = TransferRequest(null, null);
        req.SourceType = "MANUAL";
        req.Direction  = "OUTBOUND";

        var act = async () => await h.Deliveries.CreateAsync(req, User);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("TRANSFER")]
    [InlineData("MANUAL")]
    public async Task Source_types_with_no_source_document_are_pointed_at_the_right_endpoint(string type)
    {
        var h = NewHarness();

        var act = async () => await h.Repo.CreateFromSourceAsync(Request(type, Guid.NewGuid()), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*no source document*")
            .WithMessage("*POST /api/logistics/deliveries*");
    }

    // ── TC-12.9 — the double-deduction regression guard ─────────────────────

    [Fact]
    public async Task Every_source_type_carries_the_posting_rule_declared_in_the_vocabulary()
    {
        // The tripwire. If a delivery ever disagrees with DeliverySourceTypeInfo about whether it
        // posts stock, either MIV/SRO stock is deducted twice or a transfer is never deducted at
        // all — and both are silent.
        var h = NewHarness();

        var created = new Dictionary<string, Guid>
        {
            ["SRO"] = await h.Repo.CreateFromSourceAsync(
                Request("SRO", SeedSro(h, productUuid: h.Variants.AddProductWithDefaultVariant()).UUID), User),
            ["MIV"] = await h.Repo.CreateFromSourceAsync(Request("MIV", SeedMiv(h).UUID), User),
            ["TRANSFER"] = await h.Deliveries.CreateAsync(
                TransferRequest(Guid.NewGuid(), Guid.NewGuid()), User)
        };

        foreach (var (sourceCode, uuid) in created)
        {
            var expected = DeliverySourceTypeInfo.PostsGoodsIssue(
                LogisticsCode.Parse<DeliverySourceType>(sourceCode));

            var detail = await h.Deliveries.GetByUuidAsync(uuid);

            detail!.SourceType.Should().Be(sourceCode);
            detail.PostsGoodsIssue.Should().Be(expected,
                $"a {sourceCode} delivery must agree with DeliverySourceTypeInfo");
        }
    }
}
