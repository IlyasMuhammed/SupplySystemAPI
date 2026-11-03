using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Modules.Hrm.Services;
using SMS.Shared.Common;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Tests;

/// <summary>
/// Tasks 072–073 on a throwaway LocalDB database: A49_HrmInitial up, down, up again, re-run with the history row lost (the drifted
/// shared database), and what only a real database enforces: filtered unique indexes, CHECKs, the atomic employee number.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class A49MigrationTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A49Mig_{Guid.NewGuid():N};";
    private readonly Guid _org = Guid.NewGuid();

    private HrmDbContext NewContext(bool retrying = false) => new(
        new DbContextOptionsBuilder<HrmDbContext>().UseSqlServer(_connection, sql => { if (retrying) sql.EnableRetryOnFailure(3); }).Options,
        new StaticTenantContext { OrganizationId = _org });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var conn = new SqlConnection(_connection);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new SqlConnection(_connection);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task<int> TablesAsync() => CountAsync("SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('hrm')");

    [Fact]
    public void The_model_snapshot_matches_the_model_so_startup_Migrate_has_nothing_pending()
    {
        using var db = NewContext();
        db.Database.HasPendingModelChanges().Should().BeFalse("EF 9 refuses Migrate() on pending model changes");
    }

    [Fact]
    public async Task Up_down_up_and_a_rerun_with_the_history_lost_all_work()
    {
        await using (var db = NewContext()) await db.Database.MigrateAsync();
        var indexes = await CountAsync("SELECT COUNT(*) FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id WHERE t.schema_id = SCHEMA_ID('hrm') AND i.type > 0");
        (await TablesAsync()).Should().Be(58);
        indexes.Should().BeGreaterThan(200);
        (await CountAsync("SELECT COUNT(*) FROM sys.foreign_keys fk JOIN sys.tables p ON p.object_id = fk.referenced_object_id WHERE fk.schema_id = SCHEMA_ID('hrm') AND p.schema_id <> SCHEMA_ID('hrm')"))
            .Should().Be(0, "task 073: no cross-schema FK");

        await using (var db = NewContext()) await db.GetService<IMigrator>().MigrateAsync("0");
        (await TablesAsync()).Should().Be(0);

        await using (var db = NewContext()) await db.Database.MigrateAsync();
        (await TablesAsync()).Should().Be(58);

        // The drifted database: objects there, history row gone. A re-run must change nothing and fail on nothing.
        await ExecAsync("DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE '%A49_HrmInitial'");
        await ExecAsync("DROP INDEX [UQ_Emp_OrgNumber] ON [hrm].[Employees]");
        await using (var db = NewContext()) await db.Database.MigrateAsync();
        (await TablesAsync()).Should().Be(58);
        (await CountAsync("SELECT COUNT(*) FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id WHERE t.schema_id = SCHEMA_ID('hrm') AND i.type > 0")).Should().Be(indexes, "a missing index comes back");
    }

    [Fact]
    public async Task The_real_database_enforces_what_the_service_also_checks()
    {
        await using (var db = NewContext()) await db.Database.MigrateAsync();
        await using var ctx = NewContext();

        var dept = new Department { OrganizationId = _org, Code = "D", Name = "D" };
        var desig = new Designation { OrganizationId = _org, Code = "X", Name = "X" };
        var branch = new Branch { OrganizationId = _org, Code = "B", Name = "B" };
        var e = new Employee { OrganizationId = _org, EmployeeNumber = "EMP-1", FirstName = "A", LastName = "B", CnicHash = "H1", UserId = 5 };
        ctx.AddRange(dept, desig, branch, e);
        await ctx.SaveChangesAsync();

        Func<Task> Save(object row)
        {
            return async () =>
            {
                await using var c = NewContext();
                c.Add(row);
                await c.SaveChangesAsync();
            };
        }

        // UQ_Emp_OrgNumber, UQ_Emp_OrgCnicHash, IX_Emp_UserId
        await Save(new Employee { OrganizationId = _org, EmployeeNumber = "EMP-1", FirstName = "A", LastName = "B" }).Should().ThrowAsync<DbUpdateException>();
        await Save(new Employee { OrganizationId = _org, EmployeeNumber = "EMP-2", FirstName = "A", LastName = "B", CnicHash = "H1" }).Should().ThrowAsync<DbUpdateException>();
        await Save(new Employee { OrganizationId = _org, EmployeeNumber = "EMP-3", FirstName = "A", LastName = "B", UserId = 5 }).Should().ThrowAsync<DbUpdateException>();
        // The same hash and user in another organization are fine; CK_Employees_Status refuses an unknown status.
        await Save(new Employee { OrganizationId = Guid.NewGuid(), EmployeeNumber = "EMP-1", FirstName = "A", LastName = "B", CnicHash = "H1", UserId = 5 }).Should().NotThrowAsync();
        await Save(new Employee { OrganizationId = _org, EmployeeNumber = "EMP-4", FirstName = "A", LastName = "B", Status = "Nope" }).Should().ThrowAsync<DbUpdateException>();

        // UQ_Employment_OneOpen, UQ_Employment_EmployeeFrom
        EmploymentRecord Rec(DateOnly from, DateOnly? to = null) => new()
        {
            OrganizationId = _org, EmployeeId = e.Id, EffectiveFrom = from, EffectiveTo = to, DepartmentId = dept.Id, DesignationId = desig.Id, BranchId = branch.Id
        };
        await Save(Rec(new DateOnly(2026, 1, 1))).Should().NotThrowAsync();
        await Save(Rec(new DateOnly(2026, 2, 1))).Should().ThrowAsync<DbUpdateException>("one open record per employee");
        await Save(Rec(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 15))).Should().ThrowAsync<DbUpdateException>("unique start date");
        await Save(Rec(new DateOnly(2025, 1, 2), new DateOnly(2025, 1, 1))).Should().ThrowAsync<DbUpdateException>("CK_Employment_Dates");

        // UQ_BankAccount_OnePrimary
        EmployeeBankAccount Bank(bool primary, bool active = true) => new() { OrganizationId = _org, EmployeeId = e.Id, BankName = "HBL", IsPrimary = primary, IsActive = active };
        await Save(Bank(true)).Should().NotThrowAsync();
        await Save(Bank(true)).Should().ThrowAsync<DbUpdateException>("one active primary");
        await Save(Bank(true, active: false)).Should().NotThrowAsync("an inactive one does not count");

        // UQ_PayrollRun_PeriodGroup is filtered on status.
        PayrollRun Run(string number, string status) => new()
        {
            OrganizationId = _org, RunNumber = number, PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2026, 1, 31), PayDate = new DateOnly(2026, 2, 1),
            CurrencyCode = "PKR", Status = status
        };
        await Save(Run("R1", PayrollRunStatuses.Cancelled)).Should().NotThrowAsync();
        await Save(Run("R2", PayrollRunStatuses.Reversed)).Should().NotThrowAsync();
        await Save(Run("R3", PayrollRunStatuses.Draft)).Should().NotThrowAsync();
        await Save(Run("R4", PayrollRunStatuses.Calculated)).Should().ThrowAsync<DbUpdateException>("one live run per period and group");
    }

    private (EmployeeService, HrmDbContext) Service()
    {
        var db = NewContext(retrying: true);
        var settings = Options.Create(new AppSettings { AesEncryptionKey = "k" });
        var seeder = new HrmSeeder(db);
        var scope = new HrmScopeService(db);
        return (new EmployeeService(db, scope, new HrmSensitiveData(new AesEncryptionService(settings), settings), seeder, new EmploymentRecordService(db, scope)), db);
    }

    private HrmCaller Caller => new(_org, 1, P.HrmCodes, "T");

    [Fact]
    public async Task Concurrent_hires_get_distinct_numbers_and_the_counter_survives_under_a_retrying_context()
    {
        await using (var db = NewContext()) await db.Database.MigrateAsync();
        var created = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            var (svc, db) = Service();
            await using (db) return await svc.CreateAsync(Caller, new EmployeeUpsert { FirstName = "E" + i, LastName = "L" });
        }));
        created.Select(e => e.EmployeeNumber).Should().OnlyHaveUniqueItems();
        created.Select(e => e.EmployeeNumber).Should().BeEquivalentTo(Enumerable.Range(1, 8).Select(n => $"EMP-{n:D4}"));
        (await CountAsync($"SELECT NextEmployeeNumber FROM hrm.HrmSettings WHERE OrganizationId = '{_org}'")).Should().Be(9);
    }

    [Fact]
    public async Task Create_with_a_hire_record_and_the_transaction_work_under_a_retrying_context()
    {
        await using (var db = NewContext()) await db.Database.MigrateAsync();
        await using var ctx = NewContext(retrying: true);
        var settings = Options.Create(new AppSettings { AesEncryptionKey = "k" });
        var scope = new HrmScopeService(ctx);
        var records = new EmploymentRecordService(ctx, scope);
        var seeder = new HrmSeeder(ctx);
        var employees = new EmployeeService(ctx, scope, new HrmSensitiveData(new AesEncryptionService(settings), settings), seeder, records);
        var departments = new DepartmentService(ctx);
        var dept = await departments.CreateAsync(Caller, new DepartmentUpsert { Code = "D", Name = "D" });
        var desig = await new DesignationService(ctx).CreateAsync(Caller, new DesignationUpsert { Code = "X", Name = "X" });
        var branch = await new BranchService(ctx).CreateAsync(Caller, new BranchUpsert { Code = "B", Name = "B" });

        var e = await employees.CreateAsync(Caller, new EmployeeUpsert
        {
            FirstName = "A", LastName = "B", Cnic = "42101-1234567-3",
            Employment = new EmploymentRecordCreate { EffectiveFrom = new DateOnly(2026, 1, 1), DepartmentUuid = dept.Uuid, DesignationUuid = desig.Uuid, BranchUuid = branch.Uuid }
        });
        e.CurrentEmployment!.ChangeReason.Should().Be("Hire");

        var t = await records.TransferAsync(Caller, e.Uuid, new EmploymentTransferRequest { EffectiveFrom = new DateOnly(2026, 3, 1), BranchUuid = branch.Uuid });
        t.ChangeReason.Should().Be("Transfer");
        (await CountAsync("SELECT COUNT(*) FROM hrm.EmploymentRecords WHERE EffectiveTo IS NULL")).Should().Be(1);
    }

    [Fact]
    public async Task The_scope_list_query_translates_on_sql_server()
    {
        await using (var db = NewContext()) await db.Database.MigrateAsync();
        var (svc, ctx) = Service();
        await using (ctx)
        {
            var uuid = Guid.NewGuid();
            var caller = new HrmCaller(_org, 2, [P.HRM_EMPLOYEE_VIEW, P.HRM_SCOPE_DEPARTMENT], "T");
            var page = await svc.ListAsync(caller, new EmployeeListFilter { Search = "x", DepartmentUuid = uuid, BranchUuid = uuid, DesignationUuid = uuid, ManagerUuid = uuid });
            page.TotalRecords.Should().Be(0);
        }
    }

    [Fact]
    public async Task Seeding_works_on_sql_server_and_is_safe_under_concurrent_first_requests()
    {
        await using (var db = NewContext()) await db.Database.MigrateAsync();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var c = NewContext(retrying: true);
            await new HrmSeeder(c).EnsureAsync(_org);
        }));
        (await CountAsync("SELECT COUNT(*) FROM hrm.HrmSettings")).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM hrm.LeaveTypes")).Should().Be(6);
        (await CountAsync("SELECT COUNT(*) FROM hrm.SalaryStructures")).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM hrm.TaxSlabTables WHERE IsVerified = 1")).Should().Be(0);
    }
}
