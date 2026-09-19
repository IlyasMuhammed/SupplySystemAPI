using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-09 — document numbering.
//
// The logic tests run in memory. The concurrency test does not and cannot: the in-memory provider
// does not enforce concurrency tokens, so a race there would "pass" no matter how broken the
// generator was. That one test runs against SQL Server — see SqlServerFact.
public class DocumentNumberTests
{
    private static DocumentNumberGenerator Generator(LogisticsDbContext db, Guid organizationId) =>
        new(db, new StaticTenantContext { OrganizationId = organizationId });

    // ── TC-09.1 — the first number of the year ───────────────────────────────

    [Fact]
    public async Task The_first_delivery_of_the_year_is_number_one()
    {
        var (db, tenant, _) = LogisticsTestDb.New();

        var number = await Generator(db, tenant.OrganizationId)
            .NextAsync(DocumentNumberPrefix.Delivery, new DateTime(2026, 1, 1));

        number.Should().Be("DLV-2026-00001");
    }

    [Fact]
    public async Task Numbers_are_issued_in_order_and_zero_padded_to_five_digits()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var generator = Generator(db, tenant.OrganizationId);
        var when = new DateTime(2026, 6, 1);

        var issued = new List<string>();
        for (var i = 0; i < 12; i++)
            issued.Add(await generator.NextAsync(DocumentNumberPrefix.Delivery, when));

        issued[0].Should().Be("DLV-2026-00001");
        issued[9].Should().Be("DLV-2026-00010");
        issued[11].Should().Be("DLV-2026-00012");
        issued.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Each_prefix_has_its_own_counter()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var generator = Generator(db, tenant.OrganizationId);
        var when = new DateTime(2026, 3, 1);

        await generator.NextAsync(DocumentNumberPrefix.Delivery, when);
        await generator.NextAsync(DocumentNumberPrefix.Delivery, when);

        var consignment = await generator.NextAsync(DocumentNumberPrefix.Consignment, when);

        consignment.Should().Be("SHP-2026-00001",
            "consignments must not inherit the deliveries' position");
    }

    // ── TC-09.3 — the year boundary ──────────────────────────────────────────

    [Fact]
    public async Task The_sequence_restarts_each_january()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var generator = Generator(db, tenant.OrganizationId);

        await generator.NextAsync(DocumentNumberPrefix.Delivery, new DateTime(2026, 12, 31));
        await generator.NextAsync(DocumentNumberPrefix.Delivery, new DateTime(2026, 12, 31));

        var newYear = await generator.NextAsync(DocumentNumberPrefix.Delivery, new DateTime(2027, 1, 1));

        newYear.Should().Be("DLV-2027-00001");
    }

    [Fact]
    public async Task An_old_year_continues_where_it_left_off()
    {
        // Back-dating a document must not disturb the current year's counter.
        var (db, tenant, _) = LogisticsTestDb.New();
        var generator = Generator(db, tenant.OrganizationId);

        await generator.NextAsync(DocumentNumberPrefix.Delivery, new DateTime(2026, 5, 1));
        await generator.NextAsync(DocumentNumberPrefix.Delivery, new DateTime(2027, 5, 1));

        var backdated = await generator.NextAsync(DocumentNumberPrefix.Delivery, new DateTime(2026, 5, 1));

        backdated.Should().Be("DLV-2026-00002");
    }

    // ── TC-09.4 — organizations are independent ──────────────────────────────

    [Fact]
    public async Task Two_organizations_number_their_documents_independently()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var when = new DateTime(2026, 2, 1);

        var (dbA, _, dbName) = LogisticsTestDb.New(orgA);
        await Generator(dbA, orgA).NextAsync(DocumentNumberPrefix.Delivery, when);
        await Generator(dbA, orgA).NextAsync(DocumentNumberPrefix.Delivery, when);

        await using var dbB = LogisticsTestDb.OpenAs(dbName, orgB);
        var first = await Generator(dbB, orgB).NextAsync(DocumentNumberPrefix.Delivery, when);

        first.Should().Be("DLV-2026-00001");

        // And org A's counter is untouched by org B.
        var third = await Generator(dbA, orgA).NextAsync(DocumentNumberPrefix.Delivery, when);
        third.Should().Be("DLV-2026-00003");
    }

    // ── TC-09.5 — deleting documents never recycles a number ─────────────────

    [Fact]
    public async Task Deleting_documents_does_not_make_a_number_be_issued_twice()
    {
        // The defect in the legacy COUNT(*) + 1 scheme: remove a row and the next call reissues
        // a number that is already printed on a document somewhere. A counter never looks at the
        // documents, so it cannot do that.
        var (db, tenant, _) = LogisticsTestDb.New();
        var generator = Generator(db, tenant.OrganizationId);
        var when = new DateTime(2026, 4, 1);

        var issued = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var number = await generator.NextAsync(DocumentNumberPrefix.Delivery, when);
            issued.Add(number);
            db.DeliveryOrders.Add(new DeliveryOrder
            {
                UUID = Guid.NewGuid(), DeliveryNumber = number,
                Direction  = LogisticsCode.Of(DeliveryDirection.Outbound),
                SourceType = LogisticsCode.Of(DeliverySourceType.Manual),
                CreatedBy  = 1, CreatedDate = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();

        // Soft-delete one, hard-delete another — neither may affect the counter.
        var all = await db.DeliveryOrders.ToListAsync();
        all[0].IsDelete = true;
        db.DeliveryOrders.Remove(all[1]);
        await db.SaveChangesAsync();

        var next = await generator.NextAsync(DocumentNumberPrefix.Delivery, when);

        next.Should().Be("DLV-2026-00004");
        issued.Should().NotContain(next);
    }

    // ── TC-09.6 — overflow fails loudly ──────────────────────────────────────

    [Fact]
    public async Task Exhausting_five_digits_fails_with_an_explanation_rather_than_wrapping()
    {
        var (db, tenant, _) = LogisticsTestDb.New();

        db.DocumentNumberSequences.Add(new DocumentNumberSequence
        {
            OrganizationId = tenant.OrganizationId,
            Prefix         = DocumentNumberPrefix.Delivery,
            Year           = 2026,
            NextValue      = 99_999
        });
        await db.SaveChangesAsync();

        var generator = Generator(db, tenant.OrganizationId);
        var when = new DateTime(2026, 1, 1);

        // The last number that still fits is issued normally.
        (await generator.NextAsync(DocumentNumberPrefix.Delivery, when))
            .Should().Be("DLV-2026-99999");

        // The next one must not silently become DLV-2026-100000 and break the column width, nor
        // wrap around and collide with a number already issued.
        var act = async () => await generator.NextAsync(DocumentNumberPrefix.Delivery, when);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*99,999*")
            .WithMessage("*five-digit*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_prefix_is_rejected(string? prefix)
    {
        var (db, tenant, _) = LogisticsTestDb.New();

        var act = async () => await Generator(db, tenant.OrganizationId).NextAsync(prefix!);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── TC-09.2 — the race, against a real database ──────────────────────────

    [SqlServerFact]
    public async Task Fifty_concurrent_callers_get_fifty_distinct_numbers()
    {
        // This is the test the whole task exists for, and it is meaningless in memory: the
        // in-memory provider ignores concurrency tokens, so every caller would "win" and the
        // assertion would pass against a generator with no protection at all.
        await using var harness = await SqlServerHarness.CreateAsync();

        var organizationId = Guid.NewGuid();
        var when = new DateTime(2026, 7, 1);

        var numbers = await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            // A fresh context per caller, as a real request would have.
            await using var db = harness.NewContext(organizationId);
            return await Generator(db, organizationId).NextAsync(DocumentNumberPrefix.Delivery, when);
        }));

        numbers.Should().OnlyHaveUniqueItems("a duplicate number is a duplicate document");
        numbers.Should().HaveCount(50);

        // Contiguous 1..50 — nothing was skipped or handed out twice.
        numbers.OrderBy(n => n).Should().Equal(
            Enumerable.Range(1, 50).Select(i => $"DLV-2026-{i:D5}"));
    }

    [SqlServerFact]
    public async Task Concurrent_callers_across_two_organizations_do_not_interfere()
    {
        await using var harness = await SqlServerHarness.CreateAsync();

        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var when = new DateTime(2026, 8, 1);

        var work = Enumerable.Range(0, 20).SelectMany(_ => new[]
        {
            Issue(orgA), Issue(orgB)
        });

        await Task.WhenAll(work);

        await using var check = harness.NewContext(orgA);
        var counters = await check.DocumentNumberSequences.IgnoreQueryFilters().ToListAsync();

        counters.Should().HaveCount(2, "one counter per organization");
        counters.Should().OnlyContain(c => c.NextValue == 21);

        async Task Issue(Guid organizationId)
        {
            await using var db = harness.NewContext(organizationId);
            await Generator(db, organizationId).NextAsync(DocumentNumberPrefix.Delivery, when);
        }
    }
}

/// <summary>
/// A throwaway SQL Server database, migrated and dropped per test. Uses LocalDB by default;
/// override with <c>SMS_TEST_SQLSERVER</c>.
/// </summary>
internal sealed class SqlServerHarness : IAsyncDisposable
{
    private readonly string _database;
    private readonly string _connectionString;

    private SqlServerHarness(string database, string connectionString)
    {
        _database         = database;
        _connectionString = connectionString;
    }

    internal static string MasterConnectionString =>
        Environment.GetEnvironmentVariable("SMS_TEST_SQLSERVER")
        ?? "Server=(localdb)\\mssqllocaldb;Database=master;Trusted_Connection=True;";

    internal static bool IsAvailable
    {
        get
        {
            try
            {
                using var conn = new SqlConnection(MasterConnectionString);
                conn.Open();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static async Task<SqlServerHarness> CreateAsync()
    {
        var database = $"SMS_LogTest_{Guid.NewGuid():N}";
        var builder  = new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = "master" };

        await using (var conn = new SqlConnection(builder.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE [{database}]";
            await cmd.ExecuteNonQueryAsync();
        }

        builder.InitialCatalog = database;
        var harness = new SqlServerHarness(database, builder.ConnectionString);

        await using var db = harness.NewContext(Guid.NewGuid());
        await db.Database.MigrateAsync();

        return harness;
    }

    internal LogisticsDbContext NewContext(Guid organizationId)
    {
        var options = new DbContextOptionsBuilder<LogisticsDbContext>()
            .UseSqlServer(_connectionString)
            .Options;

        return new LogisticsDbContext(options, new StaticTenantContext { OrganizationId = organizationId });
    }

    public async ValueTask DisposeAsync()
    {
        var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]";
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// A fact that skips itself when no SQL Server is reachable, so a machine or CI runner without
/// one still gets a green suite — while the test stays real everywhere a database exists.
/// </summary>
internal sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!SqlServerHarness.IsAvailable)
            Skip = "No SQL Server reachable. Set SMS_TEST_SQLSERVER to run the concurrency tests.";
    }
}
