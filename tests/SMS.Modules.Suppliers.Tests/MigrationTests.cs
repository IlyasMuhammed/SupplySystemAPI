using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SMS.Modules.Suppliers.Data;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

// Addendum 29, P1-01 — suppliers.Suppliers renamed to suppliers.BusinessPartners. Only the table
// (and the index/constraint names EF derives from it) moves in this migration; the C# entity,
// DbSet and every repository/service keep the name Supplier — that is a separate, later task.
public class MigrationTests
{
    private static SuppliersDbContext SqlServerContext()
    {
        var options = new DbContextOptionsBuilder<SuppliersDbContext>()
            .UseSqlServer("Server=none;Database=none;Trusted_Connection=True;")
            .Options;

        return new SuppliersDbContext(options, new StaticTenantContext());
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

    private static Migration RenameMigration() =>
        Migrations().Single(m => m.GetType().Name == "RenameSuppliersToBusinessPartners");

    private static Migration PartnerTypeMigration() =>
        Migrations().Single(m => m.GetType().Name == "AddPartnerTypeFlagsAndCarrierFields");

    private static Migration EntityRenameMigration() =>
        Migrations().Single(m => m.GetType().Name == "BusinessPartnerEntityRename");

    [Fact]
    public void The_rename_migration_comes_after_every_migration_already_deployed()
    {
        // Ordering invariant, not an exact list — the latter would have to be edited by every
        // future migration, which makes it a chore rather than a guard.
        var names = Migrations().Select(m => m.GetType().Name).ToList();

        names.Take(6).Should().Equal(new List<string>
        {
            "InitialCreate",
            "FsdSupplierFields",
            "AddSupplierScorecard",
            "AddSupplierScoreSnapshotTrend",
            "SyncSuppliersModelSnapshot",
            "AddOrganizationIdTenantScoping"
        });

        names.Should().Contain("RenameSuppliersToBusinessPartners");
        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_migration_renames_the_table_and_nothing_else_is_dropped_or_altered()
    {
        var up = RenameMigration().UpOperations;

        var renamed = up.OfType<RenameTableOperation>().ToList();
        renamed.Should().ContainSingle();
        renamed[0].Schema.Should().Be("suppliers");
        renamed[0].Name.Should().Be("Suppliers");
        renamed[0].NewName.Should().Be("BusinessPartners");
        renamed[0].NewSchema.Should().Be("suppliers");

        // No structural loss: this is a rename plus the constraint/index renames SQL Server's
        // naming convention forces, not a schema change. Real business data (five child tables'
        // worth of contacts, documents, bank details, type/industry mappings) rides through.
        up.OfType<DropTableOperation>().Should().BeEmpty();
        up.OfType<DropColumnOperation>().Should().BeEmpty();
        up.OfType<AlterColumnOperation>().Should().BeEmpty();
        up.OfType<CreateTableOperation>().Should().BeEmpty();
    }

    [Fact]
    public void The_five_child_table_foreign_keys_are_dropped_and_recreated_pointing_at_the_new_name()
    {
        // SQL Server foreign-key constraint names embed the principal table's name, so a rename
        // forces EF to drop and recreate each one — this is boilerplate the rename requires, not
        // an incidental change, and every recreated FK must still cascade and still point at
        // BusinessPartners(Id), not at a dangling Suppliers.
        var up = RenameMigration().UpOperations;

        var dropped = up.OfType<DropForeignKeyOperation>().Select(op => op.Table).ToList();
        var added   = up.OfType<AddForeignKeyOperation>().ToList();

        var expectedTables = new[]
        {
            "SupplierBankDetails", "SupplierContacts", "SupplierDocuments",
            "SupplierIndustryMappings", "SupplierTypeMappings"
        };

        dropped.Should().BeEquivalentTo(expectedTables);
        added.Select(op => op.Table).Should().BeEquivalentTo(expectedTables);

        foreach (var fk in added)
        {
            fk.PrincipalSchema.Should().Be("suppliers");
            fk.PrincipalTable.Should().Be("BusinessPartners");
            fk.PrincipalColumns.Should().ContainSingle().Which.Should().Be("Id");
            fk.OnDelete.Should().Be(ReferentialAction.Cascade);
        }
    }

    [Fact]
    public void Rolling_the_migration_back_restores_the_original_table_and_foreign_keys()
    {
        var migration = RenameMigration();

        var renamedBack = migration.DownOperations.OfType<RenameTableOperation>().Single();
        renamedBack.Name.Should().Be("BusinessPartners");
        renamedBack.NewName.Should().Be("Suppliers");

        migration.DownOperations.OfType<AddForeignKeyOperation>()
            .Should().OnlyContain(fk => fk.PrincipalTable == "Suppliers");
    }

    // ── P1-02 — partner type flags & carrier/service fields ──────────────────

    [Fact]
    public void The_partner_type_migration_comes_after_the_rename()
    {
        var names = Migrations().Select(m => m.GetType().Name).ToList();
        names.IndexOf("AddPartnerTypeFlagsAndCarrierFields")
            .Should().BeGreaterThan(names.IndexOf("RenameSuppliersToBusinessPartners"));
    }

    [Fact]
    public void The_migration_only_adds_columns_and_indexes_nothing_existing_is_touched()
    {
        var up = PartnerTypeMigration().UpOperations;

        up.OfType<DropTableOperation>().Should().BeEmpty();
        up.OfType<DropColumnOperation>().Should().BeEmpty();
        up.OfType<AlterColumnOperation>().Should().BeEmpty();
        up.OfType<RenameTableOperation>().Should().BeEmpty();
        up.OfType<RenameColumnOperation>().Should().BeEmpty();
        up.OfType<DropForeignKeyOperation>().Should().BeEmpty();

        var added = up.OfType<AddColumnOperation>().ToDictionary(op => op.Name);
        added.Keys.Should().BeEquivalentTo(new[]
        {
            "PartnerType", "IsVendor", "IsCustomer", "IsCarrier", "IsServiceProvider",
            "VehicleTypes", "ServiceCategories"
        });
        added.Values.Should().OnlyContain(op => op.Table == "BusinessPartners" && op.Schema == "suppliers");
    }

    [Fact]
    public void Every_existing_row_backfills_to_vendor_via_the_column_defaults_not_a_separate_update()
    {
        // The task asked for "UPDATE existing rows SET partner_type='VENDOR', is_vendor=1" — that
        // is exactly what a NOT NULL column's DEFAULT does to pre-existing rows at ADD COLUMN
        // time in SQL Server, so the defaults themselves are the backfill (verified for real
        // against a throwaway database — see the task notes). The other three flags default false
        // for the same existing-rows-are-all-vendors reason.
        var up = PartnerTypeMigration().UpOperations.OfType<AddColumnOperation>()
            .ToDictionary(op => op.Name);

        up["PartnerType"].IsNullable.Should().BeFalse();
        up["PartnerType"].DefaultValue.Should().Be("VENDOR");

        up["IsVendor"].IsNullable.Should().BeFalse();
        up["IsVendor"].DefaultValue.Should().Be(true);

        up["IsCustomer"].DefaultValue.Should().Be(false);
        up["IsCarrier"].DefaultValue.Should().Be(false);
        up["IsServiceProvider"].DefaultValue.Should().Be(false);

        // Free-text lists, not flags — nullable, no default is meaningful for either.
        up["VehicleTypes"].IsNullable.Should().BeTrue();
        up["ServiceCategories"].IsNullable.Should().BeTrue();
    }

    [Fact]
    public void Rolling_back_the_partner_type_migration_drops_exactly_what_it_added()
    {
        var migration = PartnerTypeMigration();

        var added   = migration.UpOperations.OfType<AddColumnOperation>().Select(op => op.Name);
        var dropped = migration.DownOperations.OfType<DropColumnOperation>().Select(op => op.Name);

        dropped.Should().BeEquivalentTo(added);
    }

    // ── P1-03 — the entity rename (Supplier -> BusinessPartner) is CLR-only ──

    [Fact]
    public void The_entity_rename_migration_touches_no_sql_at_all()
    {
        // Renaming the C# class changes nothing about the table, its columns, or its
        // constraints — the table was already BusinessPartners (P1-01) and every column mapping
        // is unchanged. This migration exists purely so the model snapshot's generated C# refers
        // to BusinessPartner instead of a now-nonexistent Supplier type; it must be a genuine
        // no-op against the database, matching the established SyncSuppliersModelSnapshot
        // precedent in this same module's history.
        var migration = EntityRenameMigration();

        migration.UpOperations.Should().BeEmpty();
        migration.DownOperations.Should().BeEmpty();
    }

    // ── P1-07 (Addendum 29 §1.1/§1.7) — FK integrity after the rename, at the model level ────
    //
    // The migration-shape tests above prove the ONE rename migration recreates the 5 FKs
    // correctly. This complements them by checking the CURRENT model — the thing every other
    // migration builds on top of — still declares all 5 relationships pointing at BusinessPartner,
    // which the shape tests alone would not catch if a later change quietly broke one of them
    // without touching the rename migration itself. EF Core's InMemory provider (used by every
    // other test in this project) does not enforce real FK constraints, so this is the closest an
    // automated test gets to "FK integrity" without a live SQL Server — see the P1-01 task notes
    // for the actual live-database proof (LocalDB round: real row, real constraint names, rollback).

    [Theory]
    [InlineData("SupplierContacts")]
    [InlineData("SupplierBankDetails")]
    [InlineData("SupplierDocuments")]
    [InlineData("SupplierTypeMappings")]
    [InlineData("SupplierIndustryMappings")]
    public void The_current_model_still_points_every_child_table_at_BusinessPartners(string childTable)
    {
        using var db = SqlServerContext();
        var model = db.Model;

        var entityType = model.GetEntityTypes()
            .Single(e => e.GetTableName() == childTable);

        var fk = entityType.GetForeignKeys().Single();

        fk.PrincipalEntityType.GetTableName().Should().Be("BusinessPartners");
        fk.PrincipalEntityType.ClrType.Name.Should().Be("BusinessPartner");
        fk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
    }

    // ── The test that stops the model and the migrations drifting apart ──────

    [Fact]
    public void There_are_no_model_changes_still_waiting_for_a_migration()
    {
        using var db = SqlServerContext();

        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;
        snapshot.Should().NotBeNull("the module must have a model snapshot");

        var snapshotModel = snapshot!.Model;
        if (snapshotModel is IMutableModel mutable) snapshotModel = mutable.FinalizeModel();

        snapshotModel = db.GetService<IModelRuntimeInitializer>()
            .Initialize(snapshotModel, designTime: true, validationLogger: null);

        var differences = db.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshotModel.GetRelationalModel(),
            db.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        differences.Should().BeEmpty(
            "the entities and the migration snapshot have drifted — run 'dotnet ef migrations add'");
    }
}
