using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Services.Pay;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Tests;

/// <summary>H3 services: tasks 169, 171, 188–190, 193, 200. Components, structures, assignments and the statutory data's verification.</summary>
public sealed class SalaryConfigTests
{
    private static async Task<PayHarness> Ready() => await new PayHarness().ReadyAsync();

    [Fact]
    public async Task Seeded_components_and_a_new_formula_component()
    {
        var h = await Ready();
        var list = await h.Components.ListAsync(h.Admin);
        list.Should().Contain(c => c.Code == "BASIC" && c.IsSystem && c.PostingRole == "DEFAULT_SALARY_EXPENSE");
        var made = await h.Components.CreateAsync(h.Admin, new SalaryComponentUpsert
        {
            Code = "meal", Name = "Meal", Type = "earning", CalcMethod = "Formula", Formula = "BASIC * 0.1", InGross = true, PostingRole = "DEFAULT_SALARY_EXPENSE"
        });
        made.Code.Should().Be("MEAL");
        made.Type.Should().Be("Earning");
    }

    [Theory]
    [InlineData("BAD", "Earning", "Formula", "1 + System.IO", "DEFAULT_SALARY_EXPENSE", "COMPONENT_FORMULA_INVALID")]
    [InlineData("BAD", "Earning", "Formula", "NOPE * 2", "DEFAULT_SALARY_EXPENSE", "COMPONENT_FORMULA_INVALID")]
    [InlineData("BAD", "Earning", "Fixed", null, "DEFAULT_MADE_UP", "COMPONENT_ROLE_UNKNOWN")]
    [InlineData("BAD", "Earning", "Fixed", null, null, "COMPONENT_ROLE_UNKNOWN")]
    public async Task Component_validation(string code, string type, string method, string? formula, string? role, string error)
    {
        var h = await Ready();
        var act = () => h.Components.CreateAsync(h.Admin, new SalaryComponentUpsert { Code = code, Name = "x", Type = type, CalcMethod = method, Formula = formula, PostingRole = role });
        await act.Should().ThrowAsync<BadRequestException>().WithMessage(error + "*");
    }

    [Fact]
    public async Task System_component_code_and_type_are_locked_and_codes_are_unique_and_reserved_names_refused()
    {
        var h = await Ready();
        var basic = (await h.Components.ListAsync(h.Admin)).First(c => c.Code == "BASIC");
        var lockedCode = () => h.Components.UpdateAsync(h.Admin, basic.Uuid, new SalaryComponentUpsert { Code = "BASIC2", Name = "x", Type = basic.Type, CalcMethod = "Fixed", PostingRole = basic.PostingRole });
        await lockedCode.Should().ThrowAsync<ConflictException>().WithMessage("COMPONENT_SYSTEM_LOCKED*");
        var dup = () => h.Components.CreateAsync(h.Admin, new SalaryComponentUpsert { Code = "BASIC", Name = "x", Type = "Earning", PostingRole = "DEFAULT_SALARY_EXPENSE" });
        await dup.Should().ThrowAsync<ConflictException>().WithMessage("COMPONENT_CODE_TAKEN*");
        var reserved = () => h.Components.CreateAsync(h.Admin, new SalaryComponentUpsert { Code = "GROSS", Name = "x", Type = "Earning", PostingRole = "DEFAULT_SALARY_EXPENSE" });
        await reserved.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Structure_formula_may_only_name_what_comes_earlier()
    {
        var h = await Ready();
        var comps = await h.Components.ListAsync(h.Admin);
        var basic = comps.First(c => c.Code == "BASIC");
        var rent = comps.First(c => c.Code == "HRENT");

        var late = () => h.Structures.CreateAsync(h.Admin, new SalaryStructureUpsert
        {
            Code = "S1", Name = "S1",
            Lines = [new() { ComponentUuid = rent.Uuid, Sequence = 1, FormulaOverride = "BASIC * 0.45" }, new() { ComponentUuid = basic.Uuid, Sequence = 2 }]
        });
        // BASIC is a built-in variable too, so this one is legal; a later non-built-in code is not:
        await late.Should().NotThrowAsync();

        var refersLater = () => h.Structures.CreateAsync(h.Admin, new SalaryStructureUpsert
        {
            Code = "S2", Name = "S2",
            Lines = [new() { ComponentUuid = basic.Uuid, Sequence = 1, FormulaOverride = "HRENT * 2" }, new() { ComponentUuid = rent.Uuid, Sequence = 2 }]
        });
        await refersLater.Should().ThrowAsync<BadRequestException>().WithMessage("COMPONENT_FORMULA_INVALID*");

        var ok = await h.Structures.CreateAsync(h.Admin, new SalaryStructureUpsert
        {
            Code = "S3", Name = "S3", IsDefault = true,
            Lines = [new() { ComponentUuid = basic.Uuid, Sequence = 1 }, new() { ComponentUuid = rent.Uuid, Sequence = 2, FormulaOverride = "BASIC * 0.45" }]
        });
        ok.Lines.Select(l => l.ComponentCode).Should().Equal("BASIC", "HRENT");
        var updated = await h.Structures.UpdateAsync(h.Admin, ok.Uuid, new SalaryStructureUpsert { Code = "S3", Name = "S3b", IsDefault = true, Lines = [new() { ComponentUuid = basic.Uuid, Sequence = 1 }] });
        updated.Lines.Should().ContainSingle();
        (await h.Structures.ListAsync(h.Admin)).Count(s => s.IsDefault).Should().Be(1);
    }

    [Fact]
    public async Task Formula_test_route_answers_and_never_throws()
    {
        var h = await Ready();
        (await h.Structures.TestFormulaAsync(h.Admin, new FormulaTestRequest { Formula = "basic * 2", Variables = new() { ["basic"] = 5m } })).Result.Should().Be(10m);
        var bad = await h.Structures.TestFormulaAsync(h.Admin, new FormulaTestRequest { Formula = "File.Delete('x')" });
        bad.Valid.Should().BeFalse();
        bad.Error.Should().NotBeNullOrEmpty();
    }

    // ── assignments ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approval_flow_and_effective_dates()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Ali", 100_000m);
        (await h.Salaries.GetCurrentAsync(h.Admin, e.Uuid))!.Lines.Should().ContainSingle(l => l.ComponentCode == "BASIC" && l.Amount == 100_000m);

        var std = await h.StandardAsync();
        var basic = std.Lines.First(l => l.ComponentCode == "BASIC").ComponentUuid;
        var draft = await h.Salaries.CreateAsync(h.Admin, e.Uuid, new SalaryAssignmentCreate { StructureUuid = std.Uuid, EffectiveFrom = new DateOnly(2026, 11, 1), Lines = [new() { ComponentUuid = basic, Amount = 120_000m }] });
        draft.ApprovalStatus.Should().Be("Draft");

        var notPending = () => h.Salaries.ApproveAsync(h.Admin, draft.Uuid);
        await notPending.Should().ThrowAsync<ConflictException>().WithMessage("SALARY_NOT_PENDING*");

        (await h.Salaries.SubmitAsync(h.Admin, draft.Uuid)).ApprovalStatus.Should().Be("PendingApproval");
        var resubmit = () => h.Salaries.SubmitAsync(h.Admin, draft.Uuid);
        await resubmit.Should().ThrowAsync<ConflictException>().WithMessage("SALARY_NOT_DRAFT*");

        (await h.Salaries.ApproveAsync(h.Admin, draft.Uuid)).ApprovalStatus.Should().Be("Approved");

        var all = await h.Salaries.ListAsync(h.Admin, e.Uuid);
        var old = all.First(a => a.EffectiveFrom == new DateOnly(2026, 1, 1));
        old.ApprovalStatus.Should().Be("Superseded");
        old.EffectiveTo.Should().Be(new DateOnly(2026, 10, 31));

        var overlap = () => h.Salaries.CreateAsync(h.Admin, e.Uuid, new SalaryAssignmentCreate { StructureUuid = std.Uuid, EffectiveFrom = new DateOnly(2026, 6, 1), Lines = [new() { ComponentUuid = basic, Amount = 1m }] });
        await overlap.Should().ThrowAsync<ConflictException>().WithMessage("SALARY_OVERLAP*");
    }

    [Fact]
    public async Task Rejected_and_workflow_statuses_through_the_handler()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Ali", 100_000m);
        var std = await h.StandardAsync();
        var basic = std.Lines.First(l => l.ComponentCode == "BASIC").ComponentUuid;
        var handler = new SalaryChangeStatusHandler(h.Db, new SalaryDecisions(h.Db));
        handler.InterfaceCode.Should().Be(HrmWorkflowCodes.SalaryChange);

        var a = await h.Salaries.CreateAsync(h.Admin, e.Uuid, new SalaryAssignmentCreate { StructureUuid = std.Uuid, EffectiveFrom = new DateOnly(2026, 11, 1), Lines = [new() { ComponentUuid = basic, Amount = 120_000m }] });
        await h.Salaries.SubmitAsync(h.Admin, a.Uuid);
        await handler.UpdateStatusAsync(a.Uuid, "REJECTED");
        (await handler.GetStatusAsync(a.Uuid)).Should().Be("Rejected");

        var b = await h.Salaries.CreateAsync(h.Admin, e.Uuid, new SalaryAssignmentCreate { StructureUuid = std.Uuid, EffectiveFrom = new DateOnly(2026, 11, 1), Lines = [new() { ComponentUuid = basic, Amount = 130_000m }] });
        await h.Salaries.SubmitAsync(h.Admin, b.Uuid);
        await handler.UpdateStatusAsync(b.Uuid, "RECALLED");
        (await handler.GetStatusAsync(b.Uuid)).Should().Be("Draft");
        await h.Salaries.SubmitAsync(h.Admin, b.Uuid);
        await handler.UpdateStatusAsync(b.Uuid, "APPROVED");
        (await handler.GetStatusAsync(b.Uuid)).Should().Be("Approved");
        await handler.UpdateStatusAsync(b.Uuid, "REJECTED"); // already decided: ignored
        (await handler.GetStatusAsync(b.Uuid)).Should().Be("Approved");
        h.Approvals.Submitted.Should().BeGreaterThan(2);
    }

    [Fact]
    public async Task Nobody_approves_their_own_pay_and_scope_and_self_service_hold()
    {
        var h = await Ready();
        var me = await h.StaffAsync("Self", 100_000m);
        var row = await h.Db.Employees.FirstAsync(x => x.Uuid == me.Uuid);
        row.UserId = 555;
        await h.Db.SaveChangesAsync();
        var other = await h.StaffAsync("Other", 90_000m);

        var std = await h.StandardAsync();
        var basic = std.Lines.First(l => l.ComponentCode == "BASIC").ComponentUuid;
        var mine = await h.Salaries.CreateAsync(h.Admin, me.Uuid, new SalaryAssignmentCreate { StructureUuid = std.Uuid, EffectiveFrom = new DateOnly(2026, 11, 1), Lines = [new() { ComponentUuid = basic, Amount = 500_000m }] });
        await h.Salaries.SubmitAsync(h.Admin, mine.Uuid);
        var self = () => h.Salaries.ApproveAsync(h.Core.As(555, P.HRM_SALARY_APPROVE, P.HRM_SCOPE_ALL), mine.Uuid);
        await self.Should().ThrowAsync<ForbiddenException>();

        var employee = h.Core.As(555, P.HRM_SELF_SERVICE);
        (await h.Salaries.ListAsync(employee, me.Uuid)).Should().NotBeEmpty();
        var theirs = () => h.Salaries.ListAsync(employee, other.Uuid);
        await theirs.Should().ThrowAsync<ForbiddenException>();

        var outOfScope = () => h.Salaries.ListAsync(h.Core.As(777, P.HRM_SALARY_VIEW), other.Uuid);
        await outOfScope.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Grade_band_and_bulk_revision_and_another_org()
    {
        var h = await Ready();
        var grade = await h.Core.Grades.CreateAsync(h.Admin, new GradeUpsert { Code = "G1", Name = "G1", MinSalary = 50_000m, MaxSalary = 150_000m });
        var e = await h.Core.Employees.CreateAsync(h.Admin, new EmployeeUpsert { FirstName = "Band", LastName = "Test", Employment = h.Core.Hire(h.Structure) });
        var record = await h.Db.EmploymentRecords.FirstAsync(r => r.Employee!.Uuid == e.Uuid);
        record.GradeId = (await h.Db.Grades.FirstAsync(g => g.Uuid == grade.Uuid)).Id;
        await h.Db.SaveChangesAsync();
        var std = await h.StandardAsync();
        var basic = std.Lines.First(l => l.ComponentCode == "BASIC").ComponentUuid;

        var low = () => h.Salaries.CreateAsync(h.Admin, e.Uuid, new SalaryAssignmentCreate { StructureUuid = std.Uuid, EffectiveFrom = new DateOnly(2026, 1, 1), Lines = [new() { ComponentUuid = basic, Amount = 1_000m }] });
        await low.Should().ThrowAsync<BadRequestException>().WithMessage("SALARY_BELOW_GRADE_MIN*");
        var high = () => h.Salaries.CreateAsync(h.Admin, e.Uuid, new SalaryAssignmentCreate { StructureUuid = std.Uuid, EffectiveFrom = new DateOnly(2026, 1, 1), Lines = [new() { ComponentUuid = basic, Amount = 999_000m }] });
        await high.Should().ThrowAsync<BadRequestException>().WithMessage("SALARY_ABOVE_GRADE_MAX*");

        var a = await h.StaffAsync("A", 100_000.55m);
        var drafts = await h.Salaries.BulkRevisionAsync(h.Admin, new BulkRevisionRequest { Percent = 10m, ComponentCode = "BASIC", EffectiveFrom = new DateOnly(2026, 11, 1), EmployeeUuids = [a.Uuid], Reason = "Annual" });
        drafts.Should().ContainSingle();
        drafts[0].ApprovalStatus.Should().Be("Draft");
        drafts[0].Lines.Single().Amount.Should().Be(110_000.61m);

        var other = new PayHarness(new HrmHarness(dbName: h.Core.DbName));
        var cross = () => other.Salaries.ListAsync(other.Admin, a.Uuid);
        await cross.Should().ThrowAsync<NotFoundException>();
    }

    // ── statutory ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Seeds_are_unverified_and_verifying_and_editing_behave()
    {
        var h = await new PayHarness().ReadyAsync(verify: false);
        (await h.Statutory.ListRatesAsync(h.Admin)).Should().OnlyContain(r => !r.IsVerified);
        (await h.Statutory.ListTaxTablesAsync(h.Admin)).Should().OnlyContain(t => !t.IsVerified);

        var eobi = (await h.Statutory.ListRatesAsync(h.Admin)).First(r => r.RateType == "EOBI");
        var verified = await h.Statutory.VerifyRateAsync(h.Admin, eobi.Uuid, "Checked against notification");
        verified.IsVerified.Should().BeTrue();
        verified.VerifiedByName.Should().Be("HR Admin");
        verified.SourceNote.Should().Be("Checked against notification");

        var edited = await h.Statutory.UpdateRateAsync(h.Admin, eobi.Uuid, new StatutoryRateUpsert
        {
            RateType = "EOBI", Name = eobi.Name, EmployeePercent = 2m, EmployerPercent = 5m, FixedBasis = 40_000m, EffectiveFrom = eobi.EffectiveFrom
        });
        edited.IsVerified.Should().BeFalse("an edit clears verification");

        var table = (await h.Statutory.ListTaxTablesAsync(h.Admin)).First(t => t.Code == "PK-2027");
        var ok = await h.Statutory.VerifyTaxTableAsync(h.Admin, table.Uuid, null);
        ok.IsVerified.Should().BeTrue();
        (await h.Statutory.UnverifyTaxTableAsync(h.Admin, table.Uuid)).IsVerified.Should().BeFalse();
        await h.Statutory.VerifyTaxTableAsync(h.Admin, table.Uuid, null);
        var saved = await h.Statutory.UpdateTaxTableAsync(h.Admin, table.Uuid, new TaxTableUpsert
        {
            Code = table.Code, Name = table.Name, EffectiveFrom = table.EffectiveFrom, EffectiveTo = table.EffectiveTo, Slabs = [new TaxSlabModel { FromAmount = 0m, FixedAmount = 0m, RatePercent = 5m }]
        });
        saved.IsVerified.Should().BeFalse();
        saved.Slabs.Should().ContainSingle();
    }

    [Fact]
    public async Task A_new_rate_closes_the_open_one_and_overlaps_are_refused()
    {
        var h = await Ready();
        var made = await h.Statutory.CreateRateAsync(h.Admin, new StatutoryRateUpsert { RateType = "PF", Name = "PF 2027", EmployeePercent = 10m, EmployerPercent = 10m, EffectiveFrom = new DateOnly(2027, 1, 1) });
        made.IsVerified.Should().BeFalse();
        var rates = await h.Statutory.ListRatesAsync(h.Admin);
        var old = rates.First(r => r.RateType == "PF" && r.EffectiveFrom == new DateOnly(2025, 7, 1));
        old.EffectiveTo.Should().Be(new DateOnly(2026, 12, 31));
        old.IsVerified.Should().BeTrue("only its end date changed");

        var clash = () => h.Statutory.CreateRateAsync(h.Admin, new StatutoryRateUpsert { RateType = "PF", Name = "x", EmployeePercent = 1m, EmployerPercent = 1m, EffectiveFrom = new DateOnly(2026, 6, 1), EffectiveTo = new DateOnly(2026, 8, 1) });
        await clash.Should().ThrowAsync<ConflictException>().WithMessage("STATUTORY_RATE_OVERLAP*");
    }

    [Theory]
    [InlineData(5, null, "start at 0")]
    public async Task Tax_brackets_must_be_contiguous(int from, int? to, string error)
    {
        var h = await Ready();
        var bad = () => h.Statutory.CreateTaxTableAsync(h.Admin, new TaxTableUpsert { Code = "X", Name = "X", EffectiveFrom = new DateOnly(2030, 1, 1), Slabs = [new TaxSlabModel { FromAmount = from, ToAmount = to }] });
        await bad.Should().ThrowAsync<BadRequestException>().WithMessage("TAX_SLABS_INVALID*" + error + "*");

        var gap = () => StatutoryService.ValidateSlabs([new TaxSlabModel { FromAmount = 0m, ToAmount = 10m }, new TaxSlabModel { FromAmount = 20m }]);
        gap.Should().Throw<BadRequestException>();
        var capped = () => StatutoryService.ValidateSlabs([new TaxSlabModel { FromAmount = 0m, ToAmount = 10m }]);
        capped.Should().Throw<BadRequestException>();
        var rate = () => StatutoryService.ValidateSlabs([new TaxSlabModel { FromAmount = 0m, RatePercent = 101m }]);
        rate.Should().Throw<BadRequestException>();
    }
}
