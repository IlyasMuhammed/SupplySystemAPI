using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Lookups.Data;
using SMS.Modules.Lookups.Domain;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Lookups.Tests;

/// <summary>A37 (OPSA) D-16 / §6 — lookup values carry an app-maintained ModifiedAt; the sync reader pages them by type.</summary>
public class SyncLookupReaderTests
{
    private static LookupsDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LookupsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());

    private static LookupType Type(LookupsDbContext db, string slug)
    {
        var type = new LookupType { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true };
        db.LookupTypes.Add(type);
        return type;
    }

    [Fact]
    public async Task Values_are_stamped_and_read_by_type_strictly_after_since()
    {
        await using var db = NewDb();
        var uom = Type(db, ISyncLookupReader.Uoms);
        var tax = Type(db, ISyncLookupReader.TaxCodes);
        var pcs = new LookupValue { Id = Guid.NewGuid(), TypeId = uom.Id, DisplayName = "Piece (PCS)" };
        db.LookupValues.AddRange(pcs, new LookupValue { Id = Guid.NewGuid(), TypeId = tax.Id, DisplayName = "GST 17%" });
        await db.SaveChangesAsync();
        var firstStamp = pcs.ModifiedAt;
        await Task.Delay(20);
        db.LookupValues.Add(new LookupValue { Id = Guid.NewGuid(), TypeId = uom.Id, DisplayName = "Kilogram (KG)" });
        pcs.Notes = "each";
        await db.SaveChangesAsync();

        pcs.ModifiedAt.Should().BeAfter(firstStamp, "an update moves it");
        var reader = new SyncLookupReader(db);

        (await reader.GetChangedAsync(ISyncLookupReader.TaxCodes, null, 500)).Rows.Select(r => r.Name).Should().Equal("GST 17%");
        var uoms = await reader.GetChangedAsync(ISyncLookupReader.Uoms, firstStamp, 500);
        uoms.Rows.Select(r => r.Name).Should().BeEquivalentTo("Piece (PCS)", "Kilogram (KG)");
        uoms.HasMore.Should().BeFalse();
        (await reader.GetChangedAsync(ISyncLookupReader.Uoms, pcs.ModifiedAt, 500)).Rows.Should().BeEmpty("strictly after");
    }

    [Fact]
    public async Task A_page_never_splits_one_timestamp_and_reports_more()
    {
        await using var db = NewDb();
        var uom = Type(db, ISyncLookupReader.Uoms);
        db.LookupValues.AddRange(Enumerable.Range(1, 3).Select(i =>
            new LookupValue { Id = Guid.NewGuid(), TypeId = uom.Id, DisplayName = $"U{i}" }));
        await db.SaveChangesAsync(); // one SaveChanges = one stamp for all three
        await Task.Delay(20);
        db.LookupValues.Add(new LookupValue { Id = Guid.NewGuid(), TypeId = uom.Id, DisplayName = "Later" });
        await db.SaveChangesAsync();

        var page = await new SyncLookupReader(db).GetChangedAsync(ISyncLookupReader.Uoms, null, 2);

        page.Rows.Select(r => r.Name).Should().BeEquivalentTo("U1", "U2", "U3");
        page.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task The_schema_guard_is_a_no_op_off_SQL_Server()
    {
        await using var db = NewDb();
        await FluentActions.Awaiting(() => new LookupsDataSeeder(db).EnsureModifiedAtColumnAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task On_SQL_Server_the_startup_guard_adds_the_column_once_and_replays()
    {
        var master = Environment.GetEnvironmentVariable("SMS_TEST_SQLSERVER")
            ?? "Server=(localdb)\\mssqllocaldb;Database=master;Trusted_Connection=True;TrustServerCertificate=True;";
        try { await using var probe = new SqlConnection(master); await probe.OpenAsync(); }
        catch { return; } // skip when LocalDB is unreachable

        var database = $"SMS_LkpA37_{Guid.NewGuid():N}";
        var builder  = new SqlConnectionStringBuilder(master) { InitialCatalog = "master" };
        await ExecAsync(builder.ConnectionString, $"CREATE DATABASE [{database}]");
        builder.InitialCatalog = database;
        LookupsDbContext Db() => new(new DbContextOptionsBuilder<LookupsDbContext>().UseSqlServer(builder.ConnectionString).Options, new StaticTenantContext());

        try
        {
            await using (var db = Db())
            {
                await db.GetInfrastructure().GetRequiredService<IRelationalDatabaseCreator>().CreateTablesAsync();
                var type = Type(db, ISyncLookupReader.Uoms);
                db.LookupValues.Add(new LookupValue { Id = Guid.NewGuid(), TypeId = type.Id, DisplayName = "Piece (PCS)" });
                await db.SaveChangesAsync();
                // Back to the pre-A37 shape (the seeder is what runs on the shared database at startup).
                await db.Database.ExecuteSqlRawAsync(LookupValueModifiedAtSql.Down);
            }

            for (var run = 0; run < 2; run++)
                await using (var db = Db()) await new LookupsDataSeeder(db).EnsureModifiedAtColumnAsync();

            await using (var db = Db())
            {
                var rows = await new SyncLookupReader(db).GetChangedAsync(ISyncLookupReader.Uoms, null, 500);
                rows.Rows.Should().ContainSingle().Which.ModifiedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-5));
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
