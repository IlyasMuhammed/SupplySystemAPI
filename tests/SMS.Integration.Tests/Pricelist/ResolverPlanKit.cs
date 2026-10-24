using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Integration.Tests.Pricelist;

/// <summary>A46 C11 (A46-0290..0302) â€” scratch-LocalDB kit: creates its own database, applies the Demand migrations, seeds supplier pricelists.</summary>
internal static class ResolverPlanKit
{
    public const string Server = @"(localdb)\MSSQLLocalDB";
    public static readonly Guid OrgA = Guid.Parse("a0000000-0000-0000-0000-0000000000a1");
    public static readonly Guid OrgB = Guid.Parse("b0000000-0000-0000-0000-0000000000b2");
    public static readonly Guid Currency = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    public static readonly DateTime OrderDate = new(2026, 10, 17);
    public static readonly DateTime DeliveryDate = new(2026, 11, 15);

    public static Guid ProductGuid(int i) => new(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);
    public static Guid SupplierGuid(int i) => new(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 2]);
    /// <summary>Product p is sold by suppliers (p%30), (p%30+10)%30, (p%30+20)%30 â€” 3 suppliers per product.</summary>
    public static Guid SupplierFor(int product, int slot) => SupplierGuid((product % 30 + slot * 10) % 30 + 1);

    public static bool LocalDbAvailable()
    {
        try
        {
            using var c = new SqlConnection($"Server={Server};Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=15");
            c.Open();
            return true;
        }
        catch { return false; }
    }

    public static string ConnStr(string db) =>
        $"Server={Server};Database={db};Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30;Max Pool Size=200";

    public static DemandDbContext NewContext(string db, Guid org, params IInterceptor[] interceptors)
    {
        var b = new DbContextOptionsBuilder<DemandDbContext>().UseSqlServer(ConnStr(db));
        if (interceptors.Length > 0) b.AddInterceptors(interceptors);
        return new DemandDbContext(b.Options, new StaticTenantContext { OrganizationId = org });
    }

    public static async Task<string> CreateDatabaseAsync(string prefix)
    {
        var db = $"SMS_A46_{prefix}_{Guid.NewGuid():N}"[..40];
        await using var c = new SqlConnection(ConnStr("master"));
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{db}]";
        await cmd.ExecuteNonQueryAsync();
        await using var ctx = NewContext(db, OrgA);
        await ctx.Database.MigrateAsync();
        return db;
    }

    public static async Task DropDatabaseAsync(string db)
    {
        try
        {
            SqlConnection.ClearAllPools();
            await using var c = new SqlConnection(ConnStr("master"));
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END";
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* scratch DB; best effort */ }
    }

    /// <summary>products x 3 suppliers x 4 qty tiers x 2 date ranges for org A (+ ~10% copy for org B). Mixed date bases, every 13th row inactive,
    /// every 5th product has a variant-specific row on top of the product-level one.</summary>
    public static async Task<int> SeedAsync(string db, int products)
    {
        var t = new DataTable();
        string[] cols = ["UUID", "OrganizationId", "ProductUuid", "VariantUuid", "SupplierUuid", "VendorProductCode", "VendorProductName", "MinQuantity", "UnitPrice",
            "CurrencyId", "PricingDateBasis", "ValidFrom", "ValidTo", "LeadTimeDays", "Priority", "Notes", "IsActive", "CreatedBy", "CreatedDate"];
        Type[] types = [typeof(Guid), typeof(Guid), typeof(Guid), typeof(Guid), typeof(Guid), typeof(string), typeof(string), typeof(decimal), typeof(decimal),
            typeof(Guid), typeof(byte), typeof(DateTime), typeof(DateTime), typeof(int), typeof(int), typeof(string), typeof(bool), typeof(int), typeof(DateTime)];
        for (var i = 0; i < cols.Length; i++) t.Columns.Add(cols[i], types[i]);

        decimal[] tiers = [1m, 10m, 50m, 100m];
        (DateTime, DateTime)[] ranges = [(new(2026, 1, 1), new(2026, 6, 30)), (new(2026, 7, 1), new(2026, 12, 31))];
        var n = 0;
        void Add(Guid org, int p, Guid? variant, Guid supplier, decimal tier, int rangeIx, int slot)
        {
            n++;
            var (from, to) = ranges[rangeIx];
            t.Rows.Add(Guid.NewGuid(), org, ProductGuid(p), variant is null ? DBNull.Value : variant.Value, supplier, $"VP-{p}", $"Vendor product {p}",
                tier, 100m - (decimal)Math.Log10((double)tier + 1) * 5m, Currency, (byte)((n + slot) % 2), from, to,
                7 + slot, slot, DBNull.Value, n % 13 != 0, 1, DateTime.UtcNow);
        }

        for (var p = 1; p <= products; p++)
            for (var s = 0; s < 3; s++)
                foreach (var tier in tiers)
                    for (var r = 0; r < 2; r++)
                    {
                        Add(OrgA, p, null, SupplierFor(p, s), tier, r, s);
                        if (p % 10 == 0) Add(OrgB, p, null, SupplierFor(p, s), tier, r, s);
                    }
        // Variant-specific rows: one tier-1 row per (product, supplier 0, range) for every 5th product.
        for (var p = 5; p <= products; p += 5)
            for (var r = 0; r < 2; r++)
                Add(OrgA, p, VariantGuid(p), SupplierFor(p, 0), 1m, r, 0);

        await using var c = new SqlConnection(ConnStr(db));
        await c.OpenAsync();
        using var bulk = new SqlBulkCopy(c) { DestinationTableName = "demand.SupplierPricelists", BatchSize = 5000, BulkCopyTimeout = 300 };
        foreach (var col in cols) bulk.ColumnMappings.Add(col, col);
        await bulk.WriteToServerAsync(t);
        await using var stats = c.CreateCommand();
        stats.CommandText = "UPDATE STATISTICS demand.SupplierPricelists WITH FULLSCAN";
        await stats.ExecuteNonQueryAsync();
        return n;
    }

    public static Guid VariantGuid(int p) => new(p, 0, 0, [0, 0, 0, 0, 0, 0, 0, 3]);

    // â”€â”€ capture the resolver's real EF SQL â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public sealed record Captured(string Sql, List<SqlParameter> Parameters);

    public sealed class SqlCapture : DbCommandInterceptor
    {
        public List<Captured> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("SupplierPricelists", StringComparison.Ordinal))
                lock (Commands)
                    Commands.Add(new Captured(command.CommandText, command.Parameters.Cast<DbParameter>()
                        .Select(p => new SqlParameter(p.ParameterName, p.Value ?? DBNull.Value) { DbType = p.DbType, Size = p.Size, Precision = ((IDbDataParameter)p).Precision, Scale = ((IDbDataParameter)p).Scale }).ToList()));
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }
    }

    public static PriceResolutionRequest Request(int product, int supplierSlot, decimal qty, bool withDelivery = true) => new()
    {
        VariantUuid = Guid.Empty, // no Inventory in the scratch host -> the resolver takes the product branch from ProductUuid
        ProductUuid = ProductGuid(product),
        SupplierUuid = SupplierFor(product, supplierSlot),
        Quantity = qty,
        OrderDate = OrderDate,
        ExpectedDeliveryDate = withDelivery ? DeliveryDate : null,
        CurrencyId = Currency
    };

    public static async Task<PriceResolutionResult> ResolveAsync(string db, PriceResolutionRequest req, SqlCapture? capture = null)
    {
        await using var ctx = capture is null ? NewContext(db, OrgA) : NewContext(db, OrgA, capture);
        var resolver = new PurchasePriceResolver(ctx);
        return await resolver.ResolveAsync(req);
    }

    // â”€â”€ measure one statement: STATISTICS IO/TIME + actual plan XML â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public sealed record Measurement(string Sql, string IoText, string TimeText, string PlanXml, long LogicalReads, int Rows,
        bool HasSeek, bool HasScan, int KeyLookups, string[] IndexesUsed, double ServerElapsedMs);

    public static async Task<Measurement> MeasureAsync(string db, string sql, IEnumerable<SqlParameter> parameters, bool dropCache = false, string? prelude = null)
    {
        await using var c = new SqlConnection(ConnStr(db));
        var info = new StringBuilder();
        c.InfoMessage += (_, e) => info.AppendLine(e.Message);
        c.FireInfoMessageEventOnUserErrors = false;
        await c.OpenAsync();
        if (dropCache)
        {
            await using var d = c.CreateCommand();
            d.CommandText = "CHECKPOINT";
            await d.ExecuteNonQueryAsync();
        }
        async Task Exec(string s) { await using var x = c.CreateCommand(); x.CommandText = s; await x.ExecuteNonQueryAsync(); }

        if (prelude is not null) await Exec(prelude);
        // Warm the cache + plan cache, then measure.
        await RunAsync(c, sql, parameters, null);
        await Exec("SET STATISTICS IO ON; SET STATISTICS TIME ON;");
        info.Clear();
        var plan = new StringBuilder();
        var rows = await RunAsync(c, sql, parameters, plan, xmlPlan: true);
        await Exec("SET STATISTICS IO OFF; SET STATISTICS TIME OFF;");

        var io = string.Join(Environment.NewLine, info.ToString().Split('\n').Where(l => l.Contains("logical reads")));
        var time = string.Join(Environment.NewLine, info.ToString().Split('\n').Where(l => l.Contains("elapsed time") || l.Contains("CPU time")));
        long reads = Regex.Matches(io, @"logical reads (\d+)").Sum(m => long.Parse(m.Groups[1].Value));
        var xml = plan.ToString();
        var ops = Regex.Matches(xml, "<RelOp [^>]*PhysicalOp=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        var idx = Regex.Matches(xml, "<Object [^>]*Index=\"\\[([^\\]]+)\\]\"").Select(m => m.Groups[1].Value).Distinct().ToArray();
        // "Key Lookup" appears as PhysicalOp="Clustered Index Seek" with Lookup="1" on the nested IndexScan element.
        var lookups = Regex.Matches(xml, "<IndexScan [^>]*Lookup=\"(1|true)\"").Count + ops.Count(o => o == "Key Lookup" || o == "RID Lookup");
        var elapsed = Regex.Matches(time, @"elapsed time = (\d+) ms").Select(m => double.Parse(m.Groups[1].Value)).LastOrDefault();
        return new Measurement(sql, io.Trim(), time.Trim(), xml, reads, rows,
            ops.Any(o => o.EndsWith("Seek", StringComparison.Ordinal)),
            ops.Any(o => o.EndsWith("Scan", StringComparison.Ordinal)),
            lookups, idx, elapsed);
    }

    private static async Task<int> RunAsync(SqlConnection c, string sql, IEnumerable<SqlParameter> parameters, StringBuilder? plan, bool xmlPlan = false)
    {
        if (xmlPlan) { await using var on = c.CreateCommand(); on.CommandText = "SET STATISTICS XML ON"; await on.ExecuteNonQueryAsync(); }
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters)
            cmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value) { DbType = p.DbType, Size = p.Size, Precision = p.Precision, Scale = p.Scale });
        var rows = 0;
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            do
            {
                if (r.FieldCount == 1 && r.GetName(0).StartsWith("Microsoft SQL Server 2005 XML Showplan", StringComparison.Ordinal))
                { while (await r.ReadAsync()) plan?.Append(r.GetString(0)); }
                else while (await r.ReadAsync()) rows++;
            } while (await r.NextResultAsync());
        }
        if (xmlPlan) { await using var off = c.CreateCommand(); off.CommandText = "SET STATISTICS XML OFF"; await off.ExecuteNonQueryAsync(); }
        return rows;
    }

    public static async Task<(long Reads, int Rows, string Plan)> ProbeAsync(string db, string sql, params SqlParameter[] ps)
    {
        var m = await MeasureAsync(db, sql, ps);
        return (m.LogicalReads, m.Rows, m.PlanXml);
    }
}

/// <summary>[Fact] that skips (instead of failing) when (localdb)\MSSQLLocalDB is not reachable.</summary>
public sealed class LocalDbFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Ok = new(ResolverPlanKit.LocalDbAvailable);
    public LocalDbFactAttribute() { if (!Ok.Value) Skip = "LocalDB (localdb)\\MSSQLLocalDB is not available."; }
}

