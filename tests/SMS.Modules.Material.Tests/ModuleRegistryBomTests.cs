using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Material.Controllers;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Repositories;
using SMS.Modules.Material.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Material.Tests;

/// <summary>
/// A37 (OPSA) — D-11 BOM usage (BOM-SHR-04 ordering) and the read/write feature gates (BOM-SHR-02/03), D-16 ModifiedAt on
/// BOM header and lines, the QI sub-feature gate, and D-18 impact providers.
/// </summary>
public class ModuleRegistryBomTests
{
    private const int Author = 7, Reviewer = 8;

    private sealed class Harness
    {
        public MaterialDbContext Material { get; }
        public InventoryDbContext Inventory { get; }
        public BomRepository Repo { get; }
        private int _sequence;

        public Harness()
        {
            var tenant = new StaticTenantContext();
            Material = new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
            Inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
            var numbers = new Mock<IDocumentNumberGenerator>();
            numbers.Setup(n => n.NextAsync("BOM", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(() => $"BOM-2026-{++_sequence:D5}");
            Repo = new BomRepository(Material, Inventory, numbers.Object);
        }

        public (Guid Product, Guid Variant) Product(string name, string type)
        {
            var product = new Product
            {
                Uuid = Guid.NewGuid(), Sku = $"SKU-{Guid.NewGuid():N}"[..12], Name = name, UomCode = "PCS", ProductType = type,
                SupplyMethod = ProductTypeRules.DefaultSupplyMethod(type), IsActive = true, CreatedBy = 1
            };
            var variant = new ProductVariant
            {
                Uuid = Guid.NewGuid(), Sku = $"{product.Sku}-1", VariantName = "Default", IsDefault = true, IsActive = true,
                PurchasePrice = 5m, IsAvailableForProduction = true, CreatedBy = 1
            };
            product.Variants.Add(variant);
            Inventory.Products.Add(product);
            Inventory.SaveChanges();
            return (product.Uuid, variant.Uuid);
        }

        public async Task<Guid> BomAsync(string usage, Guid? input = null)
        {
            var (product, _) = Product($"FG {Guid.NewGuid():N}"[..10], ProductType.FinishedGood);
            var line = input ?? Product($"RM {Guid.NewGuid():N}"[..10], ProductType.RawMaterial).Variant;
            return await Repo.CreateAsync(new CreateBomRequest
            {
                ProductUuid = product, BomUsage = usage,
                Lines = [new BomLineRequest { MaterialVariantUuid = line, Quantity = 1m }]
            }, Author);
        }
    }

    // ── D-11 usage ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_BOM_is_universal_unless_told_otherwise_and_codes_are_normalised()
    {
        var h = new Harness();
        var (product, _) = h.Product("Table", ProductType.FinishedGood);
        var (_, wood) = h.Product("Wood", ProductType.RawMaterial);

        var plain = await h.Repo.CreateAsync(new CreateBomRequest
            { ProductUuid = product, Lines = [new BomLineRequest { MaterialVariantUuid = wood, Quantity = 1m }] }, Author);
        var service = await h.BomAsync(" service_preferred ");

        (await h.Repo.GetByUuidAsync(plain))!.BomUsage.Should().Be(BomUsage.Universal);
        (await h.Repo.GetByUuidAsync(service))!.BomUsage.Should().Be(BomUsage.ServicePreferred);
        await FluentActions.Invoking(() => h.BomAsync("SOMETIMES"))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*not a BOM usage*");
    }

    [Fact]
    public async Task Usage_changes_in_any_status_but_obsolete_and_a_new_version_keeps_it()
    {
        var h = new Harness();
        var uuid = await h.BomAsync(BomUsage.Universal);
        await h.Repo.SubmitAsync(uuid, Author);
        await h.Repo.ApproveAsync(uuid, Reviewer);
        await h.Repo.ActivateAsync(uuid, Reviewer);

        await h.Repo.SetUsageAsync(uuid, "production_preferred", Author);
        (await h.Repo.GetByUuidAsync(uuid))!.BomUsage.Should().Be(BomUsage.ProductionPreferred, "ACTIVE is not terminal");

        var next = await h.Repo.NewVersionAsync(uuid, Author);
        (await h.Repo.GetByUuidAsync(next))!.BomUsage.Should().Be(BomUsage.ProductionPreferred);

        await h.Repo.ObsoleteAsync(uuid, Reviewer, "replaced");
        await FluentActions.Invoking(() => h.Repo.SetUsageAsync(uuid, BomUsage.Universal, Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*obsolete*");
    }

    [Theory]
    [InlineData("PRODUCTION", new[] { BomUsage.ProductionPreferred, BomUsage.Universal, BomUsage.ServicePreferred })]
    [InlineData("service",    new[] { BomUsage.ServicePreferred, BomUsage.Universal, BomUsage.ProductionPreferred })]
    public async Task PreferFor_lists_the_matching_usage_first_then_universal_and_hides_nothing(string preferFor, string[] expected)
    {
        var h = new Harness();
        foreach (var usage in new[] { BomUsage.Universal, BomUsage.ServicePreferred, BomUsage.ProductionPreferred })
            await h.BomAsync(usage);

        var list = await h.Repo.GetListAsync(new BomListFilter { PreferFor = preferFor });

        list.Data.Select(b => b.BomUsage).Should().Equal(expected);
    }

    [Fact]
    public async Task An_unknown_preference_is_refused()
    {
        var h = new Harness();
        await FluentActions.Invoking(() => h.Repo.GetListAsync(new BomListFilter { PreferFor = "SALES" }))
            .Should().ThrowAsync<BadRequestException>();
    }

    // ── D-16 ModifiedAt ───────────────────────────────────────────────────────

    [Fact]
    public async Task ModifiedAt_moves_on_the_header_and_lines_when_a_draft_is_edited()
    {
        var h = new Harness();
        var uuid = await h.BomAsync(BomUsage.Universal);
        var bom = await h.Material.BillsOfMaterials.Include(b => b.Lines).SingleAsync(b => b.UUID == uuid);
        var (header, line) = (bom.ModifiedAt, bom.Lines.Single().ModifiedAt);
        await Task.Delay(20);

        await h.Repo.UpdateAsync(uuid, new UpdateBomRequest { Notes = "revised", BomUsage = BomUsage.ServicePreferred }, Author);
        bom.Lines.Single().Quantity = 2m;
        await h.Material.SaveChangesAsync();

        bom.ModifiedAt.Should().BeAfter(header);
        bom.Lines.Single().ModifiedAt.Should().BeAfter(line);
        (await h.Repo.GetByUuidAsync(uuid))!.ModifiedAt.Should().Be(bom.ModifiedAt);
        bom.BomUsage.Should().Be(BomUsage.ServicePreferred);
    }

    // ── Gates (BOM-SHR-02/03, §1.3) ──────────────────────────────────────────

    private static IReadOnlyList<string>? Gate(MethodInfo m) => m.GetCustomAttribute<RequiresFeatureAttribute>()?.AnyOfFeatureCodes;

    [Fact]
    public void BOM_reads_need_inventory_only_and_every_write_needs_BOM_management()
    {
        typeof(BomsController).GetCustomAttribute<RequiresFeatureAttribute>()!.AnyOfFeatureCodes.Should().Equal(ModuleCodes.Inventory);

        var actions = typeof(BomsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        var writes = actions.Where(m => m.GetCustomAttributes().Any(a => a is HttpPostAttribute or HttpPutAttribute or HttpDeleteAttribute)).ToList();
        var reads  = actions.Where(m => m.GetCustomAttribute<HttpGetAttribute>() is not null).ToList();

        writes.Select(m => m.Name).Should().BeEquivalentTo(
            "Create", "Update", "Delete", "Submit", "Approve", "Reject", "Activate", "Obsolete", "NewVersion", "SetUsage");
        writes.Should().OnlyContain(m => Gate(m)!.SequenceEqual(new[] { ModuleCodes.BomManagement }));
        reads.Should().NotBeEmpty().And.OnlyContain(m => Gate(m) == null);
    }

    [Fact]
    public void Quality_inspection_needs_its_sub_feature_on_top_of_manufacturing()
    {
        // Two class-level gates, all must pass (REG's AllowMultiple semantics).
        typeof(QualityInspectionsController).GetCustomAttributes<RequiresFeatureAttribute>().Select(a => a.FeatureCode)
            .Should().BeEquivalentTo(ModuleCodes.Manufacturing, ModuleCodes.QualityInspection);
    }

    // ── D-18 impact ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Impact_counts_open_orders_by_status_for_the_organization_only()
    {
        var h = new Harness();
        var org = TenantDefaults.ScmDemoOrganizationId;
        var other = Guid.NewGuid();
        foreach (var (o, s) in new[]
                 {
                     (org, ProductionOrderStatus.InProgress), (org, ProductionOrderStatus.InProgress), (org, ProductionOrderStatus.Draft),
                     (org, ProductionOrderStatus.Completed), (org, ProductionOrderStatus.Cancelled), (other, ProductionOrderStatus.Ready)
                 })
            h.Material.ProductionOrders.Add(new ProductionOrder { OrganizationId = o, Status = s, ProductionNumber = Guid.NewGuid().ToString("N") });
        foreach (var s in new[] { ServiceOrderStatus.Waiting, ServiceOrderStatus.Closed })
            h.Material.ServiceOrders.Add(new ServiceOrder { OrganizationId = org, Status = s, ServiceNumber = Guid.NewGuid().ToString("N") });
        await h.Material.SaveChangesAsync();

        IModuleImpactProvider production = new ProductionOrderImpactProvider(h.Material);
        IModuleImpactProvider services   = new ServiceOrderImpactProvider(h.Material);

        production.ModuleCode.Should().Be(ModuleCodes.Manufacturing);
        services.ModuleCode.Should().Be(ModuleCodes.Services);
        (await production.GetInProgressAsync(org)).Should().Equal(
            new ModuleImpactItem("Production orders – Draft", 1), new ModuleImpactItem("Production orders – In progress", 2));
        (await services.GetInProgressAsync(org)).Should().Equal(new ModuleImpactItem("Service orders – Waiting", 1));
        (await services.GetInProgressAsync(other)).Should().BeEmpty();
    }

    // ── LocalDB: A37_BomUsageAndModifiedAt is additive, guarded, backfilled ──

    [Fact]
    public async Task The_A37_migration_backfills_and_replays_on_sql_server()
    {
        var master = Environment.GetEnvironmentVariable("SMS_TEST_SQLSERVER")
            ?? "Server=(localdb)\\mssqllocaldb;Database=master;Trusted_Connection=True;TrustServerCertificate=True;";
        try { await using var probe = new SqlConnection(master); await probe.OpenAsync(); }
        catch { return; } // skip when LocalDB is unreachable (same arrangement as the A34 migration test)

        var database = $"SMS_MatA37_{Guid.NewGuid():N}";
        var builder  = new SqlConnectionStringBuilder(master) { InitialCatalog = "master" };
        await ExecAsync(builder.ConnectionString, $"CREATE DATABASE [{database}]");
        builder.InitialCatalog = database;
        var connection = builder.ConnectionString;
        MaterialDbContext Db() => new(new DbContextOptionsBuilder<MaterialDbContext>().UseSqlServer(connection).Options, new StaticTenantContext());

        try
        {
            var updated = new DateTime(2025, 5, 6, 7, 8, 9);
            await using (var db = Db())
            {
                await db.GetInfrastructure().GetRequiredService<IRelationalDatabaseCreator>().CreateTablesAsync();
                db.BillsOfMaterials.Add(new BillOfMaterial
                {
                    BomNumber = "BOM-1", ProductUuid = Guid.NewGuid(), Version = 1, BaseUom = "PCS", UpdatedAt = updated,
                    Lines = [new BillOfMaterialLine { MaterialVariantUuid = Guid.NewGuid(), MaterialProductUuid = Guid.NewGuid(), Quantity = 1, Uom = "PCS" }]
                });
                await db.SaveChangesAsync();
            }

            var migration = new SMS.Modules.Material.Migrations.A37_BomUsageAndModifiedAt();
            foreach (var op in migration.DownOperations.OfType<SqlOperation>())
                await ExecAsync(connection, op.Sql);
            for (var run = 0; run < 2; run++) // the second run is the replay on an already-migrated database
                foreach (var op in migration.UpOperations.OfType<SqlOperation>())
                    await ExecAsync(connection, op.Sql);

            await using (var db = Db())
            {
                var bom = await db.BillsOfMaterials.IgnoreQueryFilters().Include(b => b.Lines).SingleAsync();
                bom.BomUsage.Should().Be(BomUsage.Universal, "the DEFAULT fills existing BOMs");
                bom.ModifiedAt.Should().Be(updated, "backfilled from UpdatedAt");
                bom.Lines.Single().ModifiedAt.Should().Be(updated, "lines take their header's UpdatedAt");
            }
        }
        finally
        {
            SqlConnection.ClearAllPools();
            builder.InitialCatalog = "master";
            await ExecAsync(builder.ConnectionString,
                $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
        }

        static async Task ExecAsync(string connection, string sql)
        {
            await using var conn = new SqlConnection(connection);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
