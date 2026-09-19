using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SMS.Modules.Logistics.Domain;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-07 — the four-layer schema.
//
// Several rules here are asserted against the EF model rather than by provoking a database error:
// the in-memory provider does not enforce unique indexes, foreign keys or cascade constraints.
// Checking the model is still a real test of intent — it fails if someone drops an index — and
// the behaviour those constraints produce is covered once the services exist (T-10 onward).
public class SchemaTests
{
    private static readonly Type[] NewEntities =
    [
        typeof(Address),
        typeof(DeliveryOrder), typeof(DeliveryOrderLine),
        typeof(ShipmentPackage), typeof(PackageContent),
        typeof(Consignment), typeof(ConsignmentDelivery), typeof(ConsignmentStop)
    ];

    private static IModel Model()
    {
        var (db, _, _) = LogisticsTestDb.New();
        return db.Model;
    }

    // ── TC-07.1 — every table is present, in the right schema ────────────────

    [Fact]
    public void All_four_layers_are_mapped_into_the_logistics_schema()
    {
        var model = Model();

        string[] expected =
        [
            "addresses",
            "delivery_orders", "delivery_order_lines",
            "shipment_packages", "package_contents",
            "consignments", "consignment_deliveries", "consignment_stops"
        ];

        foreach (var table in expected)
        {
            var entity = model.GetEntityTypes().SingleOrDefault(e => e.GetTableName() == table);

            entity.Should().NotBeNull($"'{table}' must be mapped");
            (entity!.GetSchema() ?? model.GetDefaultSchema()).Should().Be("logistics");
        }
    }

    [Fact]
    public void The_model_is_valid_for_sql_server_not_just_the_in_memory_provider()
    {
        // The in-memory provider ignores column types, filtered indexes and most relational
        // configuration, so a model that works in these tests can still fail to build a
        // migration. Building it under the real provider catches that here rather than in T-08.
        //
        // What this does NOT catch: SQL Server rejects multiple cascade paths at CREATE TABLE
        // time, not at model-build time. That only surfaces when the migration is applied.
        var options = new DbContextOptionsBuilder<SMS.Modules.Logistics.Data.LogisticsDbContext>()
            .UseSqlServer("Server=none;Database=none;Trusted_Connection=True;")
            .Options;

        using var db = new SMS.Modules.Logistics.Data.LogisticsDbContext(
            options, new StaticTenantContext());

        var act = () => db.Model.GetEntityTypes().ToList();

        act.Should().NotThrow("the model must be buildable by the SQL Server provider");
    }

    // ── TC-07.8 — the legacy model is untouched ──────────────────────────────

    [Fact]
    public void The_legacy_carrier_and_shipment_tables_still_map()
    {
        // SMS.Modules.Reports queries LogisticsDbContext.Shipments directly. Renaming or
        // re-mapping either of these would break that module, which is why layer C is called
        // Consignment rather than Shipment.
        var model = Model();

        model.FindEntityType(typeof(Carrier))!.GetTableName().Should().Be("carriers");
        model.FindEntityType(typeof(Shipment))!.GetTableName().Should().Be("shipments");
    }

    [Fact]
    public async Task The_legacy_shipment_still_round_trips()
    {
        var (db, _, _) = LogisticsTestDb.New();

        db.Shipments.Add(TestData.Shipment());
        await db.SaveChangesAsync();

        (await db.Shipments.SingleAsync()).ShipmentNumber.Should().Be("SHP-2026-00001");
    }

    // ── TC-07.2 — tenant scoping, identity and audit ─────────────────────────

    [Fact]
    public void Every_new_entity_is_tenant_scoped_with_a_unique_uuid()
    {
        var model = Model();

        foreach (var clrType in NewEntities)
        {
            typeof(ITenantScopedEntity).IsAssignableFrom(clrType)
                .Should().BeTrue($"{clrType.Name} must implement ITenantScopedEntity");

            var entity = model.FindEntityType(clrType)!;

            entity.FindProperty(nameof(ITenantScopedEntity.OrganizationId))
                  .Should().NotBeNull($"{clrType.Name} must persist OrganizationId");

            entity.GetIndexes()
                  .Any(i => i.IsUnique && i.Properties.Count == 1 && i.Properties[0].Name == "UUID")
                  .Should().BeTrue($"{clrType.Name} must have a unique index on UUID");
        }
    }

    [Fact]
    public void Every_new_entity_records_who_created_it_and_when()
    {
        var model = Model();

        foreach (var clrType in NewEntities)
        {
            var entity = model.FindEntityType(clrType)!;

            entity.FindProperty("CreatedBy").Should().NotBeNull($"{clrType.Name} needs CreatedBy");
            entity.FindProperty("CreatedDate").Should().NotBeNull($"{clrType.Name} needs CreatedDate");
        }
    }

    [Fact]
    public void The_two_documents_carry_a_concurrency_token()
    {
        // Release and goods issue both have financial consequence; neither may be lost to a
        // last-write-wins race. Follows DocumentTimeline in the workflow engine.
        var model = Model();

        foreach (var clrType in new[] { typeof(DeliveryOrder), typeof(Consignment) })
            model.FindEntityType(clrType)!
                 .FindProperty("RowVersion")!.IsConcurrencyToken
                 .Should().BeTrue($"{clrType.Name} must use optimistic concurrency");
    }

    // ── TC-07.7 — the query filter reaches all of them ───────────────────────

    [Fact]
    public void Every_new_entity_has_a_tenant_query_filter()
    {
        var model = Model();

        foreach (var clrType in NewEntities)
            model.FindEntityType(clrType)!.GetQueryFilter()
                 .Should().NotBeNull($"{clrType.Name} must be filtered by organization");
    }

    [Fact]
    public async Task Another_organizations_delivery_is_invisible()
    {
        var orgA = Guid.NewGuid();
        var (dbA, _, dbName) = LogisticsTestDb.New(orgA);

        dbA.DeliveryOrders.Add(NewDelivery());
        await dbA.SaveChangesAsync();

        await using var dbB = LogisticsTestDb.OpenAs(dbName, Guid.NewGuid());
        (await dbB.DeliveryOrders.ToListAsync()).Should().BeEmpty();
    }

    // ── TC-07.3 — cascade shape ──────────────────────────────────────────────

    [Fact]
    public void Deleting_a_delivery_cascades_its_lines_and_packages_but_not_its_consignments()
    {
        var model = Model();

        Behavior<DeliveryOrderLine>("DeliveryOrderId").Should().Be(DeleteBehavior.Cascade);
        Behavior<ShipmentPackage>("DeliveryOrderId").Should().Be(DeleteBehavior.Cascade);
        Behavior<PackageContent>("ShipmentPackageId").Should().Be(DeleteBehavior.Cascade);
        Behavior<ConsignmentStop>("ConsignmentId").Should().Be(DeleteBehavior.Cascade);
        Behavior<ConsignmentDelivery>("ConsignmentId").Should().Be(DeleteBehavior.Cascade);

        // A consignment outlives any one delivery on it — the carrier has already been told
        // about the load.
        Behavior<ConsignmentDelivery>("DeliveryOrderId").Should().Be(DeleteBehavior.Restrict);

        // Two cascade paths would reach package_contents (delivery → lines → contents and
        // delivery → packages → contents); SQL Server refuses that. Restricting from the line is
        // also the right rule: a packed line should not be deletable without unpacking first.
        Behavior<PackageContent>("DeliveryOrderLineId").Should().Be(DeleteBehavior.Restrict);

        // Addresses are snapshots — deleting one would erase where the goods went.
        Behavior<DeliveryOrder>("ShipToAddressId").Should().Be(DeleteBehavior.Restrict);
        Behavior<Consignment>("ShipToAddressId").Should().Be(DeleteBehavior.Restrict);

        // Removing a pallet must not delete the cartons that were on it.
        Behavior<ShipmentPackage>("ParentPackageId").Should().Be(DeleteBehavior.Restrict);

        DeleteBehavior Behavior<TEntity>(string fk) =>
            model.FindEntityType(typeof(TEntity))!
                 .GetForeignKeys().Single(k => k.Properties.Any(p => p.Name == fk))
                 .DeleteBehavior;
    }

    [Fact]
    public async Task Removing_a_delivery_takes_its_lines_with_it()
    {
        var (db, _, _) = LogisticsTestDb.New();

        var delivery = NewDelivery();
        delivery.Lines.Add(NewLine(1));
        delivery.Lines.Add(NewLine(2));
        db.DeliveryOrders.Add(delivery);
        await db.SaveChangesAsync();

        (await db.DeliveryOrderLines.CountAsync()).Should().Be(2);

        db.DeliveryOrders.Remove(await db.DeliveryOrders.Include(d => d.Lines).SingleAsync());
        await db.SaveChangesAsync();

        (await db.DeliveryOrderLines.ToListAsync()).Should().BeEmpty();
    }

    // ── TC-07.4 — uniqueness declarations ────────────────────────────────────

    [Fact]
    public void The_pairings_that_must_be_unique_are_declared_unique()
    {
        var model = Model();

        HasUnique<ConsignmentDelivery>("ConsignmentId", "DeliveryOrderId")
            .Should().BeTrue("a delivery counted twice on one consignment double-counts the load");

        HasUnique<DeliveryOrder>("OrganizationId", "DeliveryNumber").Should().BeTrue();
        HasUnique<Consignment>("OrganizationId", "ConsignmentNumber").Should().BeTrue();
        HasUnique<DeliveryOrderLine>("DeliveryOrderId", "LineNo").Should().BeTrue();
        HasUnique<ConsignmentStop>("ConsignmentId", "Sequence").Should().BeTrue();

        // A handling-unit barcode is printed onto a physical carton.
        HasUnique<ShipmentPackage>("OrganizationId", "PackageBarcode").Should().BeTrue();

        // The guard against a retry booking a second real parcel.
        HasUnique<Consignment>("OrganizationId", "BookingIdempotencyKey").Should().BeTrue();

        bool HasUnique<TEntity>(params string[] properties) =>
            model.FindEntityType(typeof(TEntity))!.GetIndexes()
                 .Any(i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(properties));
    }

    // ── TC-07.5 — the many-to-many actually works both ways ──────────────────

    [Fact]
    public async Task One_consignment_can_carry_several_deliveries()
    {
        var (db, _, _) = LogisticsTestDb.New();

        var consignment = NewConsignment();
        db.Consignments.Add(consignment);

        for (var i = 1; i <= 3; i++)
        {
            var delivery = NewDelivery($"DLV-2026-0000{i}");
            db.DeliveryOrders.Add(delivery);
            db.ConsignmentDeliveries.Add(new ConsignmentDelivery
            {
                UUID = Guid.NewGuid(), Consignment = consignment,
                DeliveryOrder = delivery, Sequence = i, CreatedDate = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();

        var loaded = await db.Consignments
            .Include(c => c.Deliveries).ThenInclude(cd => cd.DeliveryOrder)
            .SingleAsync();

        loaded.Deliveries.Should().HaveCount(3);
        loaded.Deliveries.Select(d => d.DeliveryOrder.DeliveryNumber)
              .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task One_delivery_can_split_across_several_consignments()
    {
        // Stock shipping in waves — the case a one-to-many could never express.
        var (db, _, _) = LogisticsTestDb.New();

        var delivery = NewDelivery();
        db.DeliveryOrders.Add(delivery);

        foreach (var number in new[] { "SHP-2026-00001", "SHP-2026-00002" })
        {
            var consignment = NewConsignment(number);
            db.Consignments.Add(consignment);
            db.ConsignmentDeliveries.Add(new ConsignmentDelivery
            {
                UUID = Guid.NewGuid(), Consignment = consignment,
                DeliveryOrder = delivery, Sequence = 1, CreatedDate = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();

        var loaded = await db.DeliveryOrders
            .Include(d => d.Consignments).ThenInclude(cd => cd.Consignment)
            .SingleAsync();

        loaded.Consignments.Should().HaveCount(2);
    }

    // ── The posting rule is computed, never stored ───────────────────────────

    [Fact]
    public void Posts_goods_issue_is_not_a_column()
    {
        // Persisting it would let a bad import set it wrong, and the failure mode is a silently
        // double-deducted ledger. It has exactly one home: DeliverySourceTypeInfo.
        Model().FindEntityType(typeof(DeliveryOrder))!
               .FindProperty(nameof(DeliveryOrder.PostsGoodsIssue))
               .Should().BeNull();
    }

    [Theory]
    [InlineData("MIV", false)]
    [InlineData("SRO", false)]
    [InlineData("PO", false)]
    [InlineData("TRANSFER", true)]
    [InlineData("MANUAL", true)]
    [InlineData("SALE_ORDER", true)]
    public void A_delivery_derives_its_posting_rule_from_its_source(string sourceType, bool expected) =>
        new DeliveryOrder { SourceType = sourceType }.PostsGoodsIssue.Should().Be(expected);

    // ── A29-P6-01 — sale-order fulfilment columns ────────────────────────────

    [Theory]
    [InlineData(nameof(DeliveryOrder.SaleOrderUuid),        null)]
    [InlineData(nameof(DeliveryOrder.DeliveryMode),         15)]
    [InlineData(nameof(DeliveryOrder.PickupPersonName),     200)]
    [InlineData(nameof(DeliveryOrder.PickupPersonIdType),   20)]
    [InlineData(nameof(DeliveryOrder.PickupPersonIdNumber), 50)]
    [InlineData(nameof(DeliveryOrder.PickupAuthorization),  500)]
    [InlineData(nameof(DeliveryOrder.PickedUpAt),           null)]
    [InlineData(nameof(DeliveryOrder.PickedUpBy),           null)]
    public void The_sale_order_delivery_columns_are_nullable_so_no_other_delivery_needs_them(
        string column, int? maxLength)
    {
        var property = Model().FindEntityType(typeof(DeliveryOrder))!.FindProperty(column)!;

        property.IsNullable.Should().BeTrue($"{column} is empty on every non-sale-order delivery");
        property.GetMaxLength().Should().Be(maxLength);
    }

    [Fact]
    public void A_delivery_line_can_name_the_sale_order_line_it_fulfils_but_need_not()
    {
        Model().FindEntityType(typeof(DeliveryOrderLine))!
               .FindProperty(nameof(DeliveryOrderLine.SoLineUuid))!
               .IsNullable.Should().BeTrue();
    }

    [Fact]
    public void The_sale_order_references_are_bare_uuids_because_sale_orders_are_in_another_dbcontext()
    {
        // Demand owns SaleOrder; a real FK would need Logistics to reference its DbContext. The
        // existing SourceUuid / SourceLineUuid make the same trade.
        var model = Model();

        model.FindEntityType(typeof(DeliveryOrder))!.GetForeignKeys()
             .Should().NotContain(k => k.Properties.Any(p => p.Name == nameof(DeliveryOrder.SaleOrderUuid)));
        model.FindEntityType(typeof(DeliveryOrderLine))!.GetForeignKeys()
             .Should().NotContain(k => k.Properties.Any(p => p.Name == nameof(DeliveryOrderLine.SoLineUuid)));
    }

    [Fact]
    public void Deliveries_of_a_sale_order_are_indexed_by_order_and_status()
    {
        // A29 §17.2 — the lookup behind GET /api/sale-orders/{id}/deliveries.
        Model().FindEntityType(typeof(DeliveryOrder))!.GetIndexes()
               .Any(i => !i.IsUnique
                      && i.Properties.Select(p => p.Name).SequenceEqual(
                             new[] { nameof(DeliveryOrder.SaleOrderUuid), nameof(DeliveryOrder.Status) }))
               .Should().BeTrue();
    }

    [Fact]
    public async Task A_sale_order_delivery_round_trips_its_fulfilment_fields()
    {
        var (db, _, _) = LogisticsTestDb.New();
        var soUuid   = Guid.NewGuid();
        var soLine   = Guid.NewGuid();

        var delivery = NewDelivery();
        delivery.SourceType          = LogisticsCode.Of(DeliverySourceType.SaleOrder);
        delivery.SaleOrderUuid       = soUuid;
        delivery.DeliveryMode        = "SELF_PICKUP";
        delivery.PickupPersonName    = "Ahmed Raza";
        delivery.PickupPersonIdType  = "CNIC";
        delivery.PickupPersonIdNumber = "35202-1234567-1";
        delivery.PickupAuthorization = "Authorization letter AL-2026-114 signed by the customer's procurement lead";
        var line = NewLine(1);
        line.SoLineUuid = soLine;
        delivery.Lines.Add(line);
        db.DeliveryOrders.Add(delivery);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var loaded = await db.DeliveryOrders.Include(d => d.Lines).SingleAsync();
        loaded.SaleOrderUuid.Should().Be(soUuid);
        loaded.DeliveryMode.Should().Be("SELF_PICKUP");
        loaded.PickupPersonName.Should().Be("Ahmed Raza");
        loaded.PickupPersonIdType.Should().Be("CNIC");
        loaded.PickupPersonIdNumber.Should().Be("35202-1234567-1");
        loaded.PickupAuthorization.Should().StartWith("Authorization letter");
        loaded.Lines.Single().SoLineUuid.Should().Be(soLine);
        loaded.PostsGoodsIssue.Should().BeTrue();
    }

    [Fact]
    public async Task A_delivery_from_any_other_source_leaves_the_sale_order_fields_empty()
    {
        var (db, _, _) = LogisticsTestDb.New();
        var delivery = NewDelivery();
        delivery.Lines.Add(NewLine(1));
        db.DeliveryOrders.Add(delivery);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var loaded = await db.DeliveryOrders.Include(d => d.Lines).SingleAsync();
        loaded.SaleOrderUuid.Should().BeNull();
        loaded.DeliveryMode.Should().BeNull();
        loaded.PickupPersonName.Should().BeNull();
        loaded.PickupPersonIdType.Should().BeNull();
        loaded.PickupPersonIdNumber.Should().BeNull();
        loaded.PickupAuthorization.Should().BeNull();
        loaded.Lines.Single().SoLineUuid.Should().BeNull();
    }

    // ── Builders ─────────────────────────────────────────────────────────────

    private static DeliveryOrder NewDelivery(string number = "DLV-2026-00001") => new()
    {
        UUID           = Guid.NewGuid(),
        DeliveryNumber = number,
        Direction      = LogisticsCode.Of(DeliveryDirection.Outbound),
        SourceType     = LogisticsCode.Of(DeliverySourceType.Manual),
        CreatedBy      = 1,
        CreatedDate    = DateTime.UtcNow
    };

    private static DeliveryOrderLine NewLine(int lineNo) => new()
    {
        UUID            = Guid.NewGuid(),
        LineNo          = lineNo,
        ItemDescription = $"Item {lineNo}",
        UnitOfMeasure   = "PC",
        QtyOrdered      = 10m,
        CreatedBy       = 1,
        CreatedDate     = DateTime.UtcNow
    };

    private static Consignment NewConsignment(string number = "SHP-2026-00001") => new()
    {
        UUID              = Guid.NewGuid(),
        ConsignmentNumber = number,
        CreatedBy         = 1,
        CreatedDate       = DateTime.UtcNow
    };
}
