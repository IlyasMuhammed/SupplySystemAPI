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
/// A29-P7-03 §10/§17.2 — the customer ledger table. As with P7-01/02, applying the migration needs a
/// real SQL Server (done by hand against a scratch LocalDB); what is checkable from the model and the
/// migration operations lives here, and the last test fails the build if the entity is edited
/// without a migration. Nothing writes to the ledger yet, so nothing here tests posting.
/// </summary>
public class CustomerLedgerSchemaTests
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
        Migrations().Single(m => m.GetType().Name == "CreateCustomerLedger");

    private static CustomerLedgerEntry Entry(
        Guid partner, int seq, string type, decimal debit, decimal credit, decimal balance, string refNo = "SINV-20260920-0001") => new()
    {
        UUID = Guid.NewGuid(), PartnerId = partner, SequenceNo = seq,
        EntryDate = new DateTime(2026, 9, 20).AddDays(seq), EntryType = type,
        ReferenceType = "SalesInvoice", ReferenceId = Guid.NewGuid(), ReferenceNumber = refNo,
        DebitAmount = debit, CreditAmount = credit, RunningBalance = balance,
        CreatedBy = 1, CreatedDate = DateTime.UtcNow
    };

    // ── The table ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_ledger_is_mapped_into_the_finance_schema_as_customer_ledger()
    {
        var model  = Model();
        var entity = model.FindEntityType(typeof(CustomerLedgerEntry))!;

        entity.GetTableName().Should().Be("customer_ledger");
        (entity.GetSchema() ?? model.GetDefaultSchema()).Should().Be("finance");
    }

    [Fact]
    public void The_entity_is_tenant_scoped_with_a_unique_uuid_and_a_query_filter()
    {
        var entity = Model().FindEntityType(typeof(CustomerLedgerEntry))!;

        typeof(ITenantScopedEntity).IsAssignableFrom(typeof(CustomerLedgerEntry)).Should().BeTrue();
        entity.FindProperty(nameof(ITenantScopedEntity.OrganizationId)).Should().NotBeNull();
        entity.GetQueryFilter().Should().NotBeNull("a customer's account must never be visible to another organization");
        entity.GetIndexes().Any(i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["UUID"])).Should().BeTrue();
    }

    [Fact]
    public void The_sequence_is_unique_per_partner_and_the_statement_lookup_is_indexed()
    {
        var entity = Model().FindEntityType(typeof(CustomerLedgerEntry))!;

        static bool Has(IEntityType e, bool unique, params string[] props) =>
            e.GetIndexes().Any(i => i.IsUnique == unique && i.Properties.Select(p => p.Name).SequenceEqual(props));

        // The concurrency guard: the loser of a race for the next SequenceNo hits this and retries.
        Has(entity, true, "OrganizationId", "PartnerId", "SequenceNo").Should().BeTrue();
        // §17.2 — (org, partner, entry_date).
        Has(entity, false, "OrganizationId", "PartnerId", "EntryDate").Should().BeTrue();
    }

    [Fact]
    public void It_mirrors_the_supplier_ledger_column_for_column()
    {
        // "Mirrors existing SupplierLedger pattern": the same precision and lengths wherever the two
        // have the same column, so a payable and a receivable are booked to the same rounding.
        using var db = SqlServerContext();
        var customer = db.Model.FindEntityType(typeof(CustomerLedgerEntry))!;
        var supplier = db.Model.FindEntityType(typeof(SupplierLedgerEntry))!;

        void Same(string customerProp, string supplierProp)
        {
            var c = customer.FindProperty(customerProp)!;
            var s = supplier.FindProperty(supplierProp)!;
            c.GetColumnType().Should().Be(s.GetColumnType(), $"{customerProp} mirrors {supplierProp}");
            c.GetMaxLength().Should().Be(s.GetMaxLength(), $"{customerProp} mirrors {supplierProp}");
            c.IsNullable.Should().Be(s.IsNullable, $"{customerProp} mirrors {supplierProp}");
        }

        Same(nameof(CustomerLedgerEntry.DebitAmount),      nameof(SupplierLedgerEntry.DebitAmount));
        Same(nameof(CustomerLedgerEntry.CreditAmount),     nameof(SupplierLedgerEntry.CreditAmount));
        Same(nameof(CustomerLedgerEntry.RunningBalance),   nameof(SupplierLedgerEntry.BalanceAfter));
        Same(nameof(CustomerLedgerEntry.ReferenceType),    nameof(SupplierLedgerEntry.ReferenceType));
        Same(nameof(CustomerLedgerEntry.ReferenceNumber),  nameof(SupplierLedgerEntry.ReferenceNo));
        Same(nameof(CustomerLedgerEntry.Narration),        nameof(SupplierLedgerEntry.Narration));
        Same(nameof(CustomerLedgerEntry.SequenceNo),       nameof(SupplierLedgerEntry.SequenceNo));
        Same(nameof(CustomerLedgerEntry.EntryDate),        nameof(SupplierLedgerEntry.EntryDate));
        Same(nameof(CustomerLedgerEntry.ReferenceId),      nameof(SupplierLedgerEntry.ReferenceId));
    }

    [Fact]
    public void The_ledger_is_append_only_it_has_no_modified_or_deleted_columns()
    {
        // A correction is a new offsetting entry; there is nothing here to update or soft-delete.
        var names = Model().FindEntityType(typeof(CustomerLedgerEntry))!.GetProperties().Select(p => p.Name).ToList();

        names.Should().NotContain(["ModifiedBy", "ModifiedDate", "IsDelete", "IsActive"]);
        names.Should().Contain(["CreatedBy", "CreatedDate"]);
    }

    [Fact]
    public void Column_shapes_match_the_spec_and_the_module()
    {
        var entity = Model().FindEntityType(typeof(CustomerLedgerEntry))!;

        entity.FindProperty(nameof(CustomerLedgerEntry.EntryType))!.GetMaxLength().Should().Be(20);
        entity.FindProperty(nameof(CustomerLedgerEntry.EntryType))!.IsNullable.Should().BeFalse();
        entity.FindProperty(nameof(CustomerLedgerEntry.CurrencyCode))!.GetDefaultValue().Should().Be("PKR");
        entity.FindProperty(nameof(CustomerLedgerEntry.CurrencyCode))!.GetMaxLength().Should().Be(10);
        entity.FindProperty(nameof(CustomerLedgerEntry.Narration))!.IsNullable.Should().BeTrue();

        // Every one of §10's columns is a column here.
        entity.GetProperties().Select(p => p.Name).Should().Contain(
        [
            "Id", "OrganizationId", "PartnerId", "EntryDate", "EntryType", "ReferenceType", "ReferenceId",
            "ReferenceNumber", "DebitAmount", "CreditAmount", "RunningBalance", "CurrencyCode", "Narration",
            "CreatedBy", "CreatedDate"
        ]);
    }

    [Fact]
    public void The_entry_type_vocabulary_is_exactly_the_seven_of_section_10()
    {
        CustomerLedgerEntryTypes.All.Should().BeEquivalentTo(
            ["INVOICE", "PAYMENT", "CREDIT_NOTE", "DEBIT_NOTE", "ADVANCE", "REFUND", "OPENING_BAL"]);
        CustomerLedgerEntryTypes.All.Should().OnlyHaveUniqueItems();
        CustomerLedgerEntryTypes.All.Should().OnlyContain(t => t.Length <= 20, "the column is nvarchar(20)");
    }

    [Fact]
    public void The_partner_and_the_referenced_document_are_bare_uuids_with_no_foreign_key()
    {
        var entity = Model().FindEntityType(typeof(CustomerLedgerEntry))!;

        entity.GetForeignKeys().Should().BeEmpty(
            "partners live in another module's context, and a ledger entry must outlive a document being tidied away");
        entity.GetReferencingForeignKeys().Should().BeEmpty();
    }

    // ── Round trips ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_customers_entries_round_trip_including_a_negative_balance()
    {
        var org = Guid.NewGuid();
        var partner = Guid.NewGuid();
        await using var db = InMemory(org);

        // Invoiced 4000, paid 2500, then a 2000 payment: the customer has overpaid by 500.
        db.CustomerLedgerEntries.AddRange(
            Entry(partner, 1, "INVOICE", 4000m, 0m, 4000m),
            Entry(partner, 2, "PAYMENT", 0m, 2500m, 1500m, "CPAY-20260921-0001"),
            Entry(partner, 3, "PAYMENT", 0m, 2000m, -500m, "CPAY-20260922-0001"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var entries = await db.CustomerLedgerEntries.OrderBy(e => e.SequenceNo).ToListAsync();
        entries.Select(e => e.RunningBalance).Should().Equal(4000m, 1500m, -500m);
        entries.Should().OnlyContain(e => e.OrganizationId == org, "stamped on write");
        entries.Should().OnlyContain(e => e.CurrencyCode == "PKR");
        entries.Select(e => e.EntryType).Should().Equal("INVOICE", "PAYMENT", "PAYMENT");
    }

    [Fact]
    public async Task Another_organizations_ledger_is_invisible()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var a = InMemory(Guid.NewGuid(), dbName);
        a.CustomerLedgerEntries.Add(Entry(Guid.NewGuid(), 1, "INVOICE", 100m, 0m, 100m));
        await a.SaveChangesAsync();

        await using var b = InMemory(Guid.NewGuid(), dbName);
        (await b.CustomerLedgerEntries.ToListAsync()).Should().BeEmpty();
    }

    // ── The migration ─────────────────────────────────────────────────────────

    [Fact]
    public void The_migration_creates_exactly_the_one_table_and_touches_nothing_else()
    {
        var up = LedgerMigration().UpOperations;

        var table = up.OfType<CreateTableOperation>().Should().ContainSingle().Subject;
        table.Name.Should().Be("customer_ledger");
        table.Schema.Should().Be("finance");
        table.ForeignKeys.Should().BeEmpty();
        up.Should().OnlyContain(op => op is CreateTableOperation || op is CreateIndexOperation,
            "nothing on the invoice or payment tables may ride along");

        var indexes = up.OfType<CreateIndexOperation>().ToList();
        indexes.Should().Contain(i => i.Columns.SequenceEqual(new[] { "OrganizationId", "PartnerId", "SequenceNo" }) && i.IsUnique);
        indexes.Should().Contain(i => i.Columns.SequenceEqual(new[] { "OrganizationId", "PartnerId", "EntryDate" }) && !i.IsUnique);
    }

    [Fact]
    public void Rolling_the_migration_back_drops_exactly_that_table()
    {
        var migration = LedgerMigration();

        migration.DownOperations.Should().ContainSingle().Which.Should().BeOfType<DropTableOperation>()
                 .Which.Name.Should().Be("customer_ledger");
    }

    [Fact]
    public void The_migration_comes_after_the_two_receivable_migrations_before_it()
    {
        var names = Migrations().Select(m => m.GetType().Name).ToList();

        names.IndexOf("CreateCustomerLedger").Should().BeGreaterThan(names.IndexOf("CreateCustomerPaymentTables"));
        names.IndexOf("CreateCustomerLedger").Should().BeGreaterThan(names.IndexOf("CreateSalesInvoiceTables"));
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
