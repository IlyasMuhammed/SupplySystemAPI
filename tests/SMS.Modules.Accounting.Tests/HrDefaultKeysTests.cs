using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Posting;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Accounting.Tests;

/// <summary>A49 G-4 / task 069 — the 16 HR account roles: only for an organization with HRM on, never required for setup Complete, valid on a posting line.</summary>
public sealed class HrDefaultKeysTests
{
    private sealed class Gate(params string[] on) : IModuleGate
    {
        public Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default) => Task.FromResult(on.Contains(featureCode));
    }

    [Fact]
    public void Sixteen_hr_roles_of_valid_types_separate_from_the_sixteen_of_a42()
    {
        DefaultKeys.HrDefinitions.Should().HaveCount(16).And.OnlyContain(d => !d.IsRequired && d.Category == DefaultCategories.Payroll && AccountTypes.IsValid(d.ExpectedAccountType));
        DefaultKeys.All.Should().HaveCount(16);
        DefaultKeys.AllWithHr.Should().HaveCount(32).And.OnlyHaveUniqueItems();
        DefaultKeys.ByKey.Should().ContainKey(DefaultKeys.SalaryPayable);
        DefaultKeys.HrDefinitions.Should().OnlyContain(d => d.Key.Length <= 50);
    }

    [Fact]
    public async Task No_gate_or_hrm_off_keeps_the_sixteen_and_an_hr_key_is_unknown_to_that_organization()
    {
        using var h = new AccountingHarness();
        await new AccountingInitializer(h.Db, new Gate()).EnsureAsync(h.Org);
        (await h.Db.AccountingDefaults.IgnoreQueryFilters().CountAsync(d => d.OrganizationId == h.Org)).Should().Be(16);

        var assign = () => h.Defaults().AssignAsync(h.Admin(), DefaultKeys.SalaryExpense, Guid.NewGuid());
        await assign.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Hrm_on_adds_them_unassigned_and_not_required()
    {
        using var h = new AccountingHarness();
        await new AccountingInitializer(h.Db, new Gate(ModuleCodes.Hrm)).EnsureAsync(h.Org);

        var rows = await h.Db.AccountingDefaults.IgnoreQueryFilters().Where(d => d.OrganizationId == h.Org).ToListAsync();
        rows.Should().HaveCount(32);
        var hr = rows.Where(r => r.Category == DefaultCategories.Payroll).ToList();
        hr.Should().HaveCount(16).And.OnlyContain(r => r.AccountId == null && !r.IsRequired);

        // Idempotent.
        await new AccountingInitializer(h.Db, new Gate(ModuleCodes.Hrm)).EnsureAsync(h.Org);
        (await h.Db.AccountingDefaults.IgnoreQueryFilters().CountAsync(d => d.OrganizationId == h.Org)).Should().Be(32);
    }

    [Fact]
    public void A_posting_line_may_name_an_hr_role()
    {
        var tx = new FinancialTransaction
        {
            DocumentType = FinDocType.PayrollRun, SourceDocumentId = Guid.NewGuid(), SourceModule = PostingSourceModules.Hrm, SourceDocumentNumber = "PAY-2026-10",
            TransactionDate = new DateTime(2026, 10, 31), CurrencyCode = "PKR",
            Lines =
            [
                new FinancialTransactionLine { LineNumber = 1, AccountRole = DefaultKeys.SalaryExpense, Debit = 100m, CostCenter = "OPS" },
                new FinancialTransactionLine { LineNumber = 2, AccountRole = DefaultKeys.SalaryPayable, Credit = 100m },
            ]
        };
        FinancialPostingGateway.Validate(tx, Guid.NewGuid()).Should().BeEmpty();
        tx.IdempotencyKey.Should().StartWith("hrm:PayrollRun:");
    }
}

/// <summary>The guarded constraint swap on LocalDB only: old constraints replaced, a re-run changes nothing, a missing one comes back, HR rows then fit.</summary>
[Trait("Category", "LocalDb")]
public sealed class LocalDbHrDefaultKeysMigrationTests : IDisposable
{
    private readonly string _dbName = "A49_Keys_" + Guid.NewGuid().ToString("N")[..12];
    private string Conn(string db) => $"Server=(localdb)\\MSSQLLocalDB;Database={db};Trusted_Connection=True;TrustServerCertificate=True";

    private AccountingDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AccountingDbContext>().UseSqlServer(Conn(_dbName)).Options, new StaticTenantContext());

    private async Task ExecAsync(string sql)
    {
        await using var c = new SqlConnection(Conn(_dbName));
        await c.OpenAsync();
        await new SqlCommand(sql, c).ExecuteNonQueryAsync();
    }

    private async Task<string> CheckDefinitionAsync(string name)
    {
        await using var c = new SqlConnection(Conn(_dbName));
        await c.OpenAsync();
        var v = await new SqlCommand($"SELECT definition FROM sys.check_constraints WHERE name = '{name}'", c).ExecuteScalarAsync();
        return v as string ?? "";
    }

    private static string UpSql()
    {
        var mb = new Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        typeof(SMS.Modules.Accounting.Migrations.A49_HrDefaultKeys).GetMethod("Up", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(new SMS.Modules.Accounting.Migrations.A49_HrDefaultKeys(), [mb]);
        return string.Join("\n;\n", mb.Operations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>().Select(o => o.Sql));
    }

    [Fact]
    public async Task Up_replaces_the_old_constraints_reruns_safely_and_repairs_a_missing_one()
    {
        await using (var master = new SqlConnection(Conn("master")))
        {
            await master.OpenAsync();
            await new SqlCommand($"CREATE DATABASE [{_dbName}]", master).ExecuteNonQueryAsync();
        }
        await ExecAsync("CREATE SCHEMA finance");
        await ExecAsync("""
            CREATE TABLE finance.AccountingDefaults (Id int IDENTITY PRIMARY KEY, DefaultKey nvarchar(50) NOT NULL, Category nvarchar(50) NOT NULL,
              CONSTRAINT CK_AccountingDefaults_Key CHECK (DefaultKey IN ('DEFAULT_RECEIVABLE')),
              CONSTRAINT CK_AccountingDefaults_Category CHECK (Category IN ('Receivables')));
            """);

        foreach (var sql in UpSql().Split("\n;\n")) await ExecAsync(sql);
        (await CheckDefinitionAsync("CK_AccountingDefaults_Key")).Should().Contain("DEFAULT_SALARY_EXPENSE");
        (await CheckDefinitionAsync("CK_AccountingDefaults_Category")).Should().Contain("Payroll");

        foreach (var sql in UpSql().Split("\n;\n")) await ExecAsync(sql);
        await ExecAsync("ALTER TABLE finance.AccountingDefaults DROP CONSTRAINT CK_AccountingDefaults_Key");
        foreach (var sql in UpSql().Split("\n;\n")) await ExecAsync(sql);
        (await CheckDefinitionAsync("CK_AccountingDefaults_Key")).Should().Contain("DEFAULT_EMPLOYEE_ADVANCE");

        await ExecAsync("INSERT INTO finance.AccountingDefaults (DefaultKey, Category) VALUES ('DEFAULT_SALARY_EXPENSE', 'Payroll')");
        var bad = () => ExecAsync("INSERT INTO finance.AccountingDefaults (DefaultKey, Category) VALUES ('NOPE', 'Payroll')");
        await bad.Should().ThrowAsync<SqlException>();
    }

    [Fact]
    public void Snapshot_agrees_with_the_model()
    {
        using var db = NewDb();
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }

    public void Dispose()
    {
        SqlConnection.ClearAllPools();
        using var master = new SqlConnection(Conn("master"));
        master.Open();
        new SqlCommand($"IF DB_ID('{_dbName}') IS NOT NULL BEGIN ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_dbName}]; END", master).ExecuteNonQuery();
    }
}
