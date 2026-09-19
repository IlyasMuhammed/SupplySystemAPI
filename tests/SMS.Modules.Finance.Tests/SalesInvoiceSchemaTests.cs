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
/// A29-P7-01 §9.1/§9.2/§17.2 — the receivable tables and their migration. Applying it needs a
/// real SQL Server (done by hand against a scratch LocalDB — see the task notes, as the Finance
/// chain cannot be built from an empty database); everything checkable from the model and the
/// migration operations lives here, and the last test fails the build if an entity is edited
/// without a migration.
/// </summary>
public class SalesInvoiceSchemaTests
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

    private static Migration SalesInvoiceMigration() =>
        Migrations().Single(m => m.GetType().Name == "CreateSalesInvoiceTables");

    private static SalesInvoice NewInvoice(string number = "SINV-20260920-0001", params (string Desc, decimal Qty, decimal Price)[] lines)
    {
        var invoice = new SalesInvoice
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), InvoiceNumber = number,
            SaleOrderUuid = Guid.NewGuid(), SaleOrderNumber = "SO-2026-00042",
            PartnerId = Guid.NewGuid(), PartnerName = "Acme Ltd",
            InvoiceDate = new DateTime(2026, 9, 20), DueDate = new DateTime(2026, 10, 20),
            CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        var lineNo = 1;
        foreach (var (desc, qty, price) in lines.Length > 0 ? lines : [("4mm cable", 100m, 40m)])
            invoice.Lines.Add(new SalesInvoiceLine
            {
                UUID = Guid.NewGuid(), LineNo = lineNo++, SoLineUuid = Guid.NewGuid(), VariantUuid = Guid.NewGuid(),
                Description = desc, Quantity = qty, UnitPrice = price, LineTotal = qty * price
            });
        invoice.Subtotal = invoice.Lines.Sum(l => l.LineTotal);
        invoice.GrandTotal = invoice.Subtotal;
        invoice.BalanceDue = invoice.GrandTotal;
        return invoice;
    }

    // ── Tables ────────────────────────────────────────────────────────────────

    [Fact]
    public void Both_tables_are_mapped_into_the_finance_schema()
    {
        var model = Model();

        foreach (var (clrType, table) in new[] { (typeof(SalesInvoice), "sales_invoices"), (typeof(SalesInvoiceLine), "sales_invoice_lines") })
        {
            var entity = model.FindEntityType(clrType)!;
            entity.GetTableName().Should().Be(table);
            (entity.GetSchema() ?? model.GetDefaultSchema()).Should().Be("finance");
        }
    }

    [Fact]
    public void Both_entities_are_tenant_scoped_with_a_unique_uuid_and_a_query_filter()
    {
        var model = Model();

        foreach (var clrType in new[] { typeof(SalesInvoice), typeof(SalesInvoiceLine) })
        {
            typeof(ITenantScopedEntity).IsAssignableFrom(clrType).Should().BeTrue(clrType.Name);
            var entity = model.FindEntityType(clrType)!;
            entity.FindProperty(nameof(ITenantScopedEntity.OrganizationId)).Should().NotBeNull();
            entity.GetQueryFilter().Should().NotBeNull($"{clrType.Name} must be filtered by organization");
            entity.GetIndexes().Any(i => i.IsUnique && i.Properties.Count == 1 && i.Properties[0].Name == "UUID")
                  .Should().BeTrue($"{clrType.Name} needs a unique UUID index");
        }
    }

    [Fact]
    public void The_indexes_of_17_2_are_declared_and_the_number_is_unique_per_organization()
    {
        var model   = Model();
        var invoice = model.FindEntityType(typeof(SalesInvoice))!;
        var line    = model.FindEntityType(typeof(SalesInvoiceLine))!;

        static bool Has(IEntityType e, bool unique, params string[] props) =>
            e.GetIndexes().Any(i => i.IsUnique == unique && i.Properties.Select(p => p.Name).SequenceEqual(props));

        // §17.2 — the customer statement and the aging report.
        Has(invoice, false, "OrganizationId", "PartnerId", "Status").Should().BeTrue();
        Has(invoice, false, "OrganizationId", "DueDate", "BalanceDue").Should().BeTrue();
        // §9.5 — several invoices per order.
        Has(invoice, false, "OrganizationId", "SaleOrderUuid").Should().BeTrue();
        // Each org numbers its own invoices.
        Has(invoice, true, "OrganizationId", "InvoiceNumber").Should().BeTrue();
        invoice.GetIndexes().Any(i => i.Properties.Select(p => p.Name).SequenceEqual(["TraceId"])).Should().BeTrue();

        Has(line, true, "SalesInvoiceId", "LineNo").Should().BeTrue();
        line.GetIndexes().Any(i => i.Properties.Select(p => p.Name).SequenceEqual(["SoLineUuid"])).Should().BeTrue(
            "invoiced-so-far per order line is the §9.5 ceiling check");
    }

    [Theory]
    [InlineData(typeof(SalesInvoice), nameof(SalesInvoice.Subtotal),       "decimal(18,2)")]
    [InlineData(typeof(SalesInvoice), nameof(SalesInvoice.TaxAmount),      "decimal(18,2)")]
    [InlineData(typeof(SalesInvoice), nameof(SalesInvoice.DiscountAmount), "decimal(18,2)")]
    [InlineData(typeof(SalesInvoice), nameof(SalesInvoice.GrandTotal),     "decimal(18,2)")]
    [InlineData(typeof(SalesInvoice), nameof(SalesInvoice.AmountPaid),     "decimal(18,2)")]
    [InlineData(typeof(SalesInvoice), nameof(SalesInvoice.BalanceDue),     "decimal(18,2)")]
    [InlineData(typeof(SalesInvoiceLine), nameof(SalesInvoiceLine.Quantity),        "decimal(18,4)")]
    [InlineData(typeof(SalesInvoiceLine), nameof(SalesInvoiceLine.UnitPrice),       "decimal(18,2)")]
    [InlineData(typeof(SalesInvoiceLine), nameof(SalesInvoiceLine.DiscountPercent), "decimal(5,2)")]
    [InlineData(typeof(SalesInvoiceLine), nameof(SalesInvoiceLine.TaxPercent),      "decimal(5,2)")]
    [InlineData(typeof(SalesInvoiceLine), nameof(SalesInvoiceLine.LineTotal),       "decimal(18,2)")]
    public void Money_and_quantity_columns_carry_the_modules_precision(Type entity, string property, string columnType)
    {
        // Same precision as the payable side, so a receivable and a payable for the same goods
        // never differ by a rounding. Read under the SQL Server provider: the in-memory one
        // ignores column types altogether.
        using var db = SqlServerContext();
        db.Model.FindEntityType(entity)!.FindProperty(property)!.GetColumnType().Should().Be(columnType);
    }

    [Fact]
    public void Status_number_and_currency_have_the_expected_shape_and_defaults()
    {
        var invoice = Model().FindEntityType(typeof(SalesInvoice))!;

        var status = invoice.FindProperty(nameof(SalesInvoice.Status))!;
        status.GetMaxLength().Should().Be(20);
        status.GetDefaultValue().Should().Be("DRAFT");
        status.IsNullable.Should().BeFalse();

        // SINV-YYYYMMDD-NNNN is 18 characters; 25 leaves room without inviting free text.
        invoice.FindProperty(nameof(SalesInvoice.InvoiceNumber))!.GetMaxLength().Should().Be(25);
        invoice.FindProperty(nameof(SalesInvoice.CurrencyCode))!.GetDefaultValue().Should().Be("PKR");
        invoice.FindProperty(nameof(SalesInvoice.AmountPaid))!.GetDefaultValue().Should().Be(0m);
        invoice.FindProperty(nameof(SalesInvoice.Notes))!.IsNullable.Should().BeTrue();
        invoice.FindProperty(nameof(SalesInvoice.SaleOrderUuid))!.IsNullable.Should().BeFalse("an invoice always bills an order");
    }

    [Fact]
    public void The_status_vocabulary_is_exactly_the_seven_of_9_1()
    {
        SalesInvoiceStatuses.All.Should().BeEquivalentTo(
            ["DRAFT", "ISSUED", "PARTIALLY_PAID", "PAID", "OVERDUE", "CANCELLED", "CREDIT_NOTE"]);
        SalesInvoiceStatuses.All.Should().OnlyHaveUniqueItems();
        SalesInvoiceStatuses.All.Should().OnlyContain(s => s.Length <= 20, "the column is nvarchar(20)");
    }

    [Fact]
    public void Cross_module_references_are_bare_uuids_with_no_foreign_key()
    {
        var model = Model();

        model.FindEntityType(typeof(SalesInvoice))!.GetForeignKeys().Should().BeEmpty(
            "sale orders and partners live in other modules' contexts");
        model.FindEntityType(typeof(SalesInvoiceLine))!.GetForeignKeys()
             .Should().ContainSingle().Which.Properties.Single().Name.Should().Be(nameof(SalesInvoiceLine.SalesInvoiceId));
    }

    [Fact]
    public async Task Deleting_an_invoice_takes_its_lines_with_it()
    {
        var model = Model();
        model.FindEntityType(typeof(SalesInvoiceLine))!.GetForeignKeys().Single().DeleteBehavior
             .Should().Be(DeleteBehavior.Cascade);

        await using var db = InMemory();
        db.SalesInvoices.Add(NewInvoice(lines: [("4mm cable", 100m, 40m), ("Junction box", 5m, 10m)]));
        await db.SaveChangesAsync();
        (await db.SalesInvoiceLines.CountAsync()).Should().Be(2);

        db.SalesInvoices.Remove(await db.SalesInvoices.Include(i => i.Lines).SingleAsync());
        await db.SaveChangesAsync();

        (await db.SalesInvoiceLines.ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_invoice_round_trips_with_its_lines_and_is_stamped_with_the_organization()
    {
        var org = Guid.NewGuid();
        await using var db = InMemory(org);
        db.SalesInvoices.Add(NewInvoice(lines: [("4mm cable", 100m, 40m)]));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var loaded = await db.SalesInvoices.Include(i => i.Lines).SingleAsync();
        loaded.OrganizationId.Should().Be(org);
        loaded.Lines.Single().OrganizationId.Should().Be(org);
        loaded.Status.Should().Be("DRAFT");
        loaded.CurrencyCode.Should().Be("PKR");
        loaded.GrandTotal.Should().Be(4000m);
        loaded.BalanceDue.Should().Be(4000m);
        loaded.Lines.Single().Description.Should().Be("4mm cable");
    }

    [Fact]
    public async Task Another_organizations_invoices_are_invisible()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var a = InMemory(Guid.NewGuid(), dbName);
        a.SalesInvoices.Add(NewInvoice());
        await a.SaveChangesAsync();

        await using var b = InMemory(Guid.NewGuid(), dbName);
        (await b.SalesInvoices.ToListAsync()).Should().BeEmpty();
        (await b.SalesInvoiceLines.ToListAsync()).Should().BeEmpty();
    }

    // ── The migration ─────────────────────────────────────────────────────────

    [Fact]
    public void The_migration_creates_exactly_the_two_tables_and_touches_nothing_else()
    {
        var up = SalesInvoiceMigration().UpOperations;

        up.OfType<CreateTableOperation>().Select(t => t.Name).Should().BeEquivalentTo(["sales_invoices", "sales_invoice_lines"]);
        up.OfType<CreateTableOperation>().Should().OnlyContain(t => t.Schema == "finance");
        up.Should().OnlyContain(op => op is CreateTableOperation || op is CreateIndexOperation,
            "a table-creating migration must not ride changes to existing tables");

        // §17.2 — both pairs are on the real table, not just in the model.
        var indexes = up.OfType<CreateIndexOperation>().Where(i => i.Table == "sales_invoices").ToList();
        indexes.Should().Contain(i => i.Columns.SequenceEqual(new[] { "OrganizationId", "PartnerId", "Status" }) && !i.IsUnique);
        indexes.Should().Contain(i => i.Columns.SequenceEqual(new[] { "OrganizationId", "DueDate", "BalanceDue" }) && !i.IsUnique);
        indexes.Should().Contain(i => i.Columns.SequenceEqual(new[] { "OrganizationId", "InvoiceNumber" }) && i.IsUnique);

        var lines = up.OfType<CreateTableOperation>().Single(t => t.Name == "sales_invoice_lines");
        lines.ForeignKeys.Should().ContainSingle().Which.OnDelete.Should().Be(ReferentialAction.Cascade);
    }

    [Fact]
    public void Rolling_the_migration_back_drops_exactly_what_it_created()
    {
        var migration = SalesInvoiceMigration();

        migration.DownOperations.Should().OnlyContain(op => op is DropTableOperation);
        migration.DownOperations.OfType<DropTableOperation>().Select(o => o.Name)
                 .Should().BeEquivalentTo(migration.UpOperations.OfType<CreateTableOperation>().Select(o => o.Name));
        // Lines first: the foreign key would refuse the other order.
        migration.DownOperations.OfType<DropTableOperation>().First().Name.Should().Be("sales_invoice_lines");
    }

    [Fact]
    public void The_migration_comes_after_everything_that_was_already_deployed()
    {
        // Asserts an ordering invariant, not "is the latest" — that would have to be edited by every
        // later migration, which makes it a chore rather than a guard.
        var names = Migrations().Select(m => m.GetType().Name).ToList();

        names.IndexOf("CreateSalesInvoiceTables").Should().BeGreaterThan(names.IndexOf("RepairTenantOrganizationIndexes"));
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

        differences.Should().BeEmpty("the entities and the migration snapshot have drifted — run 'dotnet ef migrations add'");
    }
}
