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
/// A29-P7-02 §9.3/§9.4 — customer payments and how they are allocated to invoices. As with P7-01,
/// applying the migration needs a real SQL Server (done by hand against a scratch LocalDB); what is
/// checkable from the model and the migration operations lives here, and the last test fails the
/// build if an entity is edited without a migration.
/// </summary>
public class CustomerPaymentSchemaTests
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

    private static Migration PaymentMigration() =>
        Migrations().Single(m => m.GetType().Name == "CreateCustomerPaymentTables");

    private static SalesInvoice NewInvoice(string number = "SINV-20260920-0001", decimal total = 4000m) => new()
    {
        UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), InvoiceNumber = number,
        SaleOrderUuid = Guid.NewGuid(), SaleOrderNumber = "SO-2026-00042",
        PartnerId = Guid.NewGuid(), PartnerName = "Acme Ltd",
        InvoiceDate = new DateTime(2026, 9, 20), DueDate = new DateTime(2026, 10, 20),
        Subtotal = total, GrandTotal = total, BalanceDue = total, CreatedBy = 1, CreatedDate = DateTime.UtcNow
    };

    private static CustomerPayment NewPayment(string number = "CPAY-20260921-0001", decimal amount = 4000m) => new()
    {
        UUID = Guid.NewGuid(), PartnerId = Guid.NewGuid(), PartnerName = "Acme Ltd",
        PaymentNumber = number, PaymentDate = new DateTime(2026, 9, 21), Amount = amount,
        PaymentMethod = "BANK_TRANSFER", BankReference = "TT-88123", CreatedBy = 1, CreatedDate = DateTime.UtcNow
    };

    private static PaymentAllocation Allocate(CustomerPayment payment, SalesInvoice invoice, decimal amount) => new()
    {
        UUID = Guid.NewGuid(), CustomerPayment = payment, SalesInvoice = invoice,
        AllocatedAmount = amount, AllocatedAt = DateTime.UtcNow, AllocatedBy = 1
    };

    // ── Tables ────────────────────────────────────────────────────────────────

    [Fact]
    public void Both_tables_are_mapped_into_the_finance_schema()
    {
        var model = Model();

        foreach (var (clrType, table) in new[] { (typeof(CustomerPayment), "customer_payments"), (typeof(PaymentAllocation), "payment_allocations") })
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

        foreach (var clrType in new[] { typeof(CustomerPayment), typeof(PaymentAllocation) })
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
    public void The_payment_number_is_unique_per_organization_and_the_statement_lookup_is_indexed()
    {
        var payment    = Model().FindEntityType(typeof(CustomerPayment))!;
        var allocation = Model().FindEntityType(typeof(PaymentAllocation))!;

        static bool Has(IEntityType e, bool unique, params string[] props) =>
            e.GetIndexes().Any(i => i.IsUnique == unique && i.Properties.Select(p => p.Name).SequenceEqual(props));

        Has(payment, true,  "OrganizationId", "PaymentNumber").Should().BeTrue();
        Has(payment, false, "OrganizationId", "PartnerId", "PaymentDate").Should().BeTrue();
        Has(allocation, false, "SalesInvoiceId").Should().BeTrue("what has been paid against an invoice is how its status is derived");
    }

    [Theory]
    [InlineData(typeof(CustomerPayment),   nameof(CustomerPayment.Amount),            "decimal(18,2)")]
    [InlineData(typeof(PaymentAllocation), nameof(PaymentAllocation.AllocatedAmount), "decimal(18,2)")]
    public void Money_columns_carry_the_modules_precision(Type entity, string property, string columnType)
    {
        // Same as the invoice side, so an allocation and the balance it reduces never differ by a
        // rounding. Read under the SQL Server provider: the in-memory one ignores column types.
        using var db = SqlServerContext();
        db.Model.FindEntityType(entity)!.FindProperty(property)!.GetColumnType().Should().Be(columnType);
    }

    [Fact]
    public void Status_method_and_currency_have_the_expected_shape_and_defaults()
    {
        var payment = Model().FindEntityType(typeof(CustomerPayment))!;

        var status = payment.FindProperty(nameof(CustomerPayment.Status))!;
        status.GetMaxLength().Should().Be(20);
        status.GetDefaultValue().Should().Be("RECEIVED");
        status.IsNullable.Should().BeFalse();

        payment.FindProperty(nameof(CustomerPayment.PaymentMethod))!.GetMaxLength().Should().Be(20);
        payment.FindProperty(nameof(CustomerPayment.PaymentMethod))!.IsNullable.Should().BeFalse();
        payment.FindProperty(nameof(CustomerPayment.CurrencyCode))!.GetDefaultValue().Should().Be("PKR");
        // CPAY-YYYYMMDD-NNNN is 18 characters.
        payment.FindProperty(nameof(CustomerPayment.PaymentNumber))!.GetMaxLength().Should().Be(25);

        // Only some methods have these, so they cannot be required.
        payment.FindProperty(nameof(CustomerPayment.ChequeNumber))!.IsNullable.Should().BeTrue();
        payment.FindProperty(nameof(CustomerPayment.BankReference))!.IsNullable.Should().BeTrue();
        payment.FindProperty(nameof(CustomerPayment.Notes))!.IsNullable.Should().BeTrue();
    }

    [Fact]
    public void The_method_and_status_vocabularies_are_exactly_those_of_9_3()
    {
        CustomerPaymentMethods.All.Should().BeEquivalentTo(["CASH", "CHEQUE", "BANK_TRANSFER", "CARD", "ONLINE"]);
        CustomerPaymentStatuses.All.Should().BeEquivalentTo(["RECEIVED", "BOUNCED", "REVERSED"]);
        CustomerPaymentMethods.All.Concat(CustomerPaymentStatuses.All).Should().OnlyContain(s => s.Length <= 20, "the columns are nvarchar(20)");
    }

    // ── Relationships ─────────────────────────────────────────────────────────

    [Fact]
    public void An_allocation_belongs_to_one_payment_and_one_invoice_by_real_foreign_keys()
    {
        var allocation = Model().FindEntityType(typeof(PaymentAllocation))!;

        var fks = allocation.GetForeignKeys().ToList();
        fks.Should().HaveCount(2);

        var toPayment = fks.Single(k => k.PrincipalEntityType.ClrType == typeof(CustomerPayment));
        toPayment.Properties.Single().Name.Should().Be(nameof(PaymentAllocation.CustomerPaymentId));
        toPayment.DeleteBehavior.Should().Be(DeleteBehavior.Cascade, "deleting a payment takes its allocations with it");

        var toInvoice = fks.Single(k => k.PrincipalEntityType.ClrType == typeof(SalesInvoice));
        toInvoice.Properties.Single().Name.Should().Be(nameof(PaymentAllocation.SalesInvoiceId));
        toInvoice.DeleteBehavior.Should().Be(DeleteBehavior.Restrict, "money applied to an invoice pins the invoice");
    }

    [Fact]
    public void No_table_is_reachable_from_another_by_two_cascade_paths()
    {
        // SQL Server refuses that at CREATE TABLE time rather than at model-build time. The only
        // cascade into an allocation is from its payment; the invoice side must not add a second.
        var model = Model();

        model.FindEntityType(typeof(PaymentAllocation))!.GetForeignKeys()
             .Count(k => k.DeleteBehavior == DeleteBehavior.Cascade).Should().Be(1);
        model.FindEntityType(typeof(SalesInvoice))!.GetReferencingForeignKeys()
             .Where(k => k.DeleteBehavior == DeleteBehavior.Cascade)
             .Select(k => k.DeclaringEntityType.ClrType).Should().BeEquivalentTo([typeof(SalesInvoiceLine)]);
    }

    [Fact]
    public async Task One_payment_can_be_split_across_several_invoices_and_one_invoice_paid_by_several_payments()
    {
        await using var db = InMemory();
        var first  = NewInvoice("SINV-20260920-0001", 4000m);
        var second = NewInvoice("SINV-20260920-0002", 1000m);
        var big    = NewPayment("CPAY-20260921-0001", 3000m);
        var small  = NewPayment("CPAY-20260921-0002", 2000m);

        db.PaymentAllocations.AddRange(
            Allocate(big, first, 2500m), Allocate(big, second, 500m),   // one payment, two invoices
            Allocate(small, first, 1500m));                              // one invoice, two payments
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var loadedBig = await db.CustomerPayments.Include(p => p.Allocations).SingleAsync(p => p.PaymentNumber == "CPAY-20260921-0001");
        loadedBig.Allocations.Should().HaveCount(2);
        loadedBig.Allocations.Sum(a => a.AllocatedAmount).Should().Be(3000m);

        var loadedFirst = await db.SalesInvoices.Include(i => i.Allocations).SingleAsync(i => i.InvoiceNumber == "SINV-20260920-0001");
        loadedFirst.Allocations.Should().HaveCount(2);
        loadedFirst.Allocations.Sum(a => a.AllocatedAmount).Should().Be(4000m);
    }

    [Fact]
    public async Task A_payment_can_exist_with_no_allocations_yet_an_advance()
    {
        await using var db = InMemory();
        db.CustomerPayments.Add(NewPayment());
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var loaded = await db.CustomerPayments.Include(p => p.Allocations).SingleAsync();
        loaded.Allocations.Should().BeEmpty();
        loaded.Status.Should().Be("RECEIVED");
        loaded.CurrencyCode.Should().Be("PKR");
        loaded.Amount.Should().Be(4000m);
    }

    [Fact]
    public async Task Deleting_a_payment_takes_its_allocations_with_it_and_leaves_the_invoice()
    {
        await using var db = InMemory();
        var invoice = NewInvoice();
        var payment = NewPayment();
        db.PaymentAllocations.Add(Allocate(payment, invoice, 4000m));
        await db.SaveChangesAsync();

        db.CustomerPayments.Remove(await db.CustomerPayments.Include(p => p.Allocations).SingleAsync());
        await db.SaveChangesAsync();

        (await db.PaymentAllocations.CountAsync()).Should().Be(0);
        (await db.SalesInvoices.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Another_organizations_payments_and_allocations_are_invisible_and_stamped_on_write()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA   = Guid.NewGuid();
        await using var a = InMemory(orgA, dbName);
        a.PaymentAllocations.Add(Allocate(NewPayment(), NewInvoice(), 100m));
        await a.SaveChangesAsync();

        (await a.CustomerPayments.SingleAsync()).OrganizationId.Should().Be(orgA);
        (await a.PaymentAllocations.SingleAsync()).OrganizationId.Should().Be(orgA);

        await using var b = InMemory(Guid.NewGuid(), dbName);
        (await b.CustomerPayments.ToListAsync()).Should().BeEmpty();
        (await b.PaymentAllocations.ToListAsync()).Should().BeEmpty();
    }

    // ── The migration ─────────────────────────────────────────────────────────

    [Fact]
    public void The_migration_creates_exactly_the_two_tables_and_touches_nothing_else()
    {
        var up = PaymentMigration().UpOperations;

        up.OfType<CreateTableOperation>().Select(t => t.Name).Should().BeEquivalentTo(["customer_payments", "payment_allocations"]);
        up.OfType<CreateTableOperation>().Should().OnlyContain(t => t.Schema == "finance");
        up.Should().OnlyContain(op => op is CreateTableOperation || op is CreateIndexOperation,
            "a table-creating migration must not ride changes to existing tables — sales_invoices is untouched");

        var indexes = up.OfType<CreateIndexOperation>().Where(i => i.Table == "customer_payments").ToList();
        indexes.Should().Contain(i => i.Columns.SequenceEqual(new[] { "OrganizationId", "PaymentNumber" }) && i.IsUnique);
        indexes.Should().Contain(i => i.Columns.SequenceEqual(new[] { "OrganizationId", "PartnerId", "PaymentDate" }) && !i.IsUnique);

        var allocations = up.OfType<CreateTableOperation>().Single(t => t.Name == "payment_allocations");
        allocations.ForeignKeys.Should().HaveCount(2);
        allocations.ForeignKeys.Single(f => f.PrincipalTable == "customer_payments").OnDelete.Should().Be(ReferentialAction.Cascade);
        allocations.ForeignKeys.Single(f => f.PrincipalTable == "sales_invoices").OnDelete.Should().Be(ReferentialAction.Restrict);
    }

    [Fact]
    public void Rolling_the_migration_back_drops_exactly_what_it_created_allocations_first()
    {
        var migration = PaymentMigration();

        migration.DownOperations.Should().OnlyContain(op => op is DropTableOperation);
        var dropped = migration.DownOperations.OfType<DropTableOperation>().Select(o => o.Name).ToList();
        dropped.Should().BeEquivalentTo(migration.UpOperations.OfType<CreateTableOperation>().Select(o => o.Name));
        // The foreign key would refuse the other order.
        dropped.First().Should().Be("payment_allocations");
    }

    [Fact]
    public void The_migration_follows_the_sales_invoice_one_it_depends_on()
    {
        var names = Migrations().Select(m => m.GetType().Name).ToList();

        names.IndexOf("CreateCustomerPaymentTables").Should().BeGreaterThan(names.IndexOf("CreateSalesInvoiceTables"),
            "payment_allocations has a foreign key to sales_invoices");
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
