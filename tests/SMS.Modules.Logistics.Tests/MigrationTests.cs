using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SMS.Modules.Logistics.Data;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-08 — the migration and its startup wiring.
//
// Applying a migration needs a real SQL Server, so TC-08.1/08.2 were verified by hand against a
// throwaway LocalDB database (see the task notes). What lives here is everything that can be
// checked without one — and the last test is the important one: it fails the build the moment
// someone edits an entity and forgets the migration.
public class MigrationTests
{
    private static LogisticsDbContext SqlServerContext()
    {
        var options = new DbContextOptionsBuilder<LogisticsDbContext>()
            .UseSqlServer("Server=none;Database=none;Trusted_Connection=True;")
            .Options;

        return new LogisticsDbContext(options, new StaticTenantContext());
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

    private static Migration FourLayerMigration() =>
        Migrations().Single(m => m.GetType().Name == "AddDeliveryFourLayerSchema");

    [Fact]
    public void The_rebuild_migrations_come_after_the_two_that_were_already_deployed()
    {
        // Asserts the ordering invariant, not an exact list — the latter would have to be edited
        // by every future migration, which makes it a chore rather than a guard.
        var names = Migrations().Select(m => m.GetType().Name).ToList();

        // These two are already applied in environments we do not control, so they must stay
        // first and unedited. (Passing the expected list explicitly: the params overload of
        // Equal would read a trailing "because" string as another expected element.)
        names.Take(2).Should().Equal(
            new List<string> { "InitialLogisticsSchema", "AddOrganizationIdTenantScoping" });

        names.Should().Contain("AddDeliveryFourLayerSchema");
        names.Should().OnlyHaveUniqueItems();
    }

    // ── The migration only adds ──────────────────────────────────────────────

    [Fact]
    public void Applying_the_migration_creates_the_eight_new_tables_and_nothing_else()
    {
        var created = FourLayerMigration().UpOperations
            .OfType<CreateTableOperation>()
            .Select(op => op.Name)
            .ToList();

        created.Should().BeEquivalentTo(
        [
            "addresses",
            "delivery_orders", "delivery_order_lines",
            "shipment_packages", "package_contents",
            "consignments", "consignment_deliveries", "consignment_stops"
        ]);
    }

    [Fact]
    public void No_rebuild_migration_destroys_anything()
    {
        // The legacy carriers and shipments tables hold real master data and are still queried
        // by SMS.Modules.Reports. Nothing added by the rebuild may touch them until T-16 retires
        // them deliberately — so this covers every migration after the two that predate it, not
        // just the one that happened to be current when it was written.
        var rebuildMigrations = Migrations()
            .Where(m => m.GetType().Name is not (
                "InitialLogisticsSchema" or "AddOrganizationIdTenantScoping"
                // "Deliberately" arrived. This is the one migration that retires something, and it
                // was an approved decision rather than something slipped past: it drops
                // Carrier.RatePerKg (F39 — multiplied by nothing, superseded by rate cards) and
                // Shipment.ProofOfDeliveryUrl (F47 — a path into wwwroot, superseded by T-61's
                // stored artefacts). Named here one migration at a time, so the rule still catches
                // the next thing that tries this without being asked.
                or "RetireRatePerKgAndProofOfDeliveryUrl"))
            .ToList();

        rebuildMigrations.Should().NotBeEmpty();

        foreach (var migration in rebuildMigrations)
        {
            var name = migration.GetType().Name;
            var up   = migration.UpOperations;

            up.OfType<DropTableOperation>().Should().BeEmpty($"{name} must not drop a table");
            up.OfType<DropColumnOperation>().Should().BeEmpty($"{name} must not drop a column");
            up.OfType<AlterColumnOperation>().Should().BeEmpty($"{name} must not alter a column");
            up.OfType<RenameTableOperation>().Should().BeEmpty($"{name} must not rename a table");
            up.OfType<RenameColumnOperation>().Should().BeEmpty($"{name} must not rename a column");
            up.OfType<DropForeignKeyOperation>().Should().BeEmpty($"{name} must not drop a foreign key");
        }
    }

    [Fact]
    public void Rolling_the_migration_back_drops_exactly_what_it_created()
    {
        var migration = FourLayerMigration();

        var created = migration.UpOperations.OfType<CreateTableOperation>().Select(o => o.Name);
        var dropped = migration.DownOperations.OfType<DropTableOperation>().Select(o => o.Name);

        dropped.Should().BeEquivalentTo(created);

        // And it must not take the legacy tables with it.
        dropped.Should().NotContain("carriers").And.NotContain("shipments");
    }

    // ── Guards on the schema the migration actually writes ───────────────────

    [Fact]
    public void The_booking_idempotency_index_is_unique_and_filtered()
    {
        // The guard against a retry booking a second real parcel. It has to be filtered, because
        // without the filter every unbooked consignment would collide on a NULL key.
        var index = FourLayerMigration().UpOperations
            .OfType<CreateIndexOperation>()
            .Single(op => op.Table == "consignments" && op.Columns.Contains("BookingIdempotencyKey"));

        index.IsUnique.Should().BeTrue();
        index.Filter.Should().Contain("BookingIdempotencyKey").And.Contain("NOT NULL");
    }

    [Fact]
    public void No_table_is_reachable_by_two_cascading_paths()
    {
        // SQL Server refuses to create such a constraint, and it does so at CREATE TABLE time
        // rather than at model-build time — so this reproduces the rule here instead of waiting
        // to find out on a deployment.
        var tables = FourLayerMigration().UpOperations.OfType<CreateTableOperation>().ToList();

        var cascadeEdges = tables
            .SelectMany(t => t.ForeignKeys
                .Where(fk => fk.OnDelete == ReferentialAction.Cascade)
                .Select(fk => (Child: t.Name, Parent: fk.PrincipalTable)))
            .ToList();

        foreach (var table in tables.Select(t => t.Name))
        {
            var ancestors = cascadeEdges
                .Where(e => e.Child == table)
                .SelectMany(e => Roots(e.Parent))
                .ToList();

            ancestors.Should().OnlyHaveUniqueItems(
                $"'{table}' would be reachable from the same ancestor by two cascade paths");
        }

        // Every origin a cascade into `table` can ultimately come from.
        List<string> Roots(string table)
        {
            var parents = cascadeEdges.Where(e => e.Child == table).ToList();
            return parents.Count == 0 ? [table] : [.. parents.SelectMany(p => Roots(p.Parent))];
        }
    }

    [Fact]
    public void Existing_carriers_are_backfilled_to_manual_integration()
    {
        // T-15/TC-15.1. Carriers that predate these columns must not be left with a null
        // integration mode that some later code path could read as "this one has an API".
        var migration = Migrations()
            .Single(m => m.GetType().Name == "ExtendCarrierForCourierIntegration");

        var added = migration.UpOperations
            .OfType<AddColumnOperation>()
            .Where(op => op.Table == "carriers")
            .ToDictionary(op => op.Name, op => op.DefaultValue?.ToString());

        added.Should().ContainKey("ProviderKey").And.ContainKey("IntegrationMode");
        added["ProviderKey"].Should().Be("MANUAL");
        added["IntegrationMode"].Should().Be("MANUAL");
    }

    // ── The test that stops the model and the migrations drifting apart ──────

    [Fact]
    public void There_are_no_model_changes_still_waiting_for_a_migration()
    {
        // Edit an entity, forget the migration, and everything still compiles and every other
        // test still passes — until a deployment finds the mismatch. This catches it here.
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
