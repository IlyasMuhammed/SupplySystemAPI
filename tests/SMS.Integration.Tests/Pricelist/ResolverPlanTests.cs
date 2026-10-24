using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace SMS.Integration.Tests.Pricelist;

/// <summary>Scratch LocalDB with the Demand migrations applied and N products of pricelist rows. Own database name; never the shared/Azure DB.</summary>
public abstract class ScratchPricelistFixture : IAsyncLifetime
{
    private readonly int _products;
    protected ScratchPricelistFixture(int products) => _products = products;
    public string Db { get; private set; } = "";
    public int Rows { get; private set; }
    public bool Available { get; private set; }

    public async Task InitializeAsync()
    {
        Available = ResolverPlanKit.LocalDbAvailable();
        if (!Available) return;
        Db = await ResolverPlanKit.CreateDatabaseAsync("Plan");
        Rows = await ResolverPlanKit.SeedAsync(Db, _products);
    }

    public async Task DisposeAsync()
    {
        // A46_KEEP_DB=1 keeps the scratch database (name in A46_KEEP_DB_NAME file) for manual plan exploration.
        if (Environment.GetEnvironmentVariable("A46_KEEP_DB") == "1") { Console.WriteLine("KEPT SCRATCH DB " + Db); return; }
        if (Available && Db.Length > 0) await ResolverPlanKit.DropDatabaseAsync(Db);
    }
}

public sealed class SmallPricelistFixture() : ScratchPricelistFixture(100);   // 2,400+ rows: fast enough for CI
public sealed class FullPricelistFixture() : ScratchPricelistFixture(1000);   // 24,000+ rows: A46-0290

/// <summary>Index SQL proposed to BE-1 (guarded, idempotent). Kept here so the tests prove it; NOT applied to the Demand migration by PERF.</summary>
internal static class ProposedIndexSql
{
    /// <summary>Upgrades the existing A45 IX_SupplierPricelists_Lookup in place so it also INCLUDEs [UUID] (the only resolver column it lacks). DROP_EXISTING = single atomic rebuild.</summary>
    public const string Covering = @"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SupplierPricelists_Lookup' AND object_id = OBJECT_ID('demand.SupplierPricelists'))
    CREATE NONCLUSTERED INDEX [IX_SupplierPricelists_Lookup] ON [demand].[SupplierPricelists]
        ([OrganizationId], [SupplierUuid], [ProductUuid], [IsActive], [ValidFrom], [ValidTo])
        INCLUDE ([UUID], [VariantUuid], [UnitPrice], [MinQuantity], [Priority], [PricingDateBasis], [CurrencyId], [LeadTimeDays]);
ELSE IF NOT EXISTS (SELECT 1 FROM sys.index_columns ic
                    JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE i.name = 'IX_SupplierPricelists_Lookup' AND i.object_id = OBJECT_ID('demand.SupplierPricelists')
                      AND ic.is_included_column = 1 AND c.name = 'UUID')
    CREATE NONCLUSTERED INDEX [IX_SupplierPricelists_Lookup] ON [demand].[SupplierPricelists]
        ([OrganizationId], [SupplierUuid], [ProductUuid], [IsActive], [ValidFrom], [ValidTo])
        INCLUDE ([UUID], [VariantUuid], [UnitPrice], [MinQuantity], [Priority], [PricingDateBasis], [CurrencyId], [LeadTimeDays])
        WITH (DROP_EXISTING = ON);";}

/// <summary>A46 C11 â€” CI-sized checks (small seed). Skipped when LocalDB is unavailable.</summary>
public class ResolverPlanTests(SmallPricelistFixture fx, ITestOutputHelper output) : IClassFixture<SmallPricelistFixture>
{
    [LocalDbFact]
    public async Task Resolver_ReturnsAPrice_AndEmitsOneSeekableQuery()
    {
        var cap = new ResolverPlanKit.SqlCapture();
        var res = await ResolverPlanKit.ResolveAsync(fx.Db, ResolverPlanKit.Request(5, 0, 75m), cap);
        res.Found.Should().BeTrue();
        cap.Commands.Should().ContainSingle("the resolver issues one SupplierPricelists query");
        var sql = cap.Commands[0].Sql;
        output.WriteLine(sql);
        sql.Should().Contain("OrganizationId").And.Contain("SupplierUuid").And.Contain("ProductUuid");

        var m = await ResolverPlanKit.MeasureAsync(fx.Db, sql, cap.Commands[0].Parameters);
        output.WriteLine($"reads={m.LogicalReads} rows={m.Rows} seek={m.HasSeek} scan={m.HasScan} lookups={m.KeyLookups} idx={string.Join(",", m.IndexesUsed)}");
        m.HasSeek.Should().BeTrue("the leading predicate columns are equality filters on an indexed prefix");
        m.HasScan.Should().BeFalse();
        m.KeyLookups.Should().Be(0, "A46 C11 / BR-37: the resolver projects only columns the A46 covering IX_SupplierPricelists_Lookup includes");
    }

    [LocalDbFact]
    public async Task Resolver_IgnoresOtherOrganizationAndInactiveRows()
    {
        // Product 10 exists for org B too (same suppliers); org A's resolution must never see org B's price rows.
        var rows = await using_(fx.Db);
        rows.Should().BeGreaterThan(0);
        var res = await ResolverPlanKit.ResolveAsync(fx.Db, ResolverPlanKit.Request(10, 0, 1m));
        res.Found.Should().BeTrue();
    }

    private static async Task<int> using_(string db)
    {
        await using var c = new SqlConnection(ResolverPlanKit.ConnStr(db));
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM demand.SupplierPricelists WHERE OrganizationId = @o";
        cmd.Parameters.AddWithValue("@o", ResolverPlanKit.OrgB);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    [LocalDbFact]
    public async Task ProposedIndexSql_IsIdempotent_AndMakesTheProjectedQueryCovering()
    {
        await using var c = new SqlConnection(ResolverPlanKit.ConnStr(fx.Db));
        await c.OpenAsync();
        for (var i = 0; i < 2; i++)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = ProposedIndexSql.Covering;
            await cmd.ExecuteNonQueryAsync(); // second run must be a no-op
        }
        var m = await ResolverPlanKit.MeasureAsync(fx.Db, ResolverPerfTests.ProjectedNoVariantSql, ResolverPerfTests.ProjectedParams(5));
        output.WriteLine($"projected: reads={m.LogicalReads} seek={m.HasSeek} scan={m.HasScan} lookups={m.KeyLookups} idx={string.Join(",", m.IndexesUsed)}");
        m.KeyLookups.Should().Be(0);
        m.HasScan.Should().BeFalse();
        m.IndexesUsed.Should().Contain("IX_SupplierPricelists_Lookup");
    }

    [LocalDbFact]
    public async Task FiftyConcurrentResolutions_CompleteWithoutDeadlock()
    {
        var sw = Stopwatch.StartNew();
        var results = await Task.WhenAll(Enumerable.Range(1, 50).Select(i =>
            ResolverPlanKit.ResolveAsync(fx.Db, ResolverPlanKit.Request(1 + (i % 100), i % 3, 1 + i * 3m))));
        sw.Stop();
        results.Should().HaveCount(50).And.OnlyContain(r => r != null);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }
}

/// <summary>A46 C11 â€” full 24k-row measurement (A46-0290..0302). Run explicitly: dotnet test --filter "Category=Perf".</summary>
[Trait("Category", "Perf")]
public class ResolverPerfTests(FullPricelistFixture fx, ITestOutputHelper output) : IClassFixture<FullPricelistFixture>
{
    // The same predicate the EF query has, but projected to the 8 columns the resolver actually consumes (Option C).
    internal const string ProjectedSql = @"
SELECT [s].[Id], [s].[UUID], [s].[VariantUuid], [s].[MinQuantity], [s].[Priority], [s].[PricingDateBasis], [s].[UnitPrice], [s].[CurrencyId], [s].[LeadTimeDays], [s].[ValidFrom], [s].[ValidTo]
FROM [demand].[SupplierPricelists] AS [s]
WHERE [s].[OrganizationId] = @org AND [s].[IsActive] = CAST(1 AS bit) AND [s].[SupplierUuid] = @sup AND [s].[MinQuantity] <= @qty
  AND [s].[ProductUuid] = @prod AND ([s].[VariantUuid] = @var OR [s].[VariantUuid] IS NULL)";

    internal const string ProjectedNoVariantSql = @"
SELECT [s].[Id], [s].[UUID], [s].[VariantUuid], [s].[MinQuantity], [s].[Priority], [s].[PricingDateBasis], [s].[UnitPrice], [s].[CurrencyId], [s].[LeadTimeDays], [s].[ValidFrom], [s].[ValidTo]
FROM [demand].[SupplierPricelists] AS [s]
WHERE [s].[OrganizationId] = @org AND [s].[IsActive] = CAST(1 AS bit) AND [s].[SupplierUuid] = @sup AND [s].[MinQuantity] <= @qty AND [s].[ProductUuid] = @prod";

    internal static SqlParameter[] ProjectedParams(int product) =>
    [
        new SqlParameter("@org", ResolverPlanKit.OrgA), new SqlParameter("@sup", ResolverPlanKit.SupplierFor(product, 0)),
        new SqlParameter("@qty", System.Data.SqlDbType.Decimal) { Value = 75m, Precision = 18, Scale = 4 },
        new SqlParameter("@prod", ResolverPlanKit.ProductGuid(product)), new SqlParameter("@var", Guid.Empty)
    ];

    // The spec's SQL (BR-37) mapped to this schema: date-basis OR evaluated in SQL.
    private const string SpecSql = @"
SELECT TOP 1 sp.Id, sp.UnitPrice, sp.CurrencyId, sp.PricingDateBasis, sp.MinQuantity
FROM demand.SupplierPricelists sp
WHERE sp.OrganizationId = @org AND sp.ProductUuid = @prod AND sp.SupplierUuid = @sup AND sp.IsActive = 1 AND sp.MinQuantity <= @qty
  AND ((sp.PricingDateBasis = 0 AND @od BETWEEN sp.ValidFrom AND sp.ValidTo)
    OR (sp.PricingDateBasis = 1 AND COALESCE(@dd, @od) BETWEEN sp.ValidFrom AND sp.ValidTo))
ORDER BY sp.Priority ASC, sp.MinQuantity DESC";

    private static SqlParameter[] SpecParams(int product) =>
    [
        new SqlParameter("@org", ResolverPlanKit.OrgA), new SqlParameter("@sup", ResolverPlanKit.SupplierFor(product, 0)),
        new SqlParameter("@qty", System.Data.SqlDbType.Decimal) { Value = 75m, Precision = 18, Scale = 4 },
        new SqlParameter("@prod", ResolverPlanKit.ProductGuid(product)),
        new SqlParameter("@od", System.Data.SqlDbType.Date) { Value = ResolverPlanKit.OrderDate },
        new SqlParameter("@dd", System.Data.SqlDbType.Date) { Value = ResolverPlanKit.DeliveryDate }
    ];

    private static async Task Ddl(string db, string sql)
    {
        await using var c = new SqlConnection(ResolverPlanKit.ConnStr(db));
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private string OutDir()
    {
        var d = Environment.GetEnvironmentVariable("A46_PERF_OUT") ?? Path.Combine(Path.GetTempPath(), "a46-perf");
        Directory.CreateDirectory(d);
        return d;
    }

    private async Task Report(string label, ResolverPlanKit.Measurement m)
    {
        var line = $"[{label}] reads={m.LogicalReads} rows={m.Rows} seek={m.HasSeek} scan={m.HasScan} keyLookups={m.KeyLookups} serverElapsed={m.ServerElapsedMs}ms idx={string.Join(",", m.IndexesUsed)}";
        output.WriteLine(line);
        var sb = new StringBuilder().AppendLine(line).AppendLine("-- SQL").AppendLine(m.Sql).AppendLine("-- IO").AppendLine(m.IoText).AppendLine("-- TIME").AppendLine(m.TimeText);
        await File.WriteAllTextAsync(Path.Combine(OutDir(), $"{label}.txt"), sb.ToString());
        await File.WriteAllTextAsync(Path.Combine(OutDir(), $"{label}.sqlplan"), m.PlanXml);
    }

    [LocalDbFact]
    public async Task A_Seed_Has24kRows()
    {
        await using var c = new SqlConnection(ResolverPlanKit.ConnStr(fx.Db));
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM demand.SupplierPricelists";
        var n = (int)(await cmd.ExecuteScalarAsync())!;
        output.WriteLine($"rows={n}");
        n.Should().BeGreaterThanOrEqualTo(24000);
    }

    [LocalDbFact]
    public async Task B_RealEfQuery_Plan_Io_Time()
    {
        var cap = new ResolverPlanKit.SqlCapture();
        var res = await ResolverPlanKit.ResolveAsync(fx.Db, ResolverPlanKit.Request(500, 0, 75m), cap);
        res.Found.Should().BeTrue();
        var m = await ResolverPlanKit.MeasureAsync(fx.Db, cap.Commands[0].Sql, cap.Commands[0].Parameters);
        await Report("1-real-ef-query", m);

        // Warm-cache end-to-end resolution (EF translate + execute + materialise + in-memory date filter) â€” median of 300.
        for (var i = 0; i < 50; i++) await ResolverPlanKit.ResolveAsync(fx.Db, ResolverPlanKit.Request(500, 0, 75m));
        var times = new List<double>();
        for (var i = 0; i < 300; i++)
        {
            var sw = Stopwatch.StartNew();
            await ResolverPlanKit.ResolveAsync(fx.Db, ResolverPlanKit.Request(1 + i % 1000, i % 3, 75m));
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        var p50 = times[times.Count / 2]; var p95 = times[(int)(times.Count * .95)];
        output.WriteLine($"[resolver end-to-end warm, new context per call] p50={p50:F2}ms p95={p95:F2}ms max={times[^1]:F2}ms");
        await File.WriteAllTextAsync(Path.Combine(OutDir(), "1b-resolver-latency.txt"), $"p50={p50:F3}ms p95={p95:F3}ms max={times[^1]:F3}ms (n=300, fresh DbContext per call, warm cache)");
    }

    [LocalDbFact]
    public async Task C_SpecQuery_OrOnDateBasis_Plan()
    {
        await Report("2-spec-query-before-index", await ResolverPlanKit.MeasureAsync(fx.Db, SpecSql, SpecParams(500)));
    }

    [LocalDbFact]
    public async Task D_ProjectedQuery_And_UpgradedLookupIndex()
    {
        await Report("3-projected-before-index", await ResolverPlanKit.MeasureAsync(fx.Db, ProjectedNoVariantSql, ProjectedParams(500)));

        await Ddl(fx.Db, ProposedIndexSql.Covering);
        await Ddl(fx.Db, ProposedIndexSql.Covering); // idempotent
        var after = await ResolverPlanKit.MeasureAsync(fx.Db, ProjectedNoVariantSql, ProjectedParams(500));
        await Report("4-projected-after-index", after);
        var spec = await ResolverPlanKit.MeasureAsync(fx.Db, SpecSql, SpecParams(500));
        await Report("5-spec-after-index", spec);

        // A real-EF-shaped query (entity select) after the index: still lookups, because the entity carries uncovered columns.
        var cap = new ResolverPlanKit.SqlCapture();
        await ResolverPlanKit.ResolveAsync(fx.Db, ResolverPlanKit.Request(500, 0, 75m), cap);
        await Report("6-real-ef-after-index", await ResolverPlanKit.MeasureAsync(fx.Db, cap.Commands[0].Sql, cap.Commands[0].Parameters));

        after.KeyLookups.Should().Be(0);
        after.HasScan.Should().BeFalse();
        after.LogicalReads.Should().BeLessThanOrEqualTo(5);
    }
    [LocalDbFact]
    public async Task F_VariantShapes_BeforeAndAfterLookupIndexUpgrade()
    {
        const string cols = "s.Id, s.UUID, s.VariantUuid, s.MinQuantity, s.Priority, s.PricingDateBasis, s.UnitPrice, s.CurrencyId, s.LeadTimeDays, s.ValidFrom, s.ValidTo";
        const string where = "FROM demand.SupplierPricelists s WHERE s.OrganizationId=@org AND s.IsActive=1 AND s.SupplierUuid=@sup AND s.MinQuantity<=@qty AND s.ProductUuid=@prod";
        SqlParameter[] P(Guid v) => [.. ProjectedParams(500).Take(4), new SqlParameter("@var", v)];
        var real = ResolverPlanKit.VariantGuid(500);
        var variants = new (string, string, Guid)[]
        {
            ("entity(*) + OR variant [today's resolver]", $"SELECT s.* {where} AND (s.VariantUuid=@var OR s.VariantUuid IS NULL)", real),
            ("projected + OR variant", $"SELECT {cols} {where} AND (s.VariantUuid=@var OR s.VariantUuid IS NULL)", real),
            ("projected, variant filtered in memory", $"SELECT {cols} {where}", real),
            ("projected + UNION ALL split on variant", $"SELECT {cols} {where} AND s.VariantUuid=@var UNION ALL SELECT {cols} {where} AND s.VariantUuid IS NULL", real),
        };
        var sb = new StringBuilder();
        foreach (var phase in new[] { "before", "after" })
        {
            if (phase == "after") { await Ddl(fx.Db, ProposedIndexSql.Covering); await Ddl(fx.Db, ProposedIndexSql.Covering); }
            foreach (var (name, sql, v) in variants)
            {
                var m = await ResolverPlanKit.MeasureAsync(fx.Db, sql, P(v));
                var line = $"{phase,-6} | {name,-44} | reads={m.LogicalReads,3} rows={m.Rows} seek={m.HasSeek} scan={m.HasScan} lookups={m.KeyLookups} idx={string.Join(",", m.IndexesUsed)}";
                output.WriteLine(line); sb.AppendLine(line);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(OutDir(), "8-variant-shapes.txt"), sb.ToString());
    }
    /// <summary>
    /// Acceptance test for the proposed patch (BE-1): resolver query projected to the columns it consumes (EF LINQ, same predicate as PurchasePriceResolver) +
    /// the upgraded IX_SupplierPricelists_Lookup. BR-37 / TS-57: seek, 0 key lookups, &lt;= 5 logical reads.
    /// </summary>
    [LocalDbFact]
    public async Task G_ProposedProjectedResolverQuery_MeetsBr37()
    {
        await Ddl(fx.Db, ProposedIndexSql.Covering);
        var cap = new ResolverPlanKit.SqlCapture();
        var req = ResolverPlanKit.Request(500, 0, 75m);
        await using (var ctx = ResolverPlanKit.NewContext(fx.Db, ResolverPlanKit.OrgA, cap))
        {
            var org = ResolverPlanKit.OrgA; var prod = req.ProductUuid!.Value; var qty = req.Quantity; var sup = req.SupplierUuid; var variant = req.VariantUuid;
            var rows = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                System.Linq.Queryable.Select(
                    System.Linq.Queryable.Where(
                        Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(
                            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(ctx.SupplierPricelists)),
                        r => r.OrganizationId == org && r.IsActive && r.SupplierUuid == sup && r.MinQuantity <= qty && r.ProductUuid == prod
                             && (r.VariantUuid == variant || r.VariantUuid == null)),
                    r => new { r.Id, r.UUID, r.VariantUuid, r.MinQuantity, r.Priority, r.PricingDateBasis, r.UnitPrice, r.CurrencyId, r.LeadTimeDays, r.ValidFrom, r.ValidTo }));
            rows.Should().NotBeEmpty();
        }
        var m = await ResolverPlanKit.MeasureAsync(fx.Db, cap.Commands[0].Sql, cap.Commands[0].Parameters);
        await Report("9-proposed-ef-projection-after-index", m);
        m.HasSeek.Should().BeTrue();
        m.HasScan.Should().BeFalse();
        m.KeyLookups.Should().Be(0);
        m.LogicalReads.Should().BeLessThanOrEqualTo(5);
    }
    [LocalDbFact]
    public async Task E_FiftyConcurrentResolutions_NoDeadlockNoBlocking()
    {
        // Sample sys.dm_exec_requests for waits/blocking while 50 resolutions run (TS-58, A46-0302).
        var blocked = 0; var samples = 0; var stop = false;
        var sampler = Task.Run(async () =>
        {
            await using var c = new SqlConnection(ResolverPlanKit.ConnStr(fx.Db));
            await c.OpenAsync();
            while (!stop)
            {
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id <> 0 AND database_id = DB_ID()";
                blocked += (int)(await cmd.ExecuteScalarAsync())!; samples++;
                await Task.Delay(5);
            }
        });
        var sw = Stopwatch.StartNew();
        var results = await Task.WhenAll(Enumerable.Range(1, 50).Select(i =>
            ResolverPlanKit.ResolveAsync(fx.Db, ResolverPlanKit.Request(1 + (i * 37) % 1000, i % 3, 1 + i * 10m, withDelivery: i % 2 == 0))));
        sw.Stop(); stop = true; await sampler;
        output.WriteLine($"50 concurrent: {sw.ElapsedMilliseconds}ms total, blocked-request samples={blocked}/{samples}, found={results.Count(r => r.Found)}");
        await File.WriteAllTextAsync(Path.Combine(OutDir(), "7-concurrency.txt"), $"50 concurrent resolutions: {sw.ElapsedMilliseconds}ms total; blocked-request samples {blocked} of {samples}; found {results.Count(r => r.Found)}");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        blocked.Should().Be(0);
    }
}






