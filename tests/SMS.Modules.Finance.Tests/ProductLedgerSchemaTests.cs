using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P8-01 §11/§17.2 — the product ledger table. As with P7-01..03, applying the migration needs a
/// real SQL Server (done by hand against a scratch LocalDB, including the constraints and the index
/// plans); what is checkable from the model and the migration operations lives here, and the last test
/// fails the build if the entity is edited without a migration. Nothing writes to the ledger yet, so
/// nothing here tests posting or the weighted-average arithmetic.
/// </summary>
public class ProductLedgerSchemaTests
{
    private static FinanceDbContext InMemory(Guid? organizationId = null, string? dbName = null) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>()
                .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = organizationId ?? Guid.NewGuid() });

    private static FinanceDbContext SqlServerContext() =>
        new(new DbContextOptionsBuilder<FinanceDbContext>()
                .UseSqlServer("Server=none;Database=none;Trusted_Connection=True;").Options,
            new StaticTenantContext());

    private static IModel Model()
    {
        using var db = InMemory();
        return db.Model;
    }

    private static IReadOnlyList<Migration> Migrations()
    {
        using var db = SqlServerContext();
        var assembly = db.GetService<IMigrationsAssembly>();
        return assembly.Migrations
            .OrderBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => assembly.CreateMigration(m.Value, "Microsoft.EntityFrameworkCore.SqlServer"))
            .ToList();
    }

    private static Migration LedgerMigration() =>
        Migrations().Single(m => m.GetType().Name == "CreateProductLedger");

    private static bool Has(IEntityType e, bool unique, params string[] props) =>
        e.GetIndexes().Any(i => i.IsUnique == unique && i.Properties.Select(p => p.Name).SequenceEqual(props));

    private static ProductLedgerEntry Entry(
        Guid variant, Guid product, int seq, string type, string direction, decimal qty, decimal unitCost,
        decimal runningQty, decimal runningValue, Guid? partner = null, DateTime? date = null) => new()
    {
        UUID = Guid.NewGuid(), VariantUuid = variant, ProductUuid = product, SequenceNo = seq,
        EntryDate = date ?? new DateTime(2026, 9, 20).AddDays(seq), EntryType = type,
        ReferenceType = type == "SALE" ? "SalesInvoice" : "GRN", ReferenceId = Guid.NewGuid(),
        ReferenceNumber = type == "SALE" ? "SINV-20260921-0001" : "GRN-20260920-0001", PartnerId = partner,
        Quantity = qty, UnitCost = unitCost, TotalCost = decimal.Round(qty * unitCost, 2), Direction = direction,
        RunningQty = runningQty, RunningValue = runningValue,
        CreatedBy = 1, CreatedDate = DateTime.UtcNow
    };

    // ── The table ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_ledger_is_mapped_into_the_finance_schema_as_product_ledger()
    {
        var model  = Model();
        var entity = model.FindEntityType(typeof(ProductLedgerEntry))!;

        entity.GetTableName().Should().Be("product_ledger");
        (entity.GetSchema() ?? model.GetDefaultSchema()).Should().Be("finance");
    }

    [Fact]
    public void It_is_a_table_of_its_own_beside_the_master_product_ledger_not_a_change_to_it()
    {
        var model = Model();

        model.FindEntityType(typeof(MasterProductLedger))!.GetTableName().Should().Be("master_product_ledger");
        model.FindEntityType(typeof(ProductLedgerEntry))!.GetTableName().Should().NotBe("master_product_ledger");
    }

    [Fact]
    public void The_entity_is_tenant_scoped_with_a_unique_uuid_and_a_query_filter()
    {
        var entity = Model().FindEntityType(typeof(ProductLedgerEntry))!;

        typeof(ITenantScopedEntity).IsAssignableFrom(typeof(ProductLedgerEntry)).Should().BeTrue();
        entity.FindProperty(nameof(ITenantScopedEntity.OrganizationId)).Should().NotBeNull();
        entity.FindProperty(nameof(ITenantScopedEntity.OrganizationId))!.IsNullable.Should().BeFalse();
        entity.GetQueryFilter().Should().NotBeNull("one organization's stock costs must never be visible to another");
        Has(entity, true, "UUID").Should().BeTrue();
    }

    [Fact]
    public void The_two_section_17_2_lookups_are_indexed_and_the_sequence_is_unique_per_variant()
    {
        var entity = Model().FindEntityType(typeof(ProductLedgerEntry))!;

        // §17.2 — (org, variant, entry_date) and (org, product, entry_date).
        Has(entity, false, "OrganizationId", "VariantUuid", "EntryDate").Should().BeTrue();
        Has(entity, false, "OrganizationId", "ProductUuid", "EntryDate").Should().BeTrue();
        // The concurrency guard: the loser of a race for the next SequenceNo hits this and retries.
        Has(entity, true, "OrganizationId", "VariantUuid", "SequenceNo").Should().BeTrue();

        entity.GetIndexes().Should().HaveCount(4, "the unique uuid, the sequence guard and the two §17.2 lookups — nothing speculative");
    }

    [Fact]
    public void Every_index_leads_with_the_organization_except_the_uuid()
    {
        var entity = Model().FindEntityType(typeof(ProductLedgerEntry))!;

        entity.GetIndexes()
            .Where(i => !i.Properties.Select(p => p.Name).SequenceEqual(["UUID"]))
            .Should().OnlyContain(i => i.Properties[0].Name == "OrganizationId",
                "every query runs under the tenant filter, so an index not led by the organization is one the filter cannot use");
    }

    // ── The columns ───────────────────────────────────────────────────────────

    [Fact]
    public void Every_column_of_section_11_1_is_a_column_here()
    {
        var names = Model().FindEntityType(typeof(ProductLedgerEntry))!.GetProperties().Select(p => p.Name).ToList();

        // entry_id, org_id, variant_id, product_id, entry_date, entry_type, reference_type, reference_id,
        // reference_number, partner_id, quantity, unit_cost, total_cost, direction, running_qty,
        // running_value, narration, created_by/at — with the ids as UUIDs, and a row Uuid and sequence.
        names.Should().BeEquivalentTo(
        [
            "Id", "UUID", "OrganizationId", "VariantUuid", "ProductUuid", "SequenceNo", "EntryDate", "EntryType",
            "ReferenceType", "ReferenceId", "ReferenceNumber", "PartnerId", "Quantity", "UnitCost", "TotalCost",
            "Direction", "RunningQty", "RunningValue", "Narration", "CreatedBy", "CreatedDate"
        ]);
    }

    [Fact]
    public void Only_the_partner_and_the_narration_are_optional()
    {
        var entity = Model().FindEntityType(typeof(ProductLedgerEntry))!;

        entity.GetProperties().Where(p => p.IsNullable).Select(p => p.Name)
              .Should().BeEquivalentTo(["PartnerId", "Narration"], "an adjustment or a write-off has no partner");
    }

    [Fact]
    public void Column_shapes_are_the_ones_the_stock_side_already_uses()
    {
        // Quantities and unit costs at four places, as InventoryItems and the inventory ledger hold them;
        // money totals at two, as the master product ledger and the customer ledger do.
        using var db = SqlServerContext();
        var e = db.Model.FindEntityType(typeof(ProductLedgerEntry))!;
        var entity = e;
        e.FindProperty(nameof(ProductLedgerEntry.Quantity))!.GetColumnType().Should().Be("decimal(18,4)");
        e.FindProperty(nameof(ProductLedgerEntry.UnitCost))!.GetColumnType().Should().Be("decimal(18,4)");
        e.FindProperty(nameof(ProductLedgerEntry.RunningQty))!.GetColumnType().Should().Be("decimal(18,4)");
        e.FindProperty(nameof(ProductLedgerEntry.TotalCost))!.GetColumnType().Should().Be("decimal(18,2)");
        e.FindProperty(nameof(ProductLedgerEntry.RunningValue))!.GetColumnType().Should().Be("decimal(18,2)");

        var master = db.Model.FindEntityType(typeof(MasterProductLedger))!;
        e.FindProperty(nameof(ProductLedgerEntry.UnitCost))!.GetColumnType()
            .Should().Be(master.FindProperty(nameof(MasterProductLedger.UnitCost))!.GetColumnType());
        e.FindProperty(nameof(ProductLedgerEntry.TotalCost))!.GetColumnType()
            .Should().Be(master.FindProperty(nameof(MasterProductLedger.TotalValue))!.GetColumnType());

        entity.FindProperty(nameof(ProductLedgerEntry.EntryType))!.GetMaxLength().Should().Be(20);
        entity.FindProperty(nameof(ProductLedgerEntry.Direction))!.GetMaxLength().Should().Be(3);
        entity.FindProperty(nameof(ProductLedgerEntry.ReferenceType))!.GetMaxLength().Should().Be(30);
        entity.FindProperty(nameof(ProductLedgerEntry.ReferenceNumber))!.GetMaxLength().Should().Be(50);
        entity.FindProperty(nameof(ProductLedgerEntry.Narration))!.GetMaxLength().Should().Be(500);
        e.FindProperty(nameof(ProductLedgerEntry.VariantUuid))!.GetColumnType().Should().Be("uniqueidentifier");
    }

    [Fact]
    public void The_ledger_is_append_only_it_has_no_modified_or_deleted_columns()
    {
        var names = Model().FindEntityType(typeof(ProductLedgerEntry))!.GetProperties().Select(p => p.Name).ToList();

        names.Should().NotContain(["ModifiedBy", "ModifiedDate", "IsDelete", "IsActive"]);
        names.Should().Contain(["CreatedBy", "CreatedDate"]);
    }

    [Fact]
    public void The_variant_the_product_and_the_partner_are_bare_uuids_with_no_foreign_key()
    {
        var entity = Model().FindEntityType(typeof(ProductLedgerEntry))!;

        entity.GetForeignKeys().Should().BeEmpty(
            "variants, products and partners live in other modules' contexts, and a ledger entry must outlive a document being tidied away");
        entity.GetReferencingForeignKeys().Should().BeEmpty();

        entity.FindProperty(nameof(ProductLedgerEntry.VariantUuid))!.ClrType.Should().Be(typeof(Guid));
        entity.FindProperty(nameof(ProductLedgerEntry.ProductUuid))!.ClrType.Should().Be(typeof(Guid));
        entity.FindProperty(nameof(ProductLedgerEntry.PartnerId))!.ClrType.Should().Be(typeof(Guid?));
    }

    [Fact]
    public void The_entry_type_and_direction_vocabularies_are_exactly_those_of_section_11_1()
    {
        ProductLedgerEntryTypes.All.Should().BeEquivalentTo(
            ["PURCHASE", "SALE", "RETURN_IN", "RETURN_OUT", "ADJUSTMENT", "WRITE_OFF"]);
        ProductLedgerEntryTypes.All.Should().OnlyHaveUniqueItems();
        ProductLedgerEntryTypes.All.Should().OnlyContain(t => t.Length <= 20, "the column is nvarchar(20)");

        ProductLedgerDirections.All.Should().BeEquivalentTo(["IN", "OUT"]);
        ProductLedgerDirections.All.Should().OnlyContain(d => d.Length <= 3, "the column is nvarchar(3)");
    }

    // ── Round trips ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_variants_history_round_trips_the_way_a_purchase_and_a_sale_would_write_it()
    {
        var org = Guid.NewGuid();
        var variant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var supplier = Guid.NewGuid();
        var customer = Guid.NewGuid();
        await using var db = InMemory(org);

        // TC-10's shape: a GRN of 100 at 10, then 30 sold — running quantity 70, running value 700, WAC 10.
        db.ProductLedgerEntries.AddRange(
            Entry(variant, product, 1, "PURCHASE", "IN", 100m, 10m, 100m, 1000m, supplier),
            Entry(variant, product, 2, "SALE", "OUT", 30m, 10m, 70m, 700m, customer),
            Entry(variant, product, 3, "ADJUSTMENT", "OUT", 2.5m, 10m, 67.5m, 675m));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var entries = await db.ProductLedgerEntries.OrderBy(e => e.SequenceNo).ToListAsync();

        entries.Select(e => (e.EntryType, e.Direction, e.Quantity, e.TotalCost, e.RunningQty, e.RunningValue)).Should().Equal(
            ("PURCHASE",   "IN",  100m, 1000m, 100m,  1000m),
            ("SALE",       "OUT", 30m,  300m,  70m,   700m),
            ("ADJUSTMENT", "OUT", 2.5m, 25m,   67.5m, 675m));
        entries.Select(e => e.RunningValue / e.RunningQty).Should().OnlyContain(wac => wac == 10m);
        entries.Select(e => e.PartnerId).Should().Equal(supplier, customer, null);
        entries.Should().OnlyContain(e => e.OrganizationId == org, "stamped on write");
    }

    [Fact]
    public async Task The_four_place_quantities_and_unit_costs_survive_a_round_trip()
    {
        var org = Guid.NewGuid();
        await using var db = InMemory(org);

        db.ProductLedgerEntries.Add(Entry(Guid.NewGuid(), Guid.NewGuid(), 1, "PURCHASE", "IN", 3.3333m, 10.0025m, 3.3333m, 33.34m));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var entry = await db.ProductLedgerEntries.SingleAsync();
        entry.Quantity.Should().Be(3.3333m);
        entry.UnitCost.Should().Be(10.0025m);
        entry.TotalCost.Should().Be(33.34m);
    }

    [Fact]
    public async Task A_products_variants_share_a_product_history_but_keep_their_own_sequences()
    {
        var org = Guid.NewGuid();
        var product = Guid.NewGuid();
        var (small, large) = (Guid.NewGuid(), Guid.NewGuid());
        await using var db = InMemory(org);

        db.ProductLedgerEntries.AddRange(
            Entry(small, product, 1, "PURCHASE", "IN", 10m, 4m, 10m, 40m),
            Entry(large, product, 1, "PURCHASE", "IN", 5m, 8m, 5m, 40m),
            Entry(small, product, 2, "SALE", "OUT", 3m, 4m, 7m, 28m),
            Entry(Guid.NewGuid(), Guid.NewGuid(), 1, "PURCHASE", "IN", 1m, 1m, 1m, 1m));
        await db.SaveChangesAsync();

        var ofProduct = await db.ProductLedgerEntries.Where(e => e.ProductUuid == product).ToListAsync();
        ofProduct.Should().HaveCount(3, "the other product's entry is not in it");
        ofProduct.GroupBy(e => e.VariantUuid).Should().HaveCount(2);
        ofProduct.Where(e => e.VariantUuid == small).Select(e => e.SequenceNo).Should().Equal(1, 2);
        ofProduct.Where(e => e.VariantUuid == large).Select(e => e.SequenceNo).Should().Equal(1);
    }

    [Fact]
    public async Task Another_organizations_ledger_is_invisible_even_for_the_same_variant()
    {
        var dbName = Guid.NewGuid().ToString();
        var variant = Guid.NewGuid();
        var product = Guid.NewGuid();

        await using var a = InMemory(Guid.NewGuid(), dbName);
        a.ProductLedgerEntries.Add(Entry(variant, product, 1, "PURCHASE", "IN", 10m, 5m, 10m, 50m));
        await a.SaveChangesAsync();

        await using var b = InMemory(Guid.NewGuid(), dbName);
        (await b.ProductLedgerEntries.ToListAsync()).Should().BeEmpty();
        (await b.ProductLedgerEntries.Where(e => e.VariantUuid == variant).ToListAsync()).Should().BeEmpty();
        (await b.ProductLedgerEntries.Where(e => e.ProductUuid == product).ToListAsync()).Should().BeEmpty();
    }

    // ── The migration ─────────────────────────────────────────────────────────

    [Fact]
    public void The_migration_creates_exactly_the_one_table_and_touches_nothing_else()
    {
        var up = LedgerMigration().UpOperations;

        var table = up.OfType<CreateTableOperation>().Should().ContainSingle().Subject;
        table.Name.Should().Be("product_ledger");
        table.Schema.Should().Be("finance");
        table.ForeignKeys.Should().BeEmpty();
        up.Should().OnlyContain(op => op is CreateTableOperation || op is CreateIndexOperation,
            "nothing on the master product ledger or the receivables tables may ride along");

        var indexes = up.OfType<CreateIndexOperation>().ToList();
        indexes.Should().HaveCount(4);
        indexes.Should().Contain(i => i.Columns.SequenceEqual(new[] { "OrganizationId", "VariantUuid", "SequenceNo" }) && i.IsUnique);
        indexes.Should().Contain(i => i.Columns.SequenceEqual(new[] { "OrganizationId", "VariantUuid", "EntryDate" }) && !i.IsUnique);
        indexes.Should().Contain(i => i.Columns.SequenceEqual(new[] { "OrganizationId", "ProductUuid", "EntryDate" }) && !i.IsUnique);
        indexes.Should().Contain(i => i.Columns.SequenceEqual(new[] { "UUID" }) && i.IsUnique);
    }

    [Fact]
    public void Rolling_the_migration_back_drops_exactly_that_table()
    {
        var migration = LedgerMigration();

        migration.DownOperations.Should().ContainSingle().Which.Should().BeOfType<DropTableOperation>()
                 .Which.Name.Should().Be("product_ledger");
    }

    [Fact]
    public void The_migration_comes_after_the_receivable_migrations_before_it_and_is_the_last_one()
    {
        var names = Migrations().Select(m => m.GetType().Name).ToList();

        names.IndexOf("CreateProductLedger").Should().BeGreaterThan(names.IndexOf("CreateCustomerLedger"));
        names.IndexOf("CreateProductLedger").Should().BeGreaterThan(names.IndexOf("CreateCustomerPaymentTables"));
        names.IndexOf("CreateProductLedger").Should().BeGreaterThan(names.IndexOf("CreateSalesInvoiceTables"));
        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_model_builds_under_sql_server_and_matches_its_snapshot()
    {
        using var db = SqlServerContext();

        var act = () => db.Model.GetEntityTypes().ToList();
        act.Should().NotThrow("the model must be buildable by the SQL Server provider");

        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;
        snapshot.Should().NotBeNull();

        var snapshotModel = snapshot!.Model;
        if (snapshotModel is IMutableModel mutable) snapshotModel = mutable.FinalizeModel();
        snapshotModel = db.GetService<IModelRuntimeInitializer>().Initialize(snapshotModel, designTime: true, validationLogger: null);

        var differences = db.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshotModel.GetRelationalModel(),
            db.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        differences.Should().BeEmpty("the entity and the migration snapshot have drifted — run 'dotnet ef migrations add'");
    }
}
