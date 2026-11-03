using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Services;
using SMS.Modules.Hrm.Services.Post;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Tests;

/// <summary>A49 POST: FD-9 enablement guard and wizard (E-1, E-2, E-6), readiness (G-3), the journal export (G-19), the Odoo import (E-14).</summary>
public class PostTests
{
    private sealed class Licences(bool licensed) : IModuleLicenceReader
    {
        public Task<bool> IsLicensedAsync(Guid organizationId, string moduleCode, CancellationToken ct = default) => Task.FromResult(licensed);
    }

    private sealed class Gate(bool on) : IModuleGate
    {
        public Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default) => Task.FromResult(on);
    }

    private static HrmSetupService Setup(HrmHarness h, bool licensed = true) =>
        new(h.Db, h.CostCenters, h.Settings, h.Seeder, new Licences(licensed), new Gate(false));

    // ── E-2: the guard ───────────────────────────────────────────────────────

    [Fact]
    public async Task E6_guard_refuses_with_422_until_a_payroll_cost_center_is_set_and_seeds_nothing()
    {
        var h = new HrmHarness();
        var guard = new HrmModuleEnablementGuard(h.Db);
        guard.ModuleCode.Should().Be(ModuleCodes.Hrm);

        var refused = await guard.Invoking(g => g.CheckAsync(h.Org)).Should().ThrowAsync<UnprocessableEntityException>();
        refused.Which.Message.Should().StartWith("PAYROLL_COST_CENTER_REQUIRED: ");
        (await h.Db.Settings.IgnoreQueryFilters().CountAsync()).Should().Be(0);

        await Setup(h).SetPayrollCostCenterAsync(h.Admin(), new HrmPayrollCostCenterRequest { NewCostCenter = new CostCenterUpsert { Code = "SAL", Name = "Salaries" } });

        await guard.Invoking(g => g.CheckAsync(h.Org)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task E6_another_organizations_cost_center_never_satisfies_the_guard()
    {
        var a = new HrmHarness();
        var b = new HrmHarness(dbName: a.DbName);
        await Setup(a).SetPayrollCostCenterAsync(a.Admin(), new HrmPayrollCostCenterRequest { NewCostCenter = new CostCenterUpsert { Code = "SAL", Name = "Salaries" } });

        await new HrmModuleEnablementGuard(b.Db).Invoking(g => g.CheckAsync(b.Org)).Should().ThrowAsync<UnprocessableEntityException>();
    }

    // ── E-1: the wizard ──────────────────────────────────────────────────────

    [Fact]
    public async Task E1_an_unlicensed_organization_is_told_so_and_gets_no_hr_data()
    {
        var h = new HrmHarness();
        (await Setup(h, licensed: false).GetStatusAsync(h.Admin())).Licensed.Should().BeFalse();
        (await h.Db.Settings.IgnoreQueryFilters().CountAsync()).Should().Be(0);

        await Setup(h, licensed: false).Invoking(s => s.SetPayrollCostCenterAsync(h.Admin(), new HrmPayrollCostCenterRequest { NewCostCenter = new CostCenterUpsert { Code = "X", Name = "X" } }))
            .Should().ThrowAsync<ForbiddenException>();
        (await h.Db.CostCenters.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task E1_status_shows_the_choice_and_an_empty_list_means_create_new()
    {
        var h = new HrmHarness();
        var status = await Setup(h).GetStatusAsync(h.Admin());
        (status.Licensed, status.Enabled, status.PayrollCostCenter, status.Mode).Should().Be((true, false, null, PayrollCostCenterModes.Central));
        status.CostCenters.Should().BeEmpty();
    }

    [Fact]
    public async Task E6_inline_creation_attaches_a_normal_cost_center_and_needs_the_structure_permission()
    {
        var h = new HrmHarness();
        var only = h.As(1, P.HRM_SETTINGS_MANAGE);
        var request = new HrmPayrollCostCenterRequest { NewCostCenter = new CostCenterUpsert { Code = "sal", Name = "Salaries" }, Mode = PayrollCostCenterModes.ByDepartment };

        await Setup(h).Invoking(s => s.SetPayrollCostCenterAsync(only, request)).Should().ThrowAsync<ForbiddenException>();

        var settings = await Setup(h).SetPayrollCostCenterAsync(h.As(1, P.HRM_SETTINGS_MANAGE, P.HRM_ORG_MANAGE), request);
        settings.PayrollCostCenterCode.Should().Be("SAL");
        settings.PayrollCostCenterMode.Should().Be(PayrollCostCenterModes.ByDepartment);
        settings.EnablementCompletedAt.Should().NotBeNull();

        var status = await Setup(h).GetStatusAsync(h.Admin());
        status.PayrollCostCenter!.Code.Should().Be("SAL");
        status.CostCenters.Should().ContainSingle(c => c.IsPayrollCostCenter);
    }

    [Fact]
    public async Task E6_selecting_needs_only_the_settings_permission_and_validates_the_row()
    {
        var a = new HrmHarness();
        var b = new HrmHarness(dbName: a.DbName);
        var center = await a.CostCenters.CreateAsync(a.Admin(), new CostCenterUpsert { Code = "HO", Name = "Head Office" });
        var foreign = await b.CostCenters.CreateAsync(b.Admin(), new CostCenterUpsert { Code = "B", Name = "B" });
        var settingsOnly = a.As(1, P.HRM_SETTINGS_MANAGE);

        await Setup(a).Invoking(s => s.SetPayrollCostCenterAsync(settingsOnly, new HrmPayrollCostCenterRequest { CostCenterUuid = foreign.Uuid }))
            .Should().ThrowAsync<NotFoundException>("another organization's row is 404");
        (await Setup(a).SetPayrollCostCenterAsync(settingsOnly, new HrmPayrollCostCenterRequest { CostCenterUuid = center.Uuid })).PayrollCostCenterCode.Should().Be("HO");

        await a.CostCenters.Invoking(c => c.SetActiveAsync(a.Admin(), center.Uuid, false)).Should().ThrowAsync<ConflictException>("§11A.2");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task E1_it_is_one_or_the_other(bool select, bool create)
    {
        var h = new HrmHarness();
        var request = new HrmPayrollCostCenterRequest
        {
            CostCenterUuid = select ? Guid.NewGuid() : null,
            NewCostCenter = create ? new CostCenterUpsert { Code = "X", Name = "X" } : null
        };
        (await Setup(h).Invoking(s => s.SetPayrollCostCenterAsync(h.Admin(), request)).Should().ThrowAsync<BadRequestException>())
            .Which.Message.Should().StartWith("COST_CENTER_CHOICE");
    }

    // ── Seams ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task G5_cost_center_source_is_per_organization_and_marks_the_payroll_one()
    {
        var a = new HrmHarness();
        var b = new HrmHarness(dbName: a.DbName);
        await Setup(a).SetPayrollCostCenterAsync(a.Admin(), new HrmPayrollCostCenterRequest { NewCostCenter = new CostCenterUpsert { Code = "SAL", Name = "Salaries" } });
        await a.CostCenters.CreateAsync(a.Admin(), new CostCenterUpsert { Code = "OPS", Name = "Ops" });
        await b.CostCenters.CreateAsync(b.Admin(), new CostCenterUpsert { Code = "OTHER", Name = "Other" });

        var list = await new HrmCostCenterSource(a.Db).ListAsync(a.Org);

        list.Select(c => (c.Code, c.IsPayrollCostCenter)).Should().Equal(("OPS", false), ("SAL", true));
    }

    [Fact]
    public async Task G11_identifiers_are_this_organizations_names_and_numbers()
    {
        var a = new HrmHarness();
        var b = new HrmHarness(dbName: a.DbName);
        var at = await a.BaseAsync();
        var bt = await b.BaseAsync();
        var ali = await a.EmployeeAsync(at, "Ali", "Khan");
        await b.EmployeeAsync(bt, "Sara", "Ahmed");

        (await new HrmPersonalIdentifierSource(a.Db).GetAsync(a.Org)).Should().BeEquivalentTo(["Ali Khan", ali.EmployeeNumber]);
    }

    // ── G-3 ──────────────────────────────────────────────────────────────────

    private sealed class FakeRoles(params string[] missing) : IPostingRoleReadiness
    {
        public Task<IReadOnlyList<string>> MissingRolesAsync(IReadOnlyCollection<string> roles, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(roles.Intersect(missing).ToList());
    }

    private sealed class FakeQuickBooks(QuickBooksReadinessResult result) : IQuickBooksPostingReadiness
    {
        public QuickBooksPostingNeeds? Asked { get; private set; }
        public Task<QuickBooksReadinessResult> CheckAsync(QuickBooksPostingNeeds needs, CancellationToken ct = default)
        {
            Asked = needs;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeTarget(string code) : IFinancialPostingTarget
    {
        public string TargetCode => code;
        public Task<bool> IsActiveAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<TargetResult> PostAsync(FinancialTransaction transaction, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TargetResult> VoidAsync(VoidRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TargetResult> ReverseAsync(VoidRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeBuilder(FinancialTransaction tx) : IPayrollPostingBuilder
    {
        public DateOnly? SawPostingDate { get; private set; }
        public Task<FinancialTransaction> BuildAsync(PayrollRun run, CancellationToken ct = default)
        {
            SawPostingDate = run.PostingDate;
            return Task.FromResult(tx);
        }
        public Task<FinancialTransaction> BuildPaymentAsync(PayrollRun run, DateOnly paidOn, CancellationToken ct = default) => Task.FromResult(tx);
    }

    private static readonly FinancialTransaction Journal = new()
    {
        DocumentType = FinDocType.PayrollRun, SourceDocumentId = Guid.NewGuid(), SourceDocumentNumber = "PAY-1", SourceModule = PostingSourceModules.Hrm, CurrencyCode = "PKR",
        TransactionDate = new DateTime(2026, 10, 31),
        Lines =
        [
            new() { LineNumber = 1, AccountRole = "DEFAULT_SALARY_EXPENSE", Debit = 100m, CostCenter = "OPS", Label = "Operations" },
            new() { LineNumber = 2, AccountRole = "DEFAULT_SALARY_PAYABLE", Credit = 100m, Label = "=cmd,x" }
        ]
    };

    private static PayrollPostingReadinessService Readiness(HrmHarness h, IPostingRoleReadiness? roles = null, IQuickBooksPostingReadiness? qb = null, params string[] targets) =>
        new(h.Db, new FakeBuilder(Journal), roles ?? new NullPostingRoleReadiness(), qb ?? new NullQuickBooksPostingReadiness(),
            targets.Select(t => new FakeTarget(t)), new HrmCostCenterSource(h.Db));

    [Fact]
    public async Task G19_no_target_is_ready_and_says_so()
    {
        var h = new HrmHarness();
        var r = await Readiness(h).CheckTransactionAsync(h.Org, Journal);
        (r.IsReady, r.NoTarget, r.InternalGlActive, r.QuickBooksActive).Should().Be((true, true, false, false));
    }

    [Fact]
    public async Task G3_001_an_unmapped_gl_role_blocks()
    {
        var h = new HrmHarness();
        var r = await Readiness(h, new FakeRoles("DEFAULT_SALARY_PAYABLE"), targets: PostingTargetCodes.InternalGl).CheckTransactionAsync(h.Org, Journal);
        (r.IsReady, r.NoTarget, r.InternalGlReady).Should().Be((false, false, false));
        r.MissingGlRoles.Should().Equal("DEFAULT_SALARY_PAYABLE");
    }

    [Fact]
    public async Task G3_002_quickbooks_gaps_block_while_it_sends_now()
    {
        var h = new HrmHarness();
        await Setup(h).SetPayrollCostCenterAsync(h.Admin(), new HrmPayrollCostCenterRequest { NewCostCenter = new CostCenterUpsert { Code = "SAL", Name = "Salaries" } });
        var fake = new FakeQuickBooks(new QuickBooksReadinessResult
        {
            Connected = true, SendsNow = true,
            MissingAccounts = [new QuickBooksAccountNeed("DEFAULT_SALARY_EXPENSE", null)],
            MissingCostCenters = [new QuickBooksCostCenterGap("OPS", "Operations")],
            CurrencyMismatch = new QuickBooksCurrencyMismatch("PKR", "USD")
        });

        var r = await Readiness(h, qb: fake, targets: PostingTargetCodes.QuickBooks).CheckTransactionAsync(h.Org, Journal);

        (r.IsReady, r.QuickBooksReady, r.QuickBooksActive).Should().Be((false, false, true));
        r.MissingQboAccounts.Should().Equal(new MissingQboAccount("DEFAULT_SALARY_EXPENSE", null));
        r.MissingCostCenters.Should().Equal(new MissingCostCenter("OPS", "Operations"));
        r.CurrencyMismatch.Should().Be(new CurrencyMismatch("PKR", "USD"));
        fake.Asked!.CostCenterCodes.Should().BeEquivalentTo(["OPS", "SAL"], "a line with no cost center posts on the payroll cost center (BR-G5-003), so its Class is needed too");
        fake.Asked.DocumentType.Should().Be("PayrollRun");
    }

    [Fact]
    public async Task G3_held_quickbooks_shows_the_gaps_but_does_not_block_finalize()
    {
        var h = new HrmHarness();
        var fake = new FakeQuickBooks(new QuickBooksReadinessResult
        {
            Connected = true, SendsNow = false, MissingCostCenters = [new QuickBooksCostCenterGap("OPS", "Operations")]
        });

        var r = await Readiness(h, qb: fake, targets: PostingTargetCodes.QuickBooks).CheckTransactionAsync(h.Org, Journal);

        (r.IsReady, r.QuickBooksReady).Should().Be((true, true));
        r.MissingCostCenters.Should().ContainSingle();
    }

    [Fact]
    public async Task G3_003_all_mapped_is_ready_and_the_run_is_read_without_tracking()
    {
        var h = new HrmHarness();
        var run = new PayrollRun { OrganizationId = h.Org, RunNumber = "PAY-1", PeriodStart = new DateOnly(2026, 10, 1), PeriodEnd = new DateOnly(2026, 10, 31), PayDate = new DateOnly(2026, 11, 1), CurrencyCode = "PKR" };
        h.Db.PayrollRuns.Add(run);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        var builder = new FakeBuilder(Journal);
        var service = new PayrollPostingReadinessService(h.Db, builder, new FakeRoles(), new NullQuickBooksPostingReadiness(),
            [new FakeTarget(PostingTargetCodes.InternalGl)], new HrmCostCenterSource(h.Db));

        var r = await service.CheckAsync(h.Org, run.Id);

        (r.IsReady, r.InternalGlActive).Should().Be((true, true));
        builder.SawPostingDate.Should().Be(new DateOnly(2026, 11, 1));
        (await h.Db.PayrollRuns.IgnoreQueryFilters().SingleAsync()).PostingDate.Should().BeNull("asking changes nothing");

        var other = new HrmHarness(dbName: h.DbName);
        await new PayrollPostingReadinessService(other.Db, builder, new FakeRoles(), new NullQuickBooksPostingReadiness(), [], new HrmCostCenterSource(other.Db))
            .Invoking(s => s.CheckAsync(other.Org, run.Id)).Should().ThrowAsync<NotFoundException>();
    }

    // ── G-19 export ──────────────────────────────────────────────────────────

    [Fact]
    public void G19_002_csv_has_the_contract_columns_and_defuses_formulas()
    {
        var text = System.Text.Encoding.UTF8.GetString(new PayrollJournalExportService().ToCsv(Journal)).TrimStart('﻿');
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        lines[0].Should().Be("Account role,Account code,Cost center,Debit,Credit,Description");
        lines[1].Should().Be("DEFAULT_SALARY_EXPENSE,,OPS,100.00,0.00,Operations");
        lines[2].Should().Be("DEFAULT_SALARY_PAYABLE,,,0.00,100.00,\"'=cmd,x\"");
    }

    [Fact]
    public void G19_003_xlsx_opens_with_the_lines_and_totals()
    {
        using var stream = new MemoryStream(new PayrollJournalExportService().ToXlsx(Journal, "PAY-1"));
        using var book = new XLWorkbook(stream);
        var sheet = book.Worksheet("Journal");
        sheet.Cell(4, 1).GetString().Should().Be("Account role");
        sheet.Cell(5, 4).GetDouble().Should().Be(100d);
        sheet.Cell(7, 4).FormulaA1.Should().Be("SUM(D5:D6)");
        sheet.Cell(6, 6).GetString().Should().Be("=cmd,x", "written as text, not as a formula");
        sheet.Cell(6, 6).HasFormula.Should().BeFalse();
    }

    // ── E-14 ─────────────────────────────────────────────────────────────────

    private static HrmEmployeeImportTarget Import(HrmHarness h, bool on = true) =>
        new(h.Db, h.Employees, h.Bank, h.Sensitive, h.Seeder, new Gate(on));

    private static Dictionary<string, string?> Odoo(params (string Key, string? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);

    [Fact]
    public async Task E14_a_row_becomes_an_employee_with_a_hire_record_and_encrypted_details()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var row = Odoo(("name", "Sara Bint Khan"), ("identification_id", "12345-1234567-1"), ("gender", "female"), ("marital", "cohabitant"), ("birthday", "1990-05-01"),
            ("work_email", "sara@example.com"), ("mobile_phone", "0300"), ("department_id", "ops"), ("job_id", "Clerk"), ("work_location_id", "Karachi"),
            ("date_start", "2024-01-15"), ("bank_name", "HBL"), ("bank_account_id", "0123456789"));

        (await Import(h).ValidateAsync(h.Org, [new ImportRowInput(1, row, null)])).Single().Errors.Should().BeEmpty();
        var outcome = (await Import(h).ApplyAsync(h.Org, 5, [new ImportApplyRow(1, row, "CREATE", null)])).Single();
        outcome.Status.Should().Be("IMPORTED");

        var employee = await h.Employees.GetAsync(h.Admin(), outcome.EntityUuid);
        (employee.FirstName, employee.LastName, employee.Gender, employee.MaritalStatus).Should().Be(("Sara", "Bint Khan", "F", "Other"));
        employee.CnicMasked.Should().NotContain("1234567");
        employee.CurrentEmployment!.DepartmentUuid.Should().Be(b.Ops);
        employee.CurrentEmployment.DesignationUuid.Should().Be(b.Clerk);
        (await h.Bank.ListAsync(h.Admin(), outcome.EntityUuid)).Single().IsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task E14_validation_names_what_is_wrong_and_never_repeats_a_cnic()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        await h.EmployeeAsync(b, "Ali", "Khan", cnic: "1234512345671");

        var checks = await Import(h).ValidateAsync(h.Org,
        [
            new ImportRowInput(1, Odoo(("name", "A Khan"), ("identification_id", "12345-1234567-1")), null),
            new ImportRowInput(2, Odoo(("name", "B Khan"), ("identification_id", "11111-1111111-1")), null),
            new ImportRowInput(3, Odoo(("name", "C Khan"), ("identification_id", "1111111111111")), null),
            new ImportRowInput(4, Odoo(("name", "D Khan"), ("identification_id", "12")), null),
            new ImportRowInput(5, Odoo(("name", "E Khan"), ("department_id", "NOPE"), ("gender", "x")), null),
            new ImportRowInput(6, Odoo(("name", "F Khan"), ("bank_account_id", "1")), null)
        ]);

        checks[0].Errors.Single().Code.Should().Be("DUPLICATE_IN_DB");
        checks[1].Errors.Should().BeEmpty();
        checks[2].Errors.Single().Code.Should().Be("DUPLICATE_IN_DB", "the second time in one file");
        checks[3].Errors.Single().Code.Should().Be("INVALID_VALUE");
        checks.SelectMany(c => c.Errors).Where(e => e.Field == "identification_id").Should().OnlyContain(e => e.Value == null, "a CNIC is S3");
        checks[4].Errors.Select(e => e.Code).Should().Contain("LOOKUP_NOT_FOUND").And.Contain("INVALID_VALUE").And.Contain("MISSING_DEPENDENCY");
        checks[5].Errors.Single().Field.Should().Be("bank_name");
    }

    [Fact]
    public async Task E14_module_off_refuses_and_writes_nothing()
    {
        var h = new HrmHarness();
        var row = Odoo(("name", "Sara Khan"));
        (await Import(h, on: false).ValidateAsync(h.Org, [new ImportRowInput(1, row, null)])).Single().Errors.Single().Code.Should().Be("MODULE_OFF");
        await Import(h, on: false).Invoking(t => t.ApplyAsync(h.Org, 1, [new ImportApplyRow(1, row, "CREATE", null)])).Should().ThrowAsync<ForbiddenException>();
        (await h.Db.Employees.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task E14_a_one_word_name_is_caught_by_validate_on_its_row()
    {
        var h = new HrmHarness();
        var check = (await Import(h).ValidateAsync(h.Org, [new ImportRowInput(7, Odoo(("name", "Madonna")), null)])).Single();
        check.Errors.Single().Code.Should().Be("NAME_NEEDS_TWO_WORDS");
    }

    [Fact]
    public async Task E14_a_failure_at_run_names_its_row()
    {
        var h = new HrmHarness();
        // Validate is bypassed on purpose: what a rule added later, or a row changed meanwhile, would do.
        var refused = await Import(h).Invoking(t => t.ApplyAsync(h.Org, 1,
        [
            new ImportApplyRow(3, Odoo(("name", new string('x', 70) + " Khan")), "CREATE", null)
        ])).Should().ThrowAsync<BadRequestException>();
        refused.Which.Message.Should().StartWith("Row 3:");
    }

    [Theory]
    [InlineData("parent_id", "EMP-0001")]
    [InlineData("date_end", "2027-01-01")]
    public async Task E14_a_manager_or_contract_end_with_no_hire_record_is_an_error_not_dropped(string field, string value)
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var boss = await h.EmployeeAsync(b, "Boss", "One");
        var row = Odoo(("name", "Sara Khan"), (field, field == "parent_id" ? boss.EmployeeNumber : value));
        (await Import(h).ValidateAsync(h.Org, [new ImportRowInput(1, row, null)])).Single().Errors
            .Should().ContainSingle(e => e.Field == field && e.Code == "MISSING_DEPENDENCY");
    }

    [Fact]
    public void E14_profile_declares_its_sensitive_fields_unambiguous_dates_and_honest_external_id_help()
    {
        var profile = new HrmEmployeeImportTarget(null!, null!, null!, null!, null!).Describe();
        profile.SensitiveFields.Should().BeEquivalentTo(["identification_id", "bank_account_id"]);
        profile.Fields.Where(f => f.FieldType == ImportFieldTypes.Date).Should().OnlyContain(f => f.UnambiguousDateOnly).And.HaveCount(3);
        profile.ExternalIdHelp.Should().Contain("never updates").And.NotContain("updates the record");
    }

    [Fact]
    public void E14_profile_is_create_only_with_aliases()
    {
        var profile = new HrmEmployeeImportTarget(null!, null!, null!, null!, null!).Describe();
        profile.EntityType.Should().Be(ImportEntityTypes.Employee);
        profile.AllowUpdate.Should().BeFalse();
        profile.RequiredPermissions.Should().BeEquivalentTo([P.HRM_EMPLOYEE_EDIT, P.HRM_EMPLOYMENT_MANAGE, P.HRM_EMPLOYEE_SENSITIVE_VIEW, P.HRM_BANK_EDIT]);
        profile.Fields.Should().Contain(f => f.FieldName == "identification_id" && f.Aliases!.Contains("cnic"));
    }
}
