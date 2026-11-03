using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Exceptions;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Tests;

/// <summary>Tasks 045–048, 061, 066: numbers, S3 fields, statuses, the user link, employment and bank rules.</summary>
public sealed class EmployeeTests
{
    private const string Cnic = "42101-1234567-3";

    [Fact]
    public async Task Numbers_are_generated_in_order_per_organization_and_a_refused_form_burns_none()
    {
        var h = new HrmHarness();
        var other = new HrmHarness(dbName: h.DbName);
        var b = await h.BaseAsync();

        (await h.EmployeeAsync(b)).EmployeeNumber.Should().Be("EMP-0001");
        var refused = () => h.Employees.CreateAsync(h.Admin(), new EmployeeUpsert { FirstName = "", LastName = "X" });
        await refused.Should().ThrowAsync<BadRequestException>();
        (await h.EmployeeAsync(b, "Sara")).EmployeeNumber.Should().Be("EMP-0002");

        var ob = await other.BaseAsync();
        (await other.EmployeeAsync(ob)).EmployeeNumber.Should().Be("EMP-0001");
    }

    [Fact]
    public async Task A_number_is_never_reused_after_a_soft_delete()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var first = await h.EmployeeAsync(b);
        var row = await h.Db.Employees.FirstAsync(e => e.Uuid == first.Uuid);
        row.IsDeleted = true;
        await h.Db.SaveChangesAsync();
        (await h.EmployeeAsync(b, "Next")).EmployeeNumber.Should().Be("EMP-0002");
    }

    [Fact]
    public async Task Cnic_is_encrypted_hashed_and_masked_and_never_stored_in_clear()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b, cnic: Cnic);

        e.CnicMasked.Should().Be("*****-*******-3");
        var row = await h.Db.Employees.AsNoTracking().FirstAsync(x => x.Uuid == e.Uuid);
        row.CnicEncrypted.Should().NotBeNullOrEmpty().And.NotContain("4210112345673").And.NotContain("1234567");
        row.CnicEncrypted.Should().StartWith("v2:", "AES-GCM");
        row.CnicHash.Should().HaveLength(64).And.Be(h.Sensitive.Hash(h.Org, "4210112345673"));
        row.CnicLastDigit.Should().Be("3");
    }

    [Fact]
    public async Task Cnic_hash_differs_between_organizations()
    {
        var h = new HrmHarness();
        var other = new HrmHarness();
        h.Sensitive.Hash(h.Org, "4210112345673").Should().NotBe(other.Sensitive.Hash(other.Org, "4210112345673"));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("42101-1234567-X")]
    [InlineData("42101-1234567-33")]
    public async Task A_malformed_cnic_is_refused(string cnic)
    {
        var h = new HrmHarness();
        var bad = () => h.Employees.CreateAsync(h.Admin(), new EmployeeUpsert { FirstName = "A", LastName = "B", Cnic = cnic });
        (await bad.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("EMPLOYEE_CNIC_INVALID:");
    }

    [Fact]
    public async Task A_duplicate_cnic_is_409_regardless_of_dashes_but_fine_in_another_org()
    {
        var h = new HrmHarness();
        var other = new HrmHarness(dbName: h.DbName);
        var b = await h.BaseAsync();
        await h.EmployeeAsync(b, cnic: Cnic);

        var dup = () => h.Employees.CreateAsync(h.Admin(), new EmployeeUpsert { FirstName = "C", LastName = "D", Cnic = "4210112345673" });
        (await dup.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("EMPLOYEE_CNIC_DUPLICATE:");

        (await other.Employees.CreateAsync(other.Admin(), new EmployeeUpsert { FirstName = "C", LastName = "D", Cnic = Cnic })).CnicMasked.Should().NotBeNull();
    }

    [Fact]
    public async Task Exact_cnic_search_finds_by_hash_and_needs_the_sensitive_permission()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b, cnic: Cnic);
        await h.EmployeeAsync(b, "Other", "Person");

        var found = await h.Employees.ListAsync(h.Admin(), new EmployeeListFilter { Cnic = "4210112345673" });
        found.Data.Should().ContainSingle(x => x.Uuid == e.Uuid);

        var noPerm = () => h.Employees.ListAsync(h.As(5, P.HRM_EMPLOYEE_VIEW, P.HRM_SCOPE_ALL), new EmployeeListFilter { Cnic = Cnic });
        await noPerm.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Reveal_returns_the_value_and_writes_one_log_row_per_field()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.Employees.CreateAsync(h.Admin(), new EmployeeUpsert { FirstName = "A", LastName = "B", Cnic = Cnic, Ntn = "1234567-8", Employment = h.Hire(b) });

        var seen = await h.Employees.RevealAsync(h.Admin(), e.Uuid);
        seen.Cnic.Should().Be(Cnic);
        seen.Ntn.Should().Be("1234567-8");

        var logs = await h.Db.SensitiveAccessLogs.AsNoTracking().ToListAsync();
        logs.Select(l => l.Field).Should().BeEquivalentTo([SensitiveFields.Cnic, SensitiveFields.Ntn]);
        logs.Should().OnlyContain(l => l.UserId == 900 && l.OrganizationId == h.Org);
    }

    [Fact]
    public async Task Reveal_without_the_permission_is_403_and_logs_nothing()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b, cnic: Cnic);

        var reveal = () => h.Employees.RevealAsync(h.As(5, P.HRM_EMPLOYEE_VIEW, P.HRM_SCOPE_ALL), e.Uuid);
        await reveal.Should().ThrowAsync<ForbiddenException>();
        (await h.Db.SensitiveAccessLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_platform_super_admin_is_stripped_of_the_sensitive_codes_whatever_the_token_says()
    {
        var h = new HrmHarness();
        var caller = new HrmCaller(h.Org, 1, P.All, "Platform", isSuperAdmin: true);
        caller.Has(P.HRM_EMPLOYEE_SENSITIVE_VIEW).Should().BeFalse();
        caller.Has(P.HRM_PAYROLL_VIEW).Should().BeFalse();
        caller.Has(P.HRM_SCOPE_ALL).Should().BeFalse();
        caller.Has(P.HRM_ORG_VIEW).Should().BeTrue();
    }

    [Fact]
    public async Task Update_with_null_cnic_keeps_it_and_empty_clears_it()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b, cnic: Cnic);

        var kept = await h.Employees.UpdateAsync(h.Admin(), e.Uuid, new EmployeeUpsert { FirstName = "New", LastName = "Name" });
        kept.CnicMasked.Should().NotBeNull();
        kept.FullName.Should().Be("New Name");

        var cleared = await h.Employees.UpdateAsync(h.Admin(), e.Uuid, new EmployeeUpsert { FirstName = "New", LastName = "Name", Cnic = "" });
        cleared.CnicMasked.Should().BeNull();
    }

    [Theory]
    [InlineData(EmployeeStatuses.Active, EmployeeStatuses.OnNotice, true)]
    [InlineData(EmployeeStatuses.Active, EmployeeStatuses.Terminated, true)]
    [InlineData(EmployeeStatuses.OnNotice, EmployeeStatuses.Active, true)]
    [InlineData(EmployeeStatuses.Terminated, EmployeeStatuses.Archived, true)]
    [InlineData(EmployeeStatuses.Archived, EmployeeStatuses.Active, false)]
    [InlineData(EmployeeStatuses.Terminated, EmployeeStatuses.Active, false)]
    [InlineData(EmployeeStatuses.Probation, EmployeeStatuses.Candidate, false)]
    public void Status_machine(string from, string to, bool allowed) => EmployeeStatuses.CanMove(from, to).Should().Be(allowed);

    [Fact]
    public async Task Terminating_needs_a_date_and_a_reason_and_stamps_them()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b);

        var noDate = () => h.Employees.ChangeStatusAsync(h.Admin(), e.Uuid, new EmployeeStatusChange { Status = "Terminated" });
        (await noDate.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("EMPLOYEE_LEAVE_DATE_REQUIRED:");

        var done = await h.Employees.ChangeStatusAsync(h.Admin(), e.Uuid, new EmployeeStatusChange { Status = "Terminated", EffectiveDate = new DateOnly(2026, 6, 30), DepartureReason = "Resignation" });
        done.Status.Should().Be("Terminated");
        done.LeaveDate.Should().Be(new DateOnly(2026, 6, 30));
        done.AllowedStatuses.Should().Equal(EmployeeStatuses.Archived);

        var back = () => h.Employees.ChangeStatusAsync(h.Admin(), e.Uuid, new EmployeeStatusChange { Status = "Active" });
        (await back.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("EMPLOYEE_STATUS_TRANSITION:");
    }

    [Fact]
    public async Task An_archived_employee_cannot_be_edited()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b);
        await h.Employees.ChangeStatusAsync(h.Admin(), e.Uuid, new EmployeeStatusChange { Status = "Terminated", EffectiveDate = new DateOnly(2026, 6, 30), DepartureReason = "Other" });
        await h.Employees.ChangeStatusAsync(h.Admin(), e.Uuid, new EmployeeStatusChange { Status = "Archived" });

        var edit = () => h.Employees.UpdateAsync(h.Admin(), e.Uuid, new EmployeeUpsert { FirstName = "X", LastName = "Y" });
        (await edit.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("EMPLOYEE_ARCHIVED:");
    }

    [Fact]
    public async Task One_user_per_employee_per_organization()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        await h.EmployeeAsync(b, userId: 11);
        var second = () => h.EmployeeAsync(b, "Two", userId: 11);
        (await second.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("EMPLOYEE_USER_TAKEN:");
    }

    [Fact]
    public async Task A_user_not_in_the_organization_cannot_be_linked()
    {
        var h = new HrmHarness();
        var link = () => h.Employees.CreateAsync(h.Admin(), new EmployeeUpsert { FirstName = "A", LastName = "B", UserId = 404 });
        await link.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task List_filters_and_never_carries_cleartext()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        await h.EmployeeAsync(b, "Ali", "Khan", department: b.Ops, cnic: Cnic);
        await h.EmployeeAsync(b, "Sara", "Malik", department: b.Sales);

        var page = await h.Employees.ListAsync(h.Admin(), new EmployeeListFilter { DepartmentUuid = b.Sales });
        page.Data.Should().ContainSingle(x => x.FullName == "Sara Malik" && x.DepartmentName == "Sales");
        (await h.Employees.ListAsync(h.Admin(), new EmployeeListFilter { Search = "Khan" })).Data.Should().ContainSingle();

        var json = System.Text.Json.JsonSerializer.Serialize((await h.Employees.ListAsync(h.Admin(), new EmployeeListFilter())).Data);
        json.Should().NotContain("4210112345673").And.NotContain("42101-1234567-3");
    }

    // ── Employment (task 061) ──

    [Fact]
    public async Task Hire_record_becomes_current_and_sets_the_join_date()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b);
        e.JoinDate.Should().Be(new DateOnly(2026, 1, 1));
        e.CurrentEmployment!.DepartmentName.Should().Be("Operations");
        e.CurrentEmployment.ChangeReason.Should().Be("Hire");
    }

    [Fact]
    public async Task Transfer_closes_the_previous_record_the_day_before_and_carries_the_rest()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b);
        var t = await h.Employment.TransferAsync(h.Admin(), e.Uuid, new EmploymentTransferRequest { EffectiveFrom = new DateOnly(2026, 3, 1), DepartmentUuid = b.Sales });

        t.ChangeReason.Should().Be("Transfer");
        t.DepartmentName.Should().Be("Sales");
        t.DesignationName.Should().Be("Clerk");
        t.BranchName.Should().Be("Karachi");

        var all = await h.Employment.ListAsync(h.Admin(), e.Uuid);
        all.Should().HaveCount(2);
        all[1].EffectiveTo.Should().Be(new DateOnly(2026, 2, 28));
        all[0].EffectiveTo.Should().BeNull();
    }

    [Fact]
    public async Task Promotion_changes_designation_and_an_empty_one_is_refused()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b);
        var empty = () => h.Employment.PromoteAsync(h.Admin(), e.Uuid, new EmploymentPromotionRequest { EffectiveFrom = new DateOnly(2026, 3, 1) });
        (await empty.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("EMPLOYMENT_PROMOTION_EMPTY:");

        var p = await h.Employment.PromoteAsync(h.Admin(), e.Uuid, new EmploymentPromotionRequest { EffectiveFrom = new DateOnly(2026, 3, 1), DesignationUuid = b.Boss });
        p.DesignationName.Should().Be("Manager");
        p.ChangeReason.Should().Be("Promotion");
    }

    [Theory]
    [InlineData(2026, 1, 1)]
    [InlineData(2025, 12, 1)]
    public async Task A_record_must_start_after_the_latest_one(int y, int m, int d)
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b);
        var bad = () => h.Employment.TransferAsync(h.Admin(), e.Uuid, new EmploymentTransferRequest { EffectiveFrom = new DateOnly(y, m, d), DepartmentUuid = b.Sales });
        (await bad.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("EMPLOYMENT_OVERLAP:");
    }

    [Fact]
    public async Task A_manager_loop_is_refused_directly_and_through_a_chain()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var a = await h.EmployeeAsync(b, "A");
        var c = await h.EmployeeAsync(b, "C", manager: a.Uuid);
        var d = await h.EmployeeAsync(b, "D", manager: c.Uuid);

        var self = () => h.Employment.TransferAsync(h.Admin(), a.Uuid, new EmploymentTransferRequest { EffectiveFrom = new DateOnly(2026, 2, 1), ManagerUuid = a.Uuid });
        (await self.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("EMPLOYMENT_MANAGER_CYCLE:");
        var loop = () => h.Employment.TransferAsync(h.Admin(), a.Uuid, new EmploymentTransferRequest { EffectiveFrom = new DateOnly(2026, 2, 1), ManagerUuid = d.Uuid });
        (await loop.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("EMPLOYMENT_MANAGER_CYCLE:");
    }

    [Fact]
    public async Task An_inactive_department_cannot_take_a_new_record()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        await h.Departments.SetActiveAsync(h.Admin(), b.Sales, false);
        var e = await h.EmployeeAsync(b);
        var bad = () => h.Employment.TransferAsync(h.Admin(), e.Uuid, new EmploymentTransferRequest { EffectiveFrom = new DateOnly(2026, 2, 1), DepartmentUuid = b.Sales });
        (await bad.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("EMPLOYMENT_FIELD_INACTIVE:");
    }

    [Fact]
    public async Task Correcting_is_for_the_latest_record_only_and_moves_the_previous_end_date()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b);
        var first = e.CurrentEmployment!;
        var t = await h.Employment.TransferAsync(h.Admin(), e.Uuid, new EmploymentTransferRequest { EffectiveFrom = new DateOnly(2026, 3, 1), DepartmentUuid = b.Sales });

        var old = () => h.Employment.CorrectAsync(h.Admin(), e.Uuid, first.Uuid, h.Hire(b));
        (await old.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("EMPLOYMENT_NOT_LATEST:");

        var fix = h.Hire(b, new DateOnly(2026, 4, 1), department: b.Sales);
        (await h.Employment.CorrectAsync(h.Admin(), e.Uuid, t.Uuid, fix)).EffectiveFrom.Should().Be(new DateOnly(2026, 4, 1));
        (await h.Employment.ListAsync(h.Admin(), e.Uuid))[1].EffectiveTo.Should().Be(new DateOnly(2026, 3, 31));
    }

    [Fact]
    public async Task Cost_splits_must_total_100()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b);
        var other = await h.CostCenters.CreateAsync(h.Admin(), new CostCenterUpsert { Code = "S2", Name = "Second" });

        var req = h.Hire(b, new DateOnly(2026, 2, 1));
        req.CostSplits = [new CostSplitModel { CostCenterUuid = b.CostCenter, Percent = 60 }, new CostSplitModel { CostCenterUuid = other.Uuid, Percent = 30 }];
        var bad = () => h.Employment.CreateAsync(h.Admin(), e.Uuid, req);
        (await bad.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("COST_SPLIT_TOTAL:");
    }

    [Fact]
    public async Task Directory_gives_cost_center_shares_for_each_mode()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var payroll = await h.CostCenters.CreateAsync(h.Admin(), new CostCenterUpsert { Code = "PAY", Name = "Payroll" });
        await h.Settings.UpdateAsync(h.Admin(), new HrmSettingsUpdate { PayrollCostCenterUuid = payroll.Uuid });
        var other = await h.CostCenters.CreateAsync(h.Admin(), new CostCenterUpsert { Code = "S2", Name = "Second" });
        var e = await h.EmployeeAsync(b);
        var id = (await h.Db.Employees.FirstAsync(x => x.Uuid == e.Uuid)).Id;
        var record = (await h.Directory.GetTermsAsync(h.Org, id, new DateOnly(2026, 1, 15)))!;

        (await h.Directory.GetCostCenterSharesAsync(h.Org, record.RecordId, PayrollCostCenterModes.Central)).Should().ContainSingle(s => s.CostCenterCode == "PAY" && s.Percent == 100);
        (await h.Directory.GetCostCenterSharesAsync(h.Org, record.RecordId, PayrollCostCenterModes.ByDepartment)).Should().ContainSingle(s => s.CostCenterCode == "OPS");

        var split = h.Hire(b, new DateOnly(2026, 2, 1));
        split.CostSplits = [new CostSplitModel { CostCenterUuid = b.CostCenter, Percent = 60 }, new CostSplitModel { CostCenterUuid = other.Uuid, Percent = 40 }];
        await h.Employment.CreateAsync(h.Admin(), e.Uuid, split);
        var after = (await h.Directory.GetTermsAsync(h.Org, id, new DateOnly(2026, 2, 15)))!;
        (await h.Directory.GetCostCenterSharesAsync(h.Org, after.RecordId, PayrollCostCenterModes.ByDepartment)).Select(s => (s.CostCenterCode, s.Percent))
            .Should().BeEquivalentTo([("OPS", 60m), ("S2", 40m)]);
    }

    [Fact]
    public async Task Directory_period_list_includes_a_mid_month_leaver_and_excludes_earlier_leavers_and_other_groups()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var stay = await h.EmployeeAsync(b, "Stay");
        var leaver = await h.EmployeeAsync(b, "Leaver");
        var gone = await h.EmployeeAsync(b, "Gone");
        await h.Employees.ChangeStatusAsync(h.Admin(), leaver.Uuid, new EmployeeStatusChange { Status = "Terminated", EffectiveDate = new DateOnly(2026, 6, 15), DepartureReason = "Resignation" });
        await h.Employees.ChangeStatusAsync(h.Admin(), gone.Uuid, new EmployeeStatusChange { Status = "Terminated", EffectiveDate = new DateOnly(2026, 5, 31), DepartureReason = "Resignation" });

        var june = await h.Directory.GetEmployedInPeriodAsync(h.Org, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        june.Select(t => t.FullName).Should().BeEquivalentTo(["Stay Khan", "Leaver Khan"]);
        (await h.Directory.GetEmployedInPeriodAsync(h.Org, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), "WEEKLY")).Should().BeEmpty();
        _ = stay;
    }

    // ── Bank accounts (task 066) ──

    [Fact]
    public async Task Account_number_is_encrypted_masked_and_only_the_reveal_shows_it()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b);
        var a = await h.Bank.CreateAsync(h.Admin(), e.Uuid, new EmployeeBankAccountUpsert { BankName = "HBL", AccountNumber = "0123456789", Iban = "PK36 SCBL 0000 0011 2345 6702" });

        a.AccountNumberMasked.Should().Be("****6789");
        a.IbanMasked.Should().Be("****6702");
        var row = await h.Db.EmployeeBankAccounts.AsNoTracking().FirstAsync();
        row.AccountNumberEncrypted.Should().NotContain("0123456789");

        var seen = await h.Bank.RevealAsync(h.Admin(), e.Uuid, a.Uuid);
        seen.AccountNumber.Should().Be("0123456789");
        seen.Iban.Should().Be("PK36SCBL0000001123456702");
        (await h.Db.SensitiveAccessLogs.AsNoTracking().Select(l => l.Field).ToListAsync()).Should().BeEquivalentTo([SensitiveFields.BankAccount, SensitiveFields.Iban]);

        var noPerm = () => h.Bank.RevealAsync(h.As(5, P.HRM_BANK_EDIT, P.HRM_SCOPE_ALL), e.Uuid, a.Uuid);
        await noPerm.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task First_account_is_primary_and_there_is_only_ever_one()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b);
        var first = await h.Bank.CreateAsync(h.Admin(), e.Uuid, new EmployeeBankAccountUpsert { BankName = "HBL", AccountNumber = "1111" });
        first.IsPrimary.Should().BeTrue();

        var second = await h.Bank.CreateAsync(h.Admin(), e.Uuid, new EmployeeBankAccountUpsert { BankName = "UBL", AccountNumber = "2222" });
        second.IsPrimary.Should().BeFalse();

        await h.Bank.SetPrimaryAsync(h.Admin(), e.Uuid, second.Uuid);
        (await h.Bank.ListAsync(h.Admin(), e.Uuid)).Where(a => a.IsPrimary).Select(a => a.Uuid).Should().Equal(second.Uuid);

        var third = await h.Bank.CreateAsync(h.Admin(), e.Uuid, new EmployeeBankAccountUpsert { BankName = "MCB", AccountNumber = "3333", IsPrimary = true });
        (await h.Bank.ListAsync(h.Admin(), e.Uuid)).Where(a => a.IsPrimary).Select(a => a.Uuid).Should().Equal(third.Uuid);
    }

    [Fact]
    public async Task A_bank_payment_needs_a_number_cash_does_not_and_an_inactive_account_cannot_be_primary()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b);
        var bad = () => h.Bank.CreateAsync(h.Admin(), e.Uuid, new EmployeeBankAccountUpsert { BankName = "HBL" });
        (await bad.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("BANK_ACCOUNT_NUMBER_REQUIRED:");

        var cash = await h.Bank.CreateAsync(h.Admin(), e.Uuid, new EmployeeBankAccountUpsert { BankName = "Cash", PaymentMethod = "Cash" });
        await h.Bank.DeactivateAsync(h.Admin(), e.Uuid, cash.Uuid);
        var primary = () => h.Bank.SetPrimaryAsync(h.Admin(), e.Uuid, cash.Uuid);
        (await primary.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("BANK_ACCOUNT_INACTIVE:");
    }

    [Fact]
    public async Task Directory_decrypts_the_primary_account_for_the_bank_file()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b);
        await h.Bank.CreateAsync(h.Admin(), e.Uuid, new EmployeeBankAccountUpsert { BankName = "HBL", AccountNumber = "1111" });
        var id = (await h.Db.Employees.FirstAsync(x => x.Uuid == e.Uuid)).Id;
        (await h.Directory.GetPrimaryBankDetailsAsync(h.Org, [id, 9999])).Should().ContainSingle().Which.Value.AccountNumber.Should().Be("1111");
    }
}
