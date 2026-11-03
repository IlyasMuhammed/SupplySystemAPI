using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using QuestPDF.Infrastructure;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Modules.Hrm.Services;
using SMS.Modules.Hrm.Services.Tal;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Models;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Tests;

internal sealed class FakeDocumentStore : IHrmDocumentStore
{
    public Dictionary<Guid, (byte[] Pdf, string Code, string Permission)> Files { get; } = [];
    public Task<Guid> StorePdfAsync(HrmCaller caller, string interfaceCode, Guid documentUuid, string fileName, byte[] pdf, string requiredPermission, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        Files[id] = (pdf, interfaceCode, requiredPermission);
        return Task.FromResult(id);
    }
    public Task<AttachmentContent?> ReadAsync(Guid attachmentUuid) => Task.FromResult<AttachmentContent?>(
        Files.TryGetValue(attachmentUuid, out var f) ? new AttachmentContent(f.Pdf, "offer.pdf", "application/pdf", f.Permission, f.Code) : null);
}

internal sealed class FakeOrgName(string? name) : IOrganizationNameLookup
{
    public Task<string?> GetNameAsync(Guid organizationId, CancellationToken ct = default) => Task.FromResult(name);
}

internal static class TalKit
{
    public static RecruitmentService Recruitment(HrmHarness h, FakeDocumentStore? docs = null, IOrganizationNameLookup? names = null) =>
        new(h.Db, h.Scope, h.Employees, docs ?? new FakeDocumentStore(), h.Seeder, names ?? new FakeOrgName("Acme Ltd"));
    public static PerformanceService Performance(HrmHarness h) => new(h.Db, h.Scope, h.Directory);
    public static TrainingService Training(HrmHarness h) => new(h.Db, h.Scope);

    public static async Task<(Guid Vacancy, Guid Candidate, Guid Application)> PipelineAsync(HrmHarness h, RecruitmentService r, HrmHarness.Base b, int positions = 1, string email = "a@x.test")
    {
        var admin = h.Admin();
        var v = await r.CreateVacancyAsync(admin, new VacancyUpsert { Title = "Clerk", DesignationUuid = b.Clerk, DepartmentUuid = b.Ops, BranchUuid = b.Karachi, Positions = positions });
        var c = await r.CreateCandidateAsync(admin, new CandidateUpsert { FirstName = "Zara", LastName = "Ali", Email = email });
        var a = await r.CreateApplicationAsync(admin, new ApplicationCreate { CandidateUuid = c.Uuid, VacancyUuid = v.Uuid });
        return (v.Uuid, c.Uuid, a.Uuid);
    }
}

/// <summary>Tasks 206–208, 212–214.</summary>
public sealed class RecruitmentTests
{
    static RecruitmentTests() => QuestPDF.Settings.License = LicenseType.Community;

    private static readonly DateOnly Join = new(2026, 11, 1);

    private static ConvertToEmployeeRequest Convert() => new() { JoinDate = Join };

    [Fact]
    public async Task Vacancy_numbers_run_in_order_per_organization_and_counts_cover_every_stage()
    {
        var h = new HrmHarness();
        var other = new HrmHarness(dbName: h.DbName);
        var b = await h.BaseAsync();
        var ob = await other.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (v, _, _) = await TalKit.PipelineAsync(h, r, b);
        var second = await r.CreateVacancyAsync(h.Admin(), new VacancyUpsert { Title = "Two", DesignationUuid = b.Boss, DepartmentUuid = b.Ops });
        var foreign = await TalKit.Recruitment(other).CreateVacancyAsync(other.Admin(), new VacancyUpsert { Title = "X", DesignationUuid = ob.Clerk, DepartmentUuid = ob.Ops });

        second.VacancyNumber.Should().Be("VAC-00002");
        foreign.VacancyNumber.Should().Be("VAC-00001");
        var model = await r.GetVacancyAsync(h.Admin(), v);
        model.ApplicationCounts[ApplicationStages.New].Should().Be(1);
        model.ApplicationCounts.Keys.Should().BeEquivalentTo(ApplicationStages.All);
    }

    [Fact]
    public async Task Another_organizations_rows_are_404()
    {
        var h = new HrmHarness();
        var other = new HrmHarness(dbName: h.DbName);
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (v, c, a) = await TalKit.PipelineAsync(h, r, b);
        var o = TalKit.Recruitment(other);
        var admin = other.Admin();

        await ((Func<Task>)(() => o.GetVacancyAsync(admin, v))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => o.GetCandidateAsync(admin, c))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => o.GetApplicationAsync(admin, a))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => o.ConvertToEmployeeAsync(admin, a, Convert()))).Should().ThrowAsync<NotFoundException>();
        (await o.ListCandidatesAsync(admin, null)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_scoped_recruiter_sees_only_vacancies_of_their_scope()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (v, c, a) = await TalKit.PipelineAsync(h, r, b);
        var sales = await r.CreateVacancyAsync(h.Admin(), new VacancyUpsert { Title = "Sales", DesignationUuid = b.Clerk, DepartmentUuid = b.Sales });
        await h.UserScopes.AddAsync(h.Admin(), new HrUserScopeCreate { UserId = 50, DepartmentUuid = b.Sales });
        var scoped = h.As(50, P.HRM_RECRUITMENT_VIEW, P.HRM_RECRUITMENT_MANAGE);

        (await r.ListVacanciesAsync(scoped, null)).Select(x => x.Uuid).Should().Equal(sales.Uuid);
        await ((Func<Task>)(() => r.GetVacancyAsync(scoped, v))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => r.GetApplicationAsync(scoped, a))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => r.GetCandidateAsync(scoped, c))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => r.CreateVacancyAsync(scoped, new VacancyUpsert { Title = "No", DesignationUuid = b.Clerk, DepartmentUuid = b.Ops }))).Should().ThrowAsync<NotFoundException>();
    }

    [Theory]
    [InlineData("a@x.test", null, "email")]
    [InlineData("A@X.TEST ", null, "email")]
    [InlineData(null, "0300-123 4567", "phone")]
    [InlineData(null, "+92 300 1234567", "phone")]
    public async Task Duplicate_candidates_are_found_by_email_and_phone_in_any_format(string? email, string? phone, string matchedOn)
    {
        var h = new HrmHarness();
        var r = TalKit.Recruitment(h);
        var first = await r.CreateCandidateAsync(h.Admin(), new CandidateUpsert { FirstName = "A", LastName = "B", Email = "a@x.test", Phone = "03001234567" });

        var act = () => r.CreateCandidateAsync(h.Admin(), new CandidateUpsert { FirstName = "C", LastName = "D", Email = email, Phone = phone });
        var ex = (await act.Should().ThrowAsync<CandidateDuplicateException>()).Which;

        ex.Message.Should().StartWith("CANDIDATE_DUPLICATE: ");
        ex.Matches.Should().ContainSingle(m => m.Uuid == first.Uuid && m.MatchedOn.Contains(matchedOn) && m.Name == "A B");
    }

    [Fact]
    public async Task Force_saves_a_duplicate_and_an_edit_does_not_match_itself()
    {
        var h = new HrmHarness();
        var r = TalKit.Recruitment(h);
        var first = await r.CreateCandidateAsync(h.Admin(), new CandidateUpsert { FirstName = "A", LastName = "B", Email = "a@x.test" });

        (await r.CreateCandidateAsync(h.Admin(), new CandidateUpsert { FirstName = "C", LastName = "D", Email = "a@x.test", Force = true })).Uuid.Should().NotBe(first.Uuid);
        var edit = () => r.UpdateCandidateAsync(h.Admin(), first.Uuid, new CandidateUpsert { FirstName = "A2", LastName = "B", Email = "a@x.test" });
        await edit.Should().ThrowAsync<CandidateDuplicateException>(); // the forced copy matches
        (await r.UpdateCandidateAsync(h.Admin(), first.Uuid, new CandidateUpsert { FirstName = "A2", LastName = "B", Email = "a@x.test", Force = true })).FirstName.Should().Be("A2");

        var solo = await r.CreateCandidateAsync(h.Admin(), new CandidateUpsert { FirstName = "S", LastName = "T", Email = "s@x.test" });
        (await r.UpdateCandidateAsync(h.Admin(), solo.Uuid, new CandidateUpsert { FirstName = "S2", LastName = "T", Email = "s@x.test" })).FirstName.Should().Be("S2");
    }

    [Fact]
    public async Task A_duplicate_outside_the_scope_is_reported_without_its_name_and_other_orgs_do_not_match()
    {
        var h = new HrmHarness();
        var other = new HrmHarness(dbName: h.DbName);
        var r = TalKit.Recruitment(h);
        var first = await r.CreateCandidateAsync(h.Admin(), new CandidateUpsert { FirstName = "Hidden", LastName = "Person", Email = "a@x.test" });

        var scoped = h.As(50, P.HRM_RECRUITMENT_MANAGE);
        var act = () => r.CreateCandidateAsync(scoped, new CandidateUpsert { FirstName = "C", LastName = "D", Email = "a@x.test" });
        (await act.Should().ThrowAsync<CandidateDuplicateException>()).Which.Matches.Should().ContainSingle(m => m.Uuid == first.Uuid && m.Name == null);

        (await TalKit.Recruitment(other).CreateCandidateAsync(other.Admin(), new CandidateUpsert { FirstName = "C", LastName = "D", Email = "a@x.test" })).Email.Should().Be("a@x.test");
    }

    [Fact]
    public async Task Deleted_candidates_stop_matching_and_a_live_application_blocks_the_delete()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (_, c, a) = await TalKit.PipelineAsync(h, r, b);

        var refused = () => r.DeleteCandidateAsync(h.Admin(), c);
        (await refused.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("CANDIDATE_IN_USE");
        await r.ChangeStageAsync(h.Admin(), a, new ApplicationStageChange { Stage = "Rejected", RejectionReason = "No" });
        await r.DeleteCandidateAsync(h.Admin(), c);

        (await r.CreateCandidateAsync(h.Admin(), new CandidateUpsert { FirstName = "N", LastName = "M", Email = "a@x.test" })).Should().NotBeNull();
        (await r.ListApplicationsAsync(h.Admin(), null, null)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_application_and_one_to_a_vacancy_on_hold_are_refused()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (v, c, _) = await TalKit.PipelineAsync(h, r, b);

        var again = () => r.CreateApplicationAsync(h.Admin(), new ApplicationCreate { CandidateUuid = c, VacancyUuid = v });
        (await again.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("APPLICATION_DUPLICATE");

        await r.SetVacancyStatusAsync(h.Admin(), v, "OnHold");
        var fresh = await r.CreateCandidateAsync(h.Admin(), new CandidateUpsert { FirstName = "N", LastName = "M" });
        var held = () => r.CreateApplicationAsync(h.Admin(), new ApplicationCreate { CandidateUuid = fresh.Uuid, VacancyUuid = v });
        (await held.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("VACANCY_NOT_OPEN");
    }

    [Fact]
    public async Task Stages_move_freely_on_the_board_but_never_to_Hired_and_rejection_needs_a_reason()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (v, _, a) = await TalKit.PipelineAsync(h, r, b);
        var admin = h.Admin();

        (await r.ChangeStageAsync(admin, a, new ApplicationStageChange { Stage = "interview" })).Stage.Should().Be("Interview");
        (await r.ChangeStageAsync(admin, a, new ApplicationStageChange { Stage = "Screening" })).Stage.Should().Be("Screening");
        var board = await r.GetBoardAsync(admin, v);
        board.Select(c => c.Stage).Should().Equal(ApplicationStages.All);
        board.Single(c => c.Stage == "Screening").Applications.Should().ContainSingle(x => x.Uuid == a);

        var hired = () => r.ChangeStageAsync(admin, a, new ApplicationStageChange { Stage = "Hired" });
        (await hired.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("APPLICATION_STAGE_TRANSITION");
        var noReason = () => r.ChangeStageAsync(admin, a, new ApplicationStageChange { Stage = "Rejected" });
        (await noReason.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("APPLICATION_REJECTION_REASON_REQUIRED");

        (await r.ChangeStageAsync(admin, a, new ApplicationStageChange { Stage = "Rejected", RejectionReason = "Overqualified" })).RejectionReason.Should().Be("Overqualified");
        var reopened = await r.ChangeStageAsync(admin, a, new ApplicationStageChange { Stage = "New" });
        reopened.Stage.Should().Be("New");
        reopened.RejectionReason.Should().BeNull();
    }

    [Fact]
    public async Task Convert_creates_the_employee_and_hire_record_from_the_vacancy_and_closes_the_filled_vacancy()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (v, c, a) = await TalKit.PipelineAsync(h, r, b);
        await r.ChangeStageAsync(h.Admin(), a, new ApplicationStageChange { Stage = "Offer" });

        var employee = await r.ConvertToEmployeeAsync(h.Admin(), a, Convert());

        employee.EmployeeNumber.Should().Be("EMP-0001");
        employee.FullName.Should().Be("Zara Ali");
        employee.PersonalEmail.Should().Be("a@x.test");
        employee.SourceCandidateUuid.Should().Be(c);
        employee.Status.Should().Be("Active");
        employee.JoinDate.Should().Be(Join);
        var records = await h.Employment.ListAsync(h.Admin(), employee.Uuid);
        records.Should().ContainSingle(x => x.EffectiveFrom == Join && x.ChangeReason == "Hire" && x.DepartmentUuid == b.Ops && x.DesignationUuid == b.Clerk && x.BranchUuid == b.Karachi);

        var app = await r.GetApplicationAsync(h.Admin(), a);
        app.Stage.Should().Be("Hired");
        app.HiredEmployeeUuid.Should().Be(employee.Uuid);
        (await r.GetVacancyAsync(h.Admin(), v)).Status.Should().Be("Closed");

        var again = () => r.ConvertToEmployeeAsync(h.Admin(), a, Convert());
        (await again.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("APPLICATION_ALREADY_HIRED");
        var moved = () => r.ChangeStageAsync(h.Admin(), a, new ApplicationStageChange { Stage = "New" });
        (await moved.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("APPLICATION_STAGE_TRANSITION");
    }

    [Fact]
    public async Task Convert_needs_both_permissions_the_offer_stage_an_accepted_offer_and_a_free_position()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (v, _, a) = await TalKit.PipelineAsync(h, r, b, positions: 1);
        var admin = h.Admin();

        await ((Func<Task>)(() => r.ConvertToEmployeeAsync(h.As(1, P.HRM_RECRUITMENT_MANAGE), a, Convert()))).Should().ThrowAsync<ForbiddenException>();
        await ((Func<Task>)(() => r.ConvertToEmployeeAsync(h.As(1, P.HRM_EMPLOYEE_EDIT), a, Convert()))).Should().ThrowAsync<ForbiddenException>();
        await ((Func<Task>)(() => r.ConvertToEmployeeAsync(admin, a, new ConvertToEmployeeRequest()))).Should().ThrowAsync<BadRequestException>();

        var early = () => r.ConvertToEmployeeAsync(admin, a, Convert());
        (await early.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("APPLICATION_STAGE_TRANSITION");

        var offer = await r.CreateOfferAsync(admin, a, new OfferUpsert { OfferedSalary = 100 });
        var unaccepted = () => r.ConvertToEmployeeAsync(admin, a, Convert());
        (await unaccepted.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("OFFER_NOT_ACCEPTED");
        await r.SetOfferStatusAsync(admin, offer.Uuid, "Sent");
        await r.SetOfferStatusAsync(admin, offer.Uuid, "Accepted");
        await r.ConvertToEmployeeAsync(admin, a, Convert());

        var late = await r.CreateCandidateAsync(admin, new CandidateUpsert { FirstName = "L", LastName = "T" });
        await r.SetVacancyStatusAsync(admin, v, "Open");
        var lateApp = await r.CreateApplicationAsync(admin, new ApplicationCreate { CandidateUuid = late.Uuid, VacancyUuid = v });
        await r.ChangeStageAsync(admin, lateApp.Uuid, new ApplicationStageChange { Stage = "Offer" });
        var filled = () => r.ConvertToEmployeeAsync(admin, lateApp.Uuid, Convert());
        (await filled.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("VACANCY_FILLED");
    }

    [Fact]
    public async Task A_candidate_hired_once_cannot_be_hired_again_from_another_vacancy()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h);
        var admin = h.Admin();
        var (_, c, a) = await TalKit.PipelineAsync(h, r, b);
        var v2 = await r.CreateVacancyAsync(admin, new VacancyUpsert { Title = "Two", DesignationUuid = b.Boss, DepartmentUuid = b.Ops, BranchUuid = b.Karachi });
        var a2 = await r.CreateApplicationAsync(admin, new ApplicationCreate { CandidateUuid = c, VacancyUuid = v2.Uuid });
        await r.ChangeStageAsync(admin, a, new ApplicationStageChange { Stage = "Offer" });
        await r.ChangeStageAsync(admin, a2.Uuid, new ApplicationStageChange { Stage = "Offer" });
        await r.ConvertToEmployeeAsync(admin, a, Convert());

        var second = () => r.ConvertToEmployeeAsync(admin, a2.Uuid, Convert());
        (await second.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("APPLICATION_ALREADY_HIRED");
    }

    [Fact]
    public async Task A_scoped_recruiter_cannot_hire_into_a_department_outside_their_scope()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (_, _, a) = await TalKit.PipelineAsync(h, r, b);
        await r.ChangeStageAsync(h.Admin(), a, new ApplicationStageChange { Stage = "Offer" });
        await h.UserScopes.AddAsync(h.Admin(), new HrUserScopeCreate { UserId = 50, DepartmentUuid = b.Ops });
        var scoped = h.As(50, P.HRM_RECRUITMENT_MANAGE, P.HRM_EMPLOYEE_EDIT);

        var act = () => r.ConvertToEmployeeAsync(scoped, a, new ConvertToEmployeeRequest { JoinDate = Join, DepartmentUuid = b.Sales, BranchUuid = b.Lahore });
        await act.Should().ThrowAsync<NotFoundException>();
        (await h.Db.Employees.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Interviews_are_scheduled_scored_and_refused_on_a_closed_application()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (_, _, a) = await TalKit.PipelineAsync(h, r, b);
        var admin = h.Admin();
        var interviewer = await h.EmployeeAsync(b, "Ina");
        var at = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);

        var i = await r.CreateInterviewAsync(admin, a, new InterviewUpsert { InterviewerUuid = interviewer.Uuid, ScheduledAt = at });
        i.Result.Should().Be("Pending");
        i.InterviewerName.Should().Be("Ina Khan");
        var done = await r.UpdateInterviewAsync(admin, i.Uuid, new InterviewUpsert { InterviewerUuid = interviewer.Uuid, ScheduledAt = at, Score = 80, Result = "passed", Feedback = "Good" });
        done.Result.Should().Be("Passed");
        (await r.ListInterviewsAsync(admin, a)).Should().ContainSingle(x => x.Score == 80);

        var bad = () => r.UpdateInterviewAsync(admin, i.Uuid, new InterviewUpsert { ScheduledAt = at, Score = 101 });
        await bad.Should().ThrowAsync<BadRequestException>();
        await r.ChangeStageAsync(admin, a, new ApplicationStageChange { Stage = "Rejected", RejectionReason = "No" });
        var closed = () => r.CreateInterviewAsync(admin, a, new InterviewUpsert { ScheduledAt = at });
        (await closed.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("APPLICATION_CLOSED");
    }

    [Fact]
    public async Task My_interviews_lists_only_the_callers_own_with_no_contact_offer_or_salary_data()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (_, _, a) = await TalKit.PipelineAsync(h, r, b);
        var admin = h.Admin();
        var ina = await h.EmployeeAsync(b, "Ina", userId: 30);
        var oz = await h.EmployeeAsync(b, "Oz", userId: 31);
        await h.EmployeeAsync(b, "Noa", userId: 33);
        var at = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
        var mine = await r.CreateInterviewAsync(admin, a, new InterviewUpsert { InterviewerUuid = ina.Uuid, ScheduledAt = at });
        var theirs = await r.CreateInterviewAsync(admin, a, new InterviewUpsert { InterviewerUuid = oz.Uuid, ScheduledAt = at.AddHours(1) });
        await r.CreateInterviewAsync(admin, a, new InterviewUpsert { ScheduledAt = at.AddHours(2) }); // nobody assigned
        await r.CreateOfferAsync(admin, a, new OfferUpsert { OfferedSalary = 75000 });

        // VIEW with no scope over the vacancy.
        var ina30 = h.As(30, P.HRM_RECRUITMENT_VIEW);
        var row = (await r.ListMyInterviewsAsync(ina30)).Should().ContainSingle().Subject;
        row.Uuid.Should().Be(mine.Uuid);
        row.ApplicationUuid.Should().Be(a);
        row.ApplicationStage.Should().Be("Offer");
        row.CandidateName.Should().Be("Zara Ali");
        row.VacancyTitle.Should().Be("Clerk");
        row.DesignationName.Should().Be("Clerk");
        row.DepartmentName.Should().Be("Operations");
        row.Result.Should().Be("Pending");

        (await r.ListMyInterviewsAsync(h.As(31, P.HRM_RECRUITMENT_VIEW))).Should().ContainSingle(x => x.Uuid == theirs.Uuid);
        (await r.ListMyInterviewsAsync(h.As(33, P.HRM_RECRUITMENT_VIEW))).Should().BeEmpty();
        // HR's reach does not widen "my": holding everything still returns only one's own.
        (await r.ListMyInterviewsAsync(new HrmCaller(h.Org, 30, P.HrmCodes, "Ina"))).Should().ContainSingle(x => x.Uuid == mine.Uuid);

        // The list is what the feedback route works on.
        await r.UpdateInterviewAsync(ina30, row.Uuid, new InterviewUpsert { Feedback = "Strong", Score = 90, Result = "Passed" });
        h.Db.ChangeTracker.Clear();
        var after = (await r.ListMyInterviewsAsync(ina30)).Single();
        after.Feedback.Should().Be("Strong");
        after.Score.Should().Be(90);
        after.Result.Should().Be("Passed");

        // The model has no field that could carry the rest.
        typeof(MyInterviewModel).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
            "Uuid", "ScheduledAt", "Feedback", "Score", "Result", "ApplicationUuid", "ApplicationStage", "CandidateName", "VacancyTitle", "DesignationName", "DepartmentName");
    }

    [Fact]
    public async Task My_interviews_needs_view_and_a_linked_employee_and_stays_inside_the_organization()
    {
        var h = new HrmHarness();
        var other = new HrmHarness(dbName: h.DbName);
        var b = await h.BaseAsync();
        var ob = await other.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (_, c, a) = await TalKit.PipelineAsync(h, r, b);
        var ina = await h.EmployeeAsync(b, "Ina", userId: 30);
        await r.CreateInterviewAsync(h.Admin(), a, new InterviewUpsert { InterviewerUuid = ina.Uuid, ScheduledAt = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc) });

        var manageOnly = () => r.ListMyInterviewsAsync(h.As(30, P.HRM_RECRUITMENT_MANAGE, P.HRM_SELF_SERVICE));
        await manageOnly.Should().ThrowAsync<ForbiddenException>();
        var unlinked = () => r.ListMyInterviewsAsync(h.As(99, P.HRM_RECRUITMENT_VIEW));
        (await unlinked.Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Contain("HRM_NO_EMPLOYEE");

        // The same user id, linked to an employee in another organization, sees nothing of this one.
        await other.EmployeeAsync(ob, "Ina", userId: 30);
        (await TalKit.Recruitment(other).ListMyInterviewsAsync(other.As(30, P.HRM_RECRUITMENT_VIEW))).Should().BeEmpty();

        // A deleted candidate's interview leaves the list.
        await r.ChangeStageAsync(h.Admin(), a, new ApplicationStageChange { Stage = "Rejected", RejectionReason = "No" });
        await r.DeleteCandidateAsync(h.Admin(), c);
        (await r.ListMyInterviewsAsync(h.As(30, P.HRM_RECRUITMENT_VIEW))).Should().BeEmpty();
    }

    [Fact]
    public async Task Positions_cannot_go_below_the_number_hired()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (v, _, a) = await TalKit.PipelineAsync(h, r, b, positions: 3);
        var admin = h.Admin();
        var second = await r.CreateCandidateAsync(admin, new CandidateUpsert { FirstName = "S", LastName = "T" });
        var a2 = await r.CreateApplicationAsync(admin, new ApplicationCreate { CandidateUuid = second.Uuid, VacancyUuid = v });
        foreach (var app in new[] { a, a2.Uuid })
        {
            await r.ChangeStageAsync(admin, app, new ApplicationStageChange { Stage = "Offer" });
            await r.ConvertToEmployeeAsync(admin, app, Convert());
        }
        VacancyUpsert With(int positions) => new() { Title = "Clerk", DesignationUuid = b.Clerk, DepartmentUuid = b.Ops, BranchUuid = b.Karachi, Positions = positions };

        var below = () => r.UpdateVacancyAsync(admin, v, With(1));
        (await below.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("VACANCY_POSITIONS_BELOW_HIRED");
        h.Db.ChangeTracker.Clear();
        (await r.GetVacancyAsync(admin, v)).Positions.Should().Be(3);
        (await r.UpdateVacancyAsync(admin, v, With(2))).Positions.Should().Be(2);
    }

    [Fact]
    public async Task The_assigned_interviewer_with_only_view_records_the_outcome_and_nothing_else()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (_, _, a) = await TalKit.PipelineAsync(h, r, b);
        var admin = h.Admin();
        var ina = await h.EmployeeAsync(b, "Ina", userId: 30);
        var other = await h.EmployeeAsync(b, "Oz", userId: 31);
        var at = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
        var i = await r.CreateInterviewAsync(admin, a, new InterviewUpsert { InterviewerUuid = ina.Uuid, ScheduledAt = at });

        // Holds VIEW but no scope over the vacancy: being assigned is what lets them in.
        var saved = await r.UpdateInterviewAsync(h.As(30, P.HRM_RECRUITMENT_VIEW), i.Uuid,
            new InterviewUpsert { Feedback = "Strong", Score = 90, Result = "Passed", InterviewerUuid = other.Uuid, ScheduledAt = at.AddDays(9) });
        saved.Feedback.Should().Be("Strong");
        saved.Score.Should().Be(90);
        saved.Result.Should().Be("Passed");
        saved.InterviewerUuid.Should().Be(ina.Uuid, "who interviews stays HR's");
        saved.ScheduledAt.Should().Be(at, "when stays HR's");

        // Not assigned: VIEW in scope is 403, VIEW out of scope is 404.
        var inScope = () => r.UpdateInterviewAsync(h.As(31, P.HRM_RECRUITMENT_VIEW, P.HRM_SCOPE_ALL), i.Uuid, new InterviewUpsert { Feedback = "x", ScheduledAt = at });
        await inScope.Should().ThrowAsync<ForbiddenException>();
        // Another user: the scope service caches per user for the request.
        var outOfScope = () => r.UpdateInterviewAsync(h.As(32, P.HRM_RECRUITMENT_VIEW), i.Uuid, new InterviewUpsert { Feedback = "x", ScheduledAt = at });
        await outOfScope.Should().ThrowAsync<NotFoundException>();
        // Assigned, but holding no recruitment code.
        var noCode = () => r.UpdateInterviewAsync(h.As(30, P.HRM_SELF_SERVICE), i.Uuid, new InterviewUpsert { Feedback = "x", ScheduledAt = at });
        await noCode.Should().ThrowAsync<NotFoundException>();
        // Another organization, even with the same user id.
        var foreign = new HrmHarness(dbName: h.DbName);
        var elsewhere = () => TalKit.Recruitment(foreign).UpdateInterviewAsync(foreign.As(30, P.HRM_RECRUITMENT_VIEW), i.Uuid, new InterviewUpsert { Feedback = "x", ScheduledAt = at });
        await elsewhere.Should().ThrowAsync<NotFoundException>();

        h.Db.ChangeTracker.Clear();
        (await r.ListInterviewsAsync(admin, a)).Single().Feedback.Should().Be("Strong");
    }

    [Fact]
    public async Task The_offered_salary_is_masked_and_writable_only_with_the_salary_codes()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (_, _, a) = await TalKit.PipelineAsync(h, r, b);
        var recruiter = h.As(60, P.HRM_RECRUITMENT_MANAGE, P.HRM_SCOPE_ALL);

        var forbidden = () => r.CreateOfferAsync(recruiter, a, new OfferUpsert { OfferedSalary = 500 });
        await forbidden.Should().ThrowAsync<ForbiddenException>();

        var offer = await r.CreateOfferAsync(recruiter, a, new OfferUpsert { Notes = "Pending salary" });
        offer.OfferedSalary.Should().BeNull();
        offer.SalaryMasked.Should().BeTrue();
        (await r.GetApplicationAsync(h.Admin(), a)).Stage.Should().Be("Offer");
        var noSalary = () => r.SetOfferStatusAsync(recruiter, offer.Uuid, "Sent");
        (await noSalary.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("OFFER_SALARY_MISSING");

        var saved = await r.UpdateOfferAsync(h.Admin(), offer.Uuid, new OfferUpsert { OfferedSalary = 500 });
        saved.OfferedSalary.Should().Be(500);
        // A masked editor's save keeps the amount.
        await r.UpdateOfferAsync(recruiter, offer.Uuid, new OfferUpsert { Notes = "x" });
        (await r.GetOfferAsync(h.Admin(), offer.Uuid)).OfferedSalary.Should().Be(500);
        (await r.GetOfferAsync(recruiter, offer.Uuid)).OfferedSalary.Should().BeNull();
    }

    [Fact]
    public async Task Offer_moves_follow_the_status_graph_and_one_offer_lives_at_a_time()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (_, _, a) = await TalKit.PipelineAsync(h, r, b);
        var admin = h.Admin();
        var offer = await r.CreateOfferAsync(admin, a, new OfferUpsert { OfferedSalary = 100 });

        var second = () => r.CreateOfferAsync(admin, a, new OfferUpsert { OfferedSalary = 200 });
        (await second.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("OFFER_ACTIVE_EXISTS");
        var skip = () => r.SetOfferStatusAsync(admin, offer.Uuid, "Accepted");
        (await skip.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("OFFER_STATUS_TRANSITION");

        await r.SetOfferStatusAsync(admin, offer.Uuid, "Sent");
        var edit = () => r.UpdateOfferAsync(admin, offer.Uuid, new OfferUpsert { OfferedSalary = 300 });
        (await edit.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("OFFER_NOT_DRAFT");
        await r.SetOfferStatusAsync(admin, offer.Uuid, "Declined");
        (await r.CreateOfferAsync(admin, a, new OfferUpsert { OfferedSalary = 200 })).Status.Should().Be("Draft");
    }

    [Fact]
    public async Task The_offer_letter_is_a_stored_pdf_behind_the_salary_permission()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var docs = new FakeDocumentStore();
        var r = TalKit.Recruitment(h, docs);
        var (_, _, a) = await TalKit.PipelineAsync(h, r, b);
        var admin = h.Admin();
        var offer = await r.CreateOfferAsync(admin, a, new OfferUpsert { OfferedSalary = 75000, ProposedJoinDate = Join });

        var made = await r.GenerateLetterAsync(admin, offer.Uuid, new OfferLetterRequest());
        made.LetterAttachmentUuid.Should().NotBeNull();
        var stored = docs.Files[made.LetterAttachmentUuid!.Value];
        stored.Code.Should().Be(HrmAttachmentCodes.Letter);
        stored.Permission.Should().Be(P.HRM_SALARY_VIEW);
        var file = await r.ReadLetterAsync(admin, offer.Uuid);
        System.Text.Encoding.ASCII.GetString(file.Content, 0, 5).Should().Be("%PDF-");

        var recruiter = h.As(60, P.HRM_RECRUITMENT_MANAGE, P.HRM_SCOPE_ALL);
        await ((Func<Task>)(() => r.GenerateLetterAsync(recruiter, offer.Uuid, new OfferLetterRequest()))).Should().ThrowAsync<ForbiddenException>();
        await ((Func<Task>)(() => r.ReadLetterAsync(recruiter, offer.Uuid))).Should().ThrowAsync<ForbiddenException>();

        (await r.UpdateOfferAsync(admin, offer.Uuid, new OfferUpsert { OfferedSalary = 80000, ProposedJoinDate = Join })).LetterAttachmentUuid.Should().BeNull("the old letter states the old terms");
    }

    [Fact]
    public async Task The_letter_refuses_missing_values_and_unknown_placeholders()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h);
        var (_, _, a) = await TalKit.PipelineAsync(h, r, b);
        var admin = h.Admin();
        var offer = await r.CreateOfferAsync(admin, a, new OfferUpsert { OfferedSalary = 100 });

        var noDate = () => r.GenerateLetterAsync(admin, offer.Uuid, new OfferLetterRequest());
        (await noDate.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("OFFER_JOIN_DATE_MISSING");
        await r.UpdateOfferAsync(admin, offer.Uuid, new OfferUpsert { ProposedJoinDate = Join });

        var template = await h.Db.LetterTemplates.FirstAsync(t => t.Kind == LetterTemplateCodes.OfferLetter);
        var body = template.Body;
        template.Body = "Hello {Nonsense}";
        await h.Db.SaveChangesAsync();
        var unknown = () => r.GenerateLetterAsync(admin, offer.Uuid, new OfferLetterRequest());
        (await unknown.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("LETTER_PLACEHOLDER_UNKNOWN");
        template.Body = body;
        await h.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_organization_name_comes_from_the_lookup_then_the_body_else_400()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var r = TalKit.Recruitment(h, names: new FakeOrgName(null));
        var (_, _, a) = await TalKit.PipelineAsync(h, r, b);
        var admin = h.Admin();
        var offer = await r.CreateOfferAsync(admin, a, new OfferUpsert { OfferedSalary = 100, ProposedJoinDate = Join });

        var none = () => r.GenerateLetterAsync(admin, offer.Uuid, new OfferLetterRequest());
        (await none.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("LETTER_ORGANIZATION_REQUIRED");
        (await r.GenerateLetterAsync(admin, offer.Uuid, new OfferLetterRequest { OrganizationName = "Typed Ltd" })).LetterAttachmentUuid.Should().NotBeNull();
    }
}

/// <summary>Tasks 219, 220, 224.</summary>
public sealed class PerformanceTests
{
    private static readonly ReviewCycleUpsert Year = new() { Name = "2026", Period = "Annual", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31) };

    private sealed record Team(HrmHarness H, PerformanceService P, Guid Cycle, Guid Employee, Guid Boss, HrmCaller StaffCaller, HrmCaller BossCaller, Guid Review);

    private static async Task<Team> TeamAsync(bool open = true)
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var boss = await h.EmployeeAsync(b, "Bo", "Ss", userId: 10);
        var staff = await h.EmployeeAsync(b, "St", "Aff", userId: 11, manager: boss.Uuid);
        var p = TalKit.Performance(h);
        var cycle = await p.CreateCycleAsync(h.Admin(), Year);
        if (open) await p.OpenCycleAsync(h.Admin(), cycle.Uuid);
        var review = open ? (await p.ListReviewsAsync(h.Admin(), cycle.Uuid, null)).Single(r => r.EmployeeUuid == staff.Uuid).Uuid : Guid.Empty;
        return new Team(h, p, cycle.Uuid, staff.Uuid, boss.Uuid, h.As(11, global::SMS.Shared.Authorization.PermissionCodes.HRM_SELF_SERVICE), h.As(10, global::SMS.Shared.Authorization.PermissionCodes.HRM_SELF_SERVICE), review);
    }

    private static GoalUpsert Goal(Team t, decimal weight, decimal? rating = null, string title = "G") =>
        new() { EmployeeUuid = t.Employee, CycleUuid = t.Cycle, Title = title, Weight = weight, ManagerRating = rating };

    [Fact]
    public void The_overall_rating_is_the_weighted_mean_of_the_manager_ratings()
    {
        PerformanceService.WeightedRating([(60m, 4m, "Open"), (40m, 5m, "Achieved")]).Should().Be(4.4m);
        PerformanceService.WeightedRating([(1m, 3m, "Open"), (2m, 4m, "Open")]).Should().Be(3.67m);
        PerformanceService.WeightedRating([(50m, 4m, "Open"), (50m, null, "Dropped")]).Should().Be(4m, "a dropped goal does not count");
        PerformanceService.WeightedRating([(50m, 4m, "Open"), (0m, null, "Open")]).Should().Be(4m, "a goal with no weight does not count");
        PerformanceService.WeightedRating([(50m, 4m, "Open"), (50m, null, "Open")]).Should().BeNull();
        PerformanceService.WeightedRating([]).Should().BeNull();
        PerformanceService.WeightedRating([(0m, 4m, "Open")]).Should().BeNull();
    }

    [Fact]
    public async Task Opening_creates_one_review_per_employee_with_the_line_manager_and_can_be_repeated()
    {
        var t = await TeamAsync();
        var reviews = await t.P.ListReviewsAsync(t.H.Admin(), t.Cycle, null);
        reviews.Should().HaveCount(2);
        reviews.Single(r => r.EmployeeUuid == t.Employee).ReviewerUuid.Should().Be(t.Boss);

        await t.P.OpenCycleAsync(t.H.Admin(), t.Cycle);
        (await t.P.ListReviewsAsync(t.H.Admin(), t.Cycle, null)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Terminated_staff_get_no_review_and_a_cycle_changes_only_while_draft()
    {
        var t = await TeamAsync(open: false);
        await t.H.Employees.ChangeStatusAsync(t.H.Admin(), t.Employee, new EmployeeStatusChange { Status = "Terminated", EffectiveDate = new DateOnly(2026, 6, 1), DepartureReason = "Resignation" });
        await t.P.OpenCycleAsync(t.H.Admin(), t.Cycle);
        (await t.P.ListReviewsAsync(t.H.Admin(), t.Cycle, null)).Should().ContainSingle(r => r.EmployeeUuid == t.Boss);

        var edit = () => t.P.UpdateCycleAsync(t.H.Admin(), t.Cycle, Year);
        (await edit.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("CYCLE_NOT_DRAFT");
        await t.P.CloseCycleAsync(t.H.Admin(), t.Cycle);
        var reopen = () => t.P.OpenCycleAsync(t.H.Admin(), t.Cycle);
        (await reopen.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("CYCLE_CLOSED");
        var bad = () => t.P.CreateCycleAsync(t.H.Admin(), new ReviewCycleUpsert { Name = "X", StartDate = new DateOnly(2026, 2, 1), EndDate = new DateOnly(2026, 1, 1) });
        (await bad.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("CYCLE_DATES_INVALID");
    }

    [Fact]
    public async Task The_whole_workflow_rates_submits_and_acknowledges()
    {
        var t = await TeamAsync();
        var g1 = await t.P.CreateGoalAsync(t.BossCaller, Goal(t, 60, title: "A"));
        var g2 = await t.P.CreateGoalAsync(t.BossCaller, Goal(t, 40, title: "B"));

        var mine = await t.P.UpdateGoalAsync(t.StaffCaller, g1.Uuid, new GoalUpsert { SelfRating = 5, ManagerRating = 1, Weight = 1, Title = "Hacked" });
        mine.SelfRating.Should().Be(5);
        mine.Title.Should().Be("A", "the employee sets only the self rating");
        mine.Weight.Should().Be(60);
        (await t.H.Db.Goals.AsNoTracking().FirstAsync(g => g.Uuid == g1.Uuid)).ManagerRating.Should().BeNull();

        var unrated = () => t.P.SubmitReviewAsync(t.BossCaller, t.Review);
        (await unrated.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("REVIEW_GOALS_UNRATED");

        var rated = await t.P.UpdateGoalAsync(t.BossCaller, g1.Uuid, new GoalUpsert { Title = "A", Weight = 60, ManagerRating = 4, SelfRating = 1 });
        rated.SelfRating.Should().Be(5, "the manager does not set the self rating");
        await t.P.UpdateGoalAsync(t.BossCaller, g2.Uuid, new GoalUpsert { Title = "B", Weight = 40, ManagerRating = 5 });

        var early = () => t.P.AcknowledgeReviewAsync(t.StaffCaller, t.Review);
        (await early.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("REVIEW_NOT_SUBMITTED");

        var submitted = await t.P.SubmitReviewAsync(t.BossCaller, t.Review);
        submitted.Status.Should().Be("Submitted");
        submitted.OverallRating.Should().Be(4.4m);

        var locked = () => t.P.UpdateGoalAsync(t.BossCaller, g1.Uuid, new GoalUpsert { Title = "A", Weight = 60, ManagerRating = 1 });
        (await locked.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("REVIEW_LOCKED");
        var bossAck = () => t.P.AcknowledgeReviewAsync(t.BossCaller, t.Review);
        await bossAck.Should().ThrowAsync<ForbiddenException>();

        (await t.P.AcknowledgeReviewAsync(t.StaffCaller, t.Review)).Status.Should().Be("Acknowledged");
        var after = () => t.P.UpdateReviewAsync(t.StaffCaller, t.Review, new ReviewUpdate { EmployeeComments = "x" });
        (await after.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("REVIEW_LOCKED");
    }

    [Fact]
    public async Task The_employee_cannot_see_the_managers_side_until_it_is_submitted()
    {
        var t = await TeamAsync();
        var g = await t.P.CreateGoalAsync(t.BossCaller, Goal(t, 100, 3));
        await t.P.UpdateReviewAsync(t.BossCaller, t.Review, new ReviewUpdate { ManagerComments = "Private draft" });

        var draft = await t.P.GetReviewAsync(t.StaffCaller, t.Review);
        draft.ManagerComments.Should().BeNull();
        draft.Goals.Should().ContainSingle(x => x.ManagerRating == null);
        (await t.P.ListGoalsAsync(t.StaffCaller, t.Cycle, null)).Should().ContainSingle(x => x.Uuid == g.Uuid && x.ManagerRating == null);
        (await t.P.ListOwnReviewsAsync(t.StaffCaller)).Single().ManagerComments.Should().BeNull();
        (await t.P.GetReviewAsync(t.BossCaller, t.Review)).ManagerComments.Should().Be("Private draft");

        await t.P.SubmitReviewAsync(t.BossCaller, t.Review);
        var seen = await t.P.GetReviewAsync(t.StaffCaller, t.Review);
        seen.ManagerComments.Should().Be("Private draft");
        seen.OverallRating.Should().Be(3);
        seen.Goals.Single().ManagerRating.Should().Be(3);
    }

    [Fact]
    public async Task Nobody_rates_or_submits_their_own_review_even_with_the_manage_code()
    {
        var t = await TeamAsync();
        var selfManager = t.H.As(11, global::SMS.Shared.Authorization.PermissionCodes.HRM_PERFORMANCE_MANAGE, global::SMS.Shared.Authorization.PermissionCodes.HRM_SCOPE_ALL);

        var goal = () => t.P.CreateGoalAsync(selfManager, Goal(t, 100, 5));
        await goal.Should().ThrowAsync<ForbiddenException>();
        var g = await t.P.CreateGoalAsync(t.BossCaller, Goal(t, 100, 2));
        (await t.P.UpdateGoalAsync(selfManager, g.Uuid, new GoalUpsert { Title = "G", Weight = 100, ManagerRating = 5 })).ManagerRating.Should().BeNull();
        (await t.H.Db.Goals.AsNoTracking().FirstAsync(x => x.Uuid == g.Uuid)).ManagerRating.Should().Be(2);
        var submit = () => t.P.SubmitReviewAsync(selfManager, t.Review);
        await submit.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Assigned_reviews_lists_only_the_callers_with_what_the_screen_needs()
    {
        var t = await TeamAsync();
        await t.P.CreateGoalAsync(t.BossCaller, Goal(t, 100, 4));
        await t.P.UpdateReviewAsync(t.BossCaller, t.Review, new ReviewUpdate { ManagerComments = "Draft note" });

        var row = (await t.P.ListAssignedReviewsAsync(t.BossCaller)).Should().ContainSingle().Subject;
        row.Uuid.Should().Be(t.Review);
        row.EmployeeUuid.Should().Be(t.Employee);
        row.EmployeeName.Should().Be("St Aff");
        row.ManagerComments.Should().Be("Draft note");
        row.Goals.Should().ContainSingle(g => g.ManagerRating == 4);

        // Not the reviewer of anything, and the review about oneself does not count.
        (await t.P.ListAssignedReviewsAsync(t.StaffCaller)).Should().BeEmpty();
        // Every code does not widen it: this employee reviews nobody, and HR sees nothing here.
        (await t.P.ListAssignedReviewsAsync(new HrmCaller(t.H.Org, 11, global::SMS.Shared.Authorization.PermissionCodes.HrmCodes, "St"))).Should().BeEmpty();

        // An unlinked user.
        var unlinked = () => t.P.ListAssignedReviewsAsync(t.H.As(99, global::SMS.Shared.Authorization.PermissionCodes.HRM_SELF_SERVICE));
        (await unlinked.Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Contain("HRM_NO_EMPLOYEE");

        // The same user id, linked in another organization.
        var other = new HrmHarness(dbName: t.H.DbName);
        await other.EmployeeAsync(await other.BaseAsync(), "Bo", "Ss", userId: 10);
        (await TalKit.Performance(other).ListAssignedReviewsAsync(other.As(10, global::SMS.Shared.Authorization.PermissionCodes.HRM_SELF_SERVICE))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_reviewer_HR_reassigns_from_outside_the_team_sees_and_works_the_review_and_the_old_one_loses_it()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var boss = await h.EmployeeAsync(b, "Bo", "Ss", userId: 10);
        var staff = await h.EmployeeAsync(b, "St", "Aff", userId: 11, manager: boss.Uuid);
        // Another department, and no reporting line to the employee.
        var outsider = await h.EmployeeAsync(b, "Ou", "Ter", userId: 12, department: b.Sales, branch: b.Lahore);
        var code = global::SMS.Shared.Authorization.PermissionCodes.HRM_SELF_SERVICE;
        var hr = h.As(79, global::SMS.Shared.Authorization.PermissionCodes.HRM_PERFORMANCE_MANAGE, global::SMS.Shared.Authorization.PermissionCodes.HRM_SCOPE_ALL);
        var p = TalKit.Performance(h);
        var cycle = await p.CreateCycleAsync(h.Admin(), Year);
        await p.OpenCycleAsync(h.Admin(), cycle.Uuid);
        var review = (await p.ListReviewsAsync(h.Admin(), cycle.Uuid, null)).Single(r => r.EmployeeUuid == staff.Uuid).Uuid;
        var oldReviewer = h.As(10, code);
        var goal = await p.CreateGoalAsync(oldReviewer, new GoalUpsert { EmployeeUuid = staff.Uuid, CycleUuid = cycle.Uuid, Title = "G", Weight = 100 });
        var newReviewer = h.As(12, code);
        (await p.ListAssignedReviewsAsync(newReviewer)).Should().BeEmpty();
        await ((Func<Task>)(() => p.GetReviewAsync(newReviewer, review))).Should().ThrowAsync<NotFoundException>();

        await p.UpdateReviewAsync(hr, review, new ReviewUpdate { ReviewerUuid = outsider.Uuid });

        // The old reviewer lost it, though they are still the line manager.
        (await p.ListAssignedReviewsAsync(oldReviewer)).Should().BeEmpty();
        await ((Func<Task>)(() => p.GetReviewAsync(oldReviewer, review))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => p.SubmitReviewAsync(oldReviewer, review))).Should().ThrowAsync<NotFoundException>();

        // The new one works it from the list, end to end, on self service alone.
        var row = (await p.ListAssignedReviewsAsync(newReviewer)).Should().ContainSingle().Subject;
        row.Uuid.Should().Be(review);
        row.ReviewerUuid.Should().Be(outsider.Uuid);
        (await p.UpdateReviewAsync(newReviewer, review, new ReviewUpdate { ManagerComments = "Good year" })).ManagerComments.Should().Be("Good year");
        await p.UpdateGoalAsync(newReviewer, goal.Uuid, new GoalUpsert { Title = "G", Weight = 100, ManagerRating = 4 });
        (await p.SubmitReviewAsync(newReviewer, review)).OverallRating.Should().Be(4);

        // Reviewing one person opens nobody else, and no other route.
        (await p.ListAssignedReviewsAsync(newReviewer)).Should().ContainSingle();
        await ((Func<Task>)(() => h.Scope.RequireEmployeeAsync(newReviewer, staff.Uuid))).Should().ThrowAsync<NotFoundException>();
        (await p.AcknowledgeReviewAsync(h.As(11, code), review)).Status.Should().Be("Acknowledged");
    }

    /// <summary>FE-3: the matrix stated in API-CONTRACT Changes. The route half is pinned in TalGateTests.</summary>
    [Fact]
    public async Task Review_action_matrix_a_reviewer_with_only_self_service_works_the_managers_side()
    {
        var t = await TeamAsync();
        var reviewer = t.BossCaller;

        (await t.P.GetReviewAsync(reviewer, t.Review)).ReviewerUuid.Should().Be(t.Boss);
        (await t.P.UpdateReviewAsync(reviewer, t.Review, new ReviewUpdate { ManagerComments = "Fine", EmployeeComments = "Forged" })).ManagerComments.Should().Be("Fine");
        (await t.P.GetReviewAsync(reviewer, t.Review)).EmployeeComments.Should().BeNull("each side's field is ignored from the other");
        var doomed = await t.P.CreateGoalAsync(reviewer, Goal(t, 10, title: "Doomed"));
        await t.P.DeleteGoalAsync(reviewer, doomed.Uuid);
        var goal = await t.P.CreateGoalAsync(reviewer, Goal(t, 100));
        (await t.P.UpdateGoalAsync(reviewer, goal.Uuid, new GoalUpsert { Title = "G", Weight = 100, ManagerRating = 4 })).ManagerRating.Should().Be(4);

        // Changing the reviewer stays HR's.
        var move = () => t.P.UpdateReviewAsync(reviewer, t.Review, new ReviewUpdate { ManagerComments = "Fine", ReviewerUuid = t.Boss });
        await move.Should().ThrowAsync<ForbiddenException>();
        t.H.Db.ChangeTracker.Clear();
        var ack = () => t.P.AcknowledgeReviewAsync(reviewer, t.Review);
        await ack.Should().ThrowAsync<ForbiddenException>();

        (await t.P.SubmitReviewAsync(reviewer, t.Review)).Status.Should().Be("Submitted");
        var locked = () => t.P.UpdateReviewAsync(reviewer, t.Review, new ReviewUpdate { ManagerComments = "Later" });
        (await locked.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("REVIEW_LOCKED");
    }

    [Fact]
    public async Task Review_action_matrix_the_employee_has_only_their_side()
    {
        var t = await TeamAsync();
        var staff = t.StaffCaller;
        var goal = await t.P.CreateGoalAsync(t.BossCaller, Goal(t, 100, 4));

        (await t.P.UpdateReviewAsync(staff, t.Review, new ReviewUpdate { EmployeeComments = "Mine", ManagerComments = "Forged" })).EmployeeComments.Should().Be("Mine");
        (await t.P.GetReviewAsync(t.BossCaller, t.Review)).ManagerComments.Should().BeNull();
        (await t.P.UpdateGoalAsync(staff, goal.Uuid, new GoalUpsert { SelfRating = 3 })).SelfRating.Should().Be(3);
        await ((Func<Task>)(() => t.P.SubmitReviewAsync(staff, t.Review))).Should().ThrowAsync<ForbiddenException>();
        await ((Func<Task>)(() => t.P.CreateGoalAsync(staff, Goal(t, 1)))).Should().ThrowAsync<ForbiddenException>();
        await ((Func<Task>)(() => t.P.DeleteGoalAsync(staff, goal.Uuid))).Should().ThrowAsync<ForbiddenException>();

        await t.P.SubmitReviewAsync(t.BossCaller, t.Review);
        // After submit: comments yes, self rating no.
        (await t.P.UpdateReviewAsync(staff, t.Review, new ReviewUpdate { EmployeeComments = "Noted" })).EmployeeComments.Should().Be("Noted");
        var rate = () => t.P.UpdateGoalAsync(staff, goal.Uuid, new GoalUpsert { SelfRating = 5 });
        (await rate.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("REVIEW_LOCKED");

        // A closed cycle stops edits but not the acknowledgement.
        t.H.Db.ChangeTracker.Clear();
        await t.P.CloseCycleAsync(t.H.Admin(), t.Cycle);
        var closed = () => t.P.UpdateReviewAsync(staff, t.Review, new ReviewUpdate { EmployeeComments = "x" });
        (await closed.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("CYCLE_CLOSED");
        (await t.P.AcknowledgeReviewAsync(staff, t.Review)).Status.Should().Be("Acknowledged");
    }

    [Fact]
    public async Task Review_action_matrix_codes_alone_view_reads_manage_acts_strangers_get_404()
    {
        var t = await TeamAsync();
        var viewer = t.H.As(80, global::SMS.Shared.Authorization.PermissionCodes.HRM_PERFORMANCE_VIEW, global::SMS.Shared.Authorization.PermissionCodes.HRM_SCOPE_ALL);
        var hr = t.H.As(79, global::SMS.Shared.Authorization.PermissionCodes.HRM_PERFORMANCE_MANAGE, global::SMS.Shared.Authorization.PermissionCodes.HRM_SCOPE_ALL);
        var stranger = t.H.As(77, global::SMS.Shared.Authorization.PermissionCodes.HRM_SELF_SERVICE);
        var outOfScopeHr = t.H.As(78, global::SMS.Shared.Authorization.PermissionCodes.HRM_PERFORMANCE_MANAGE);
        var goal = await t.P.CreateGoalAsync(t.BossCaller, Goal(t, 100, 4));

        // VIEW in scope reads and cannot act, whatever the route lets through.
        (await t.P.GetReviewAsync(viewer, t.Review)).Uuid.Should().Be(t.Review);
        await ((Func<Task>)(() => t.P.UpdateReviewAsync(viewer, t.Review, new ReviewUpdate { ManagerComments = "x" }))).Should().ThrowAsync<ForbiddenException>();
        await ((Func<Task>)(() => t.P.SubmitReviewAsync(viewer, t.Review))).Should().ThrowAsync<ForbiddenException>();
        await ((Func<Task>)(() => t.P.UpdateGoalAsync(viewer, goal.Uuid, new GoalUpsert { Title = "G", Weight = 100 }))).Should().ThrowAsync<ForbiddenException>();

        // Neither a code nor a relationship, or a code out of scope: the review does not exist for them.
        foreach (var caller in new[] { stranger, outOfScopeHr })
        {
            await ((Func<Task>)(() => t.P.GetReviewAsync(caller, t.Review))).Should().ThrowAsync<NotFoundException>();
            await ((Func<Task>)(() => t.P.UpdateReviewAsync(caller, t.Review, new ReviewUpdate()))).Should().ThrowAsync<NotFoundException>();
            await ((Func<Task>)(() => t.P.SubmitReviewAsync(caller, t.Review))).Should().ThrowAsync<NotFoundException>();
            await ((Func<Task>)(() => t.P.UpdateGoalAsync(caller, goal.Uuid, new GoalUpsert { Title = "G", Weight = 1 }))).Should().ThrowAsync<NotFoundException>();
        }

        // MANAGE in scope acts without being the reviewer, and alone can change the reviewer.
        (await t.P.UpdateReviewAsync(hr, t.Review, new ReviewUpdate { ManagerComments = "HR", ReviewerUuid = t.Boss })).ManagerComments.Should().Be("HR");
        var self = () => t.P.UpdateReviewAsync(hr, t.Review, new ReviewUpdate { ManagerComments = "HR", ReviewerUuid = t.Employee });
        await self.Should().ThrowAsync<BadRequestException>();
        t.H.Db.ChangeTracker.Clear();
        (await t.P.SubmitReviewAsync(hr, t.Review)).Status.Should().Be("Submitted");
    }

    [Fact]
    public async Task Someone_unrelated_gets_404_and_HR_in_scope_can_act_for_the_reviewer()
    {
        var t = await TeamAsync();
        var stranger = t.H.As(77, global::SMS.Shared.Authorization.PermissionCodes.HRM_SELF_SERVICE);
        await ((Func<Task>)(() => t.P.GetReviewAsync(stranger, t.Review))).Should().ThrowAsync<NotFoundException>();
        (await t.P.ListGoalsAsync(stranger, t.Cycle, null)).Should().BeEmpty();
        var outOfScope = t.H.As(78, global::SMS.Shared.Authorization.PermissionCodes.HRM_PERFORMANCE_MANAGE);
        (await t.P.ListReviewsAsync(outOfScope, t.Cycle, null)).Should().BeEmpty();
        await ((Func<Task>)(() => t.P.CreateGoalAsync(outOfScope, Goal(t, 100, 1)))).Should().ThrowAsync<NotFoundException>();

        var hr = t.H.As(79, global::SMS.Shared.Authorization.PermissionCodes.HRM_PERFORMANCE_MANAGE, global::SMS.Shared.Authorization.PermissionCodes.HRM_SCOPE_ALL);
        await t.P.CreateGoalAsync(hr, Goal(t, 100, 4));
        (await t.P.SubmitReviewAsync(hr, t.Review)).OverallRating.Should().Be(4);
    }

    [Fact]
    public async Task Another_organizations_cycle_is_404()
    {
        var t = await TeamAsync();
        var other = new HrmHarness(dbName: t.H.DbName);
        var p = TalKit.Performance(other);
        await ((Func<Task>)(() => p.GetCycleAsync(other.Admin(), t.Cycle))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => p.GetReviewAsync(other.Admin(), t.Review))).Should().ThrowAsync<NotFoundException>();
        (await p.ListCyclesAsync(other.Admin())).Should().BeEmpty();
        (await p.ListGoalsAsync(other.Admin(), null, null)).Should().BeEmpty();
    }

    [Fact]
    public async Task Ratings_and_weights_are_validated_and_a_closed_cycle_is_frozen()
    {
        var t = await TeamAsync();
        var bad = () => t.P.CreateGoalAsync(t.BossCaller, Goal(t, 100, 6));
        (await bad.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("GOAL_RATING_INVALID");
        var negative = () => t.P.CreateGoalAsync(t.BossCaller, Goal(t, -1));
        (await negative.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("GOAL_WEIGHT_INVALID");

        var g = await t.P.CreateGoalAsync(t.BossCaller, Goal(t, 100));
        await t.P.DeleteGoalAsync(t.BossCaller, g.Uuid);
        (await t.P.ListGoalsAsync(t.H.Admin(), t.Cycle, t.Employee)).Should().BeEmpty();

        await t.P.CloseCycleAsync(t.H.Admin(), t.Cycle);
        var closed = () => t.P.CreateGoalAsync(t.BossCaller, Goal(t, 100));
        (await closed.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("CYCLE_CLOSED");
    }

    [Fact]
    public async Task The_dashboard_counts_completion_and_buckets_ratings_within_scope()
    {
        var t = await TeamAsync();
        await t.P.CreateGoalAsync(t.BossCaller, Goal(t, 100, 4.5m));
        await t.P.SubmitReviewAsync(t.BossCaller, t.Review);

        var all = await t.P.GetDashboardAsync(t.H.Admin(), t.Cycle);
        all.Completion.Total.Should().Be(2);
        all.Completion.Submitted.Should().Be(1);
        all.CompletionPercent.Should().Be(50);
        all.RatingDistribution.Single(x => x.Rating == 4).Count.Should().Be(1);

        var scoped = await t.P.GetDashboardAsync(t.H.As(78, global::SMS.Shared.Authorization.PermissionCodes.HRM_PERFORMANCE_VIEW), t.Cycle);
        scoped.Completion.Total.Should().Be(0);
        scoped.CompletionPercent.Should().Be(0);
    }
}

/// <summary>Tasks 228, 230.</summary>
public sealed class TrainingTests
{
    private static async Task<(HrmHarness H, TrainingService T, EmployeeDetail E, CourseModel C)> SetupAsync()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b, userId: 20);
        var t = TalKit.Training(h);
        var c = await t.CreateCourseAsync(h.Admin(), new CourseUpsert { Code = "safety", Name = "Safety", Cost = 100, DurationHours = 8 });
        return (h, t, e, c);
    }

    [Fact]
    public async Task Course_codes_are_unique_per_organization_and_inactive_courses_leave_the_catalog()
    {
        var (h, t, _, c) = await SetupAsync();
        c.Code.Should().Be("SAFETY");
        var dup = () => t.CreateCourseAsync(h.Admin(), new CourseUpsert { Code = "SAFETY", Name = "Again" });
        (await dup.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("COURSE_CODE_TAKEN");

        var other = new HrmHarness(dbName: h.DbName);
        (await TalKit.Training(other).CreateCourseAsync(other.Admin(), new CourseUpsert { Code = "SAFETY", Name = "Theirs" })).Code.Should().Be("SAFETY");
        (await TalKit.Training(other).ListCoursesAsync(other.Admin(), true)).Should().ContainSingle(x => x.Name == "Theirs");

        await t.UpdateCourseAsync(h.Admin(), c.Uuid, new CourseUpsert { Code = "SAFETY", Name = "Safety", IsActive = false });
        (await t.ListCoursesAsync(h.Admin(), false)).Should().BeEmpty();
        (await t.ListCoursesAsync(h.Admin(), true)).Should().ContainSingle();
    }

    [Fact]
    public async Task Enrollment_completes_with_a_certificate_and_shows_on_the_employees_own_list()
    {
        var (h, t, e, c) = await SetupAsync();
        var enrolled = await t.EnrollAsync(h.Admin(), new EnrollmentCreate { EmployeeUuid = e.Uuid, CourseUuid = c.Uuid, EnrolledDate = new DateOnly(2026, 1, 10) });
        enrolled.Status.Should().Be("Enrolled");

        var certificate = Guid.NewGuid();
        var done = await t.CompleteAsync(h.Admin(), enrolled.Uuid, new EnrollmentComplete { CompletedDate = new DateOnly(2026, 2, 1), CertificateAttachmentUuid = certificate });
        done.Status.Should().Be("Completed");
        done.CertificateAttachmentUuid.Should().Be(certificate);
        done.CompletedDate.Should().Be(new DateOnly(2026, 2, 1));

        (await t.ListOwnEnrollmentsAsync(h.As(20, P.HRM_SELF_SERVICE))).Should().ContainSingle(x => x.Uuid == enrolled.Uuid && x.CourseName == "Safety");
        var cancel = () => t.CancelAsync(h.Admin(), enrolled.Uuid);
        (await cancel.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("ENROLLMENT_NOT_ACTIVE");
    }

    [Fact]
    public async Task Completion_dates_are_checked()
    {
        var (h, t, e, c) = await SetupAsync();
        var enrolled = await t.EnrollAsync(h.Admin(), new EnrollmentCreate { EmployeeUuid = e.Uuid, CourseUuid = c.Uuid, EnrolledDate = new DateOnly(2026, 1, 10) });
        foreach (var date in new[] { new DateOnly(2026, 1, 9), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1) })
        {
            var act = () => t.CompleteAsync(h.Admin(), enrolled.Uuid, new EnrollmentComplete { CompletedDate = date });
            (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("ENROLLMENT_DATE_INVALID");
        }
        var none = () => t.CompleteAsync(h.Admin(), enrolled.Uuid, new EnrollmentComplete());
        await none.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_live_enrollment_blocks_another_and_a_cancelled_one_can_be_reused_the_same_day()
    {
        var (h, t, e, c) = await SetupAsync();
        var day = new DateOnly(2026, 1, 10);
        var first = await t.EnrollAsync(h.Admin(), new EnrollmentCreate { EmployeeUuid = e.Uuid, CourseUuid = c.Uuid, EnrolledDate = day });
        var dup = () => t.EnrollAsync(h.Admin(), new EnrollmentCreate { EmployeeUuid = e.Uuid, CourseUuid = c.Uuid, EnrolledDate = day.AddDays(3) });
        (await dup.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("ENROLLMENT_DUPLICATE");

        await t.CancelAsync(h.Admin(), first.Uuid);
        var again = await t.EnrollAsync(h.Admin(), new EnrollmentCreate { EmployeeUuid = e.Uuid, CourseUuid = c.Uuid, EnrolledDate = day });
        again.Uuid.Should().Be(first.Uuid);
        again.Status.Should().Be("Enrolled");
    }

    [Fact]
    public async Task Inactive_courses_and_departed_employees_cannot_be_enrolled()
    {
        var (h, t, e, c) = await SetupAsync();
        await t.UpdateCourseAsync(h.Admin(), c.Uuid, new CourseUpsert { Code = "SAFETY", Name = "Safety", IsActive = false });
        var inactive = () => t.EnrollAsync(h.Admin(), new EnrollmentCreate { EmployeeUuid = e.Uuid, CourseUuid = c.Uuid });
        (await inactive.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("COURSE_INACTIVE");

        var open = await t.CreateCourseAsync(h.Admin(), new CourseUpsert { Code = "OPEN", Name = "Open" });
        await h.Employees.ChangeStatusAsync(h.Admin(), e.Uuid, new EmployeeStatusChange { Status = "Terminated", EffectiveDate = new DateOnly(2026, 6, 1), DepartureReason = "Resignation" });
        var departed = () => t.EnrollAsync(h.Admin(), new EnrollmentCreate { EmployeeUuid = e.Uuid, CourseUuid = open.Uuid });
        (await departed.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("ENROLLMENT_EMPLOYEE_INACTIVE");
    }

    [Fact]
    public async Task Scope_and_tenant_apply_to_enrollments()
    {
        var (h, t, e, c) = await SetupAsync();
        var enrolled = await t.EnrollAsync(h.Admin(), new EnrollmentCreate { EmployeeUuid = e.Uuid, CourseUuid = c.Uuid });
        var outOfScope = h.As(70, P.HRM_TRAINING_MANAGE);
        (await t.ListEnrollmentsAsync(outOfScope, null, null)).Should().BeEmpty();
        await ((Func<Task>)(() => t.EnrollAsync(outOfScope, new EnrollmentCreate { EmployeeUuid = e.Uuid, CourseUuid = c.Uuid }))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => t.CancelAsync(outOfScope, enrolled.Uuid))).Should().ThrowAsync<NotFoundException>();

        var other = new HrmHarness(dbName: h.DbName);
        var o = TalKit.Training(other);
        await ((Func<Task>)(() => o.CancelAsync(other.Admin(), enrolled.Uuid))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => o.GetCourseAsync(other.Admin(), c.Uuid))).Should().ThrowAsync<NotFoundException>();
        (await o.ListEnrollmentsAsync(other.Admin(), null, null)).Should().BeEmpty();
    }
}

/// <summary>What only a real database proves: convert is atomic under the retrying strategy, the projections translate, unique indexes answer as 409.</summary>
[Trait("Category", "SqlServer")]
public sealed class TalSqlServerTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A49Tal_{Guid.NewGuid():N};";
    private readonly Guid _org = Guid.NewGuid();

    static TalSqlServerTests() => QuestPDF.Settings.License = LicenseType.Community;

    private HrmDbContext NewContext(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<HrmDbContext>().UseSqlServer(_connection, sql => sql.EnableRetryOnFailure(3));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new HrmDbContext(options.Options, new StaticTenantContext { OrganizationId = _org });
    }

    private sealed record Stack(HrmDbContext Db, RecruitmentService Recruitment, PerformanceService Performance, TrainingService Training, IEmployeeService Employees,
        IBranchService Branches, IDepartmentService Departments, IDesignationService Designations);

    private Stack NewStack(IInterceptor? interceptor = null)
    {
        var db = NewContext(interceptor);
        var key = Options.Create(new AppSettings { AesEncryptionKey = HrmHarness.AesKey });
        var sensitive = new HrmSensitiveData(new AesEncryptionService(key), key);
        var seeder = new HrmSeeder(db);
        var scope = new HrmScopeService(db);
        var employment = new EmploymentRecordService(db, scope);
        var employees = new EmployeeService(db, scope, sensitive, seeder, employment);
        return new Stack(db, new RecruitmentService(db, scope, employees, new FakeDocumentStore(), seeder, new FakeOrgName("Acme")),
            new PerformanceService(db, scope, new HrmEmployeeDirectory(db, sensitive, seeder)), new TrainingService(db, scope), employees,
            new BranchService(db), new DepartmentService(db), new DesignationService(db));
    }

    private HrmCaller Admin => new(_org, 900, P.HrmCodes, "HR Admin");

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    private async Task<(Guid Application, Guid Branch, Guid Vacancy, Guid Candidate)> OfferStageAsync(Stack s)
    {
        var branch = await s.Branches.CreateAsync(Admin, new BranchUpsert { Code = "KHI", Name = "Karachi" });
        var dept = await s.Departments.CreateAsync(Admin, new DepartmentUpsert { Code = "OPS", Name = "Operations" });
        var desig = await s.Designations.CreateAsync(Admin, new DesignationUpsert { Code = "CLERK", Name = "Clerk", Level = 1 });
        var v = await s.Recruitment.CreateVacancyAsync(Admin, new VacancyUpsert { Title = "Clerk", DesignationUuid = desig.Uuid, DepartmentUuid = dept.Uuid, BranchUuid = branch.Uuid });
        var c = await s.Recruitment.CreateCandidateAsync(Admin, new CandidateUpsert { FirstName = "Zara", LastName = "Ali", Email = "z@x.test", Phone = "0300-1234567" });
        var a = await s.Recruitment.CreateApplicationAsync(Admin, new ApplicationCreate { CandidateUuid = c.Uuid, VacancyUuid = v.Uuid });
        await s.Recruitment.ChangeStageAsync(Admin, a.Uuid, new ApplicationStageChange { Stage = "Offer" });
        return (a.Uuid, branch.Uuid, v.Uuid, c.Uuid);
    }

    private async Task<int> EmployeeCountAsync()
    {
        await using var db = NewContext();
        return await db.Employees.IgnoreQueryFilters().CountAsync(e => e.OrganizationId == _org);
    }

    [Fact]
    public async Task Convert_commits_everything_on_a_retrying_context()
    {
        var s = NewStack();
        var (a, _, v, _) = await OfferStageAsync(s);

        var employee = await s.Recruitment.ConvertToEmployeeAsync(Admin, a, new ConvertToEmployeeRequest { JoinDate = new DateOnly(2026, 11, 1) });
        employee.EmployeeNumber.Should().Be("EMP-0001");

        await using var fresh = NewContext();
        var app = await fresh.JobApplications.IgnoreQueryFilters().SingleAsync(x => x.Uuid == a);
        app.Stage.Should().Be("Hired");
        app.HiredEmployeeId.Should().NotBeNull();
        (await fresh.EmploymentRecords.IgnoreQueryFilters().CountAsync(r => r.EmployeeId == app.HiredEmployeeId)).Should().Be(1);
        (await fresh.Vacancies.IgnoreQueryFilters().SingleAsync(x => x.Uuid == v)).Status.Should().Be("Closed");
    }

    [Fact]
    public async Task A_convert_that_fails_after_the_employee_insert_leaves_nothing_and_burns_no_number()
    {
        var s = NewStack();
        var (a, _, _, _) = await OfferStageAsync(s);

        // The manager is checked by the record factory, after the employee row is saved.
        var bad = () => s.Recruitment.ConvertToEmployeeAsync(Admin, a, new ConvertToEmployeeRequest { JoinDate = new DateOnly(2026, 11, 1), ManagerUuid = Guid.NewGuid() });
        await bad.Should().ThrowAsync<NotFoundException>();
        (await EmployeeCountAsync()).Should().Be(0);

        await using (var fresh = NewContext())
        {
            (await fresh.JobApplications.IgnoreQueryFilters().SingleAsync(x => x.Uuid == a)).Stage.Should().Be("Offer");
        }

        var clean = NewStack();
        (await clean.Recruitment.ConvertToEmployeeAsync(Admin, a, new ConvertToEmployeeRequest { JoinDate = new DateOnly(2026, 11, 1) })).EmployeeNumber.Should().Be("EMP-0001");
    }

    [Fact]
    public async Task Every_read_translates_on_sql_server()
    {
        var s = NewStack();
        var (a, _, v, c) = await OfferStageAsync(s);
        await s.Recruitment.CreateInterviewAsync(Admin, a, new InterviewUpsert { ScheduledAt = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc) });
        (await s.Recruitment.GetBoardAsync(Admin, v)).Single(x => x.Stage == "Offer").Applications.Should().ContainSingle();
        (await s.Recruitment.ListVacanciesAsync(Admin, "Open")).Should().ContainSingle(x => x.ApplicationCounts["Offer"] == 1);
        (await s.Recruitment.ListCandidatesAsync(Admin, "Zara")).Should().ContainSingle();
        (await s.Recruitment.GetCandidateAsync(Admin, c)).Applications.Should().ContainSingle();
        (await s.Recruitment.ListInterviewsAsync(Admin, a)).Should().ContainSingle();

        var duplicate = () => s.Recruitment.CreateCandidateAsync(Admin, new CandidateUpsert { FirstName = "X", LastName = "Y", Phone = "+92 300 1234567" });
        await duplicate.Should().ThrowAsync<CandidateDuplicateException>();

        // A scoped caller goes through the non-All branches of every query.
        var scoped = new HrmCaller(_org, 55, [P.HRM_RECRUITMENT_VIEW, P.HRM_PERFORMANCE_VIEW, P.HRM_TRAINING_VIEW, P.HRM_SELF_SERVICE], "Scoped");
        (await s.Recruitment.ListVacanciesAsync(scoped, null)).Should().BeEmpty();
        (await s.Recruitment.ListCandidatesAsync(scoped, null)).Should().BeEmpty();
        (await s.Recruitment.ListApplicationsAsync(scoped, null, null)).Should().BeEmpty();

        var e = await s.Recruitment.ConvertToEmployeeAsync(Admin, a, new ConvertToEmployeeRequest { JoinDate = new DateOnly(2026, 1, 1) });
        e.CurrentEmployment.Should().NotBeNull();

        var cycle = await s.Performance.CreateCycleAsync(Admin, new ReviewCycleUpsert { Name = "2026", Period = "Annual", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31) });
        await s.Performance.OpenCycleAsync(Admin, cycle.Uuid);
        var goal = await s.Performance.CreateGoalAsync(Admin, new GoalUpsert { EmployeeUuid = e.Uuid, CycleUuid = cycle.Uuid, Title = "G", Weight = 100, ManagerRating = 4 });
        var review = (await s.Performance.ListReviewsAsync(Admin, cycle.Uuid, null)).Single();
        review.Goals.Should().ContainSingle(g => g.Uuid == goal.Uuid);
        (await s.Performance.SubmitReviewAsync(Admin, review.Uuid)).OverallRating.Should().Be(4);
        (await s.Performance.GetDashboardAsync(Admin, cycle.Uuid)).CompletionPercent.Should().Be(100);
        (await s.Performance.ListCyclesAsync(Admin)).Single().Completion.Submitted.Should().Be(1);
        (await s.Performance.ListGoalsAsync(scoped, cycle.Uuid, null)).Should().BeEmpty();
        (await s.Performance.ListGoalsAsync(new HrmCaller(_org, 56, [P.HRM_SELF_SERVICE]), null, null)).Should().BeEmpty();
        (await s.Performance.ListReviewsAsync(scoped, null, null)).Should().BeEmpty();

        var course = await s.Training.CreateCourseAsync(Admin, new CourseUpsert { Code = "C1", Name = "One", Cost = 5 });
        var enrollment = await s.Training.EnrollAsync(Admin, new EnrollmentCreate { EmployeeUuid = e.Uuid, CourseUuid = course.Uuid });
        (await s.Training.ListEnrollmentsAsync(Admin, course.Uuid, e.Uuid)).Should().ContainSingle(x => x.Uuid == enrollment.Uuid);
        (await s.Training.ListEnrollmentsAsync(scoped, null, null)).Should().BeEmpty();
    }

    [Fact]
    public async Task My_interviews_translates_on_sql_server()
    {
        var s = NewStack();
        var (a, _, _, _) = await OfferStageAsync(s);
        var ina = await s.Employees.CreateAsync(Admin, new EmployeeUpsert { FirstName = "Ina", LastName = "Khan", UserId = 70 });
        var mine = await s.Recruitment.CreateInterviewAsync(Admin, a, new InterviewUpsert { InterviewerUuid = ina.Uuid, ScheduledAt = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc) });
        await s.Recruitment.CreateInterviewAsync(Admin, a, new InterviewUpsert { ScheduledAt = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc) });

        var row = (await s.Recruitment.ListMyInterviewsAsync(new HrmCaller(_org, 70, [P.HRM_RECRUITMENT_VIEW], "Ina"))).Should().ContainSingle().Subject;
        row.Uuid.Should().Be(mine.Uuid);
        row.CandidateName.Should().Be("Zara Ali");
        row.DepartmentName.Should().Be("Operations");
    }

    [Fact]
    public async Task A_racing_course_code_answers_409_not_500()
    {
        // The rival commits after this request's service check has passed and before its insert, so only the unique index can refuse it.
        var rivalCommitted = false;
        var two = NewStack(new BeforeFirstSave(async () =>
        {
            await NewStack().Training.CreateCourseAsync(Admin, new CourseUpsert { Code = "RACE", Name = "Rival" });
            rivalCommitted = true;
        }));

        var act = () => two.Training.CreateCourseAsync(Admin, new CourseUpsert { Code = "RACE", Name = "Two" });
        (await act.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("COURSE_CODE_TAKEN");

        rivalCommitted.Should().BeTrue("otherwise the service check answered and the race was not exercised");
        await using var fresh = NewContext();
        (await fresh.Courses.IgnoreQueryFilters().Where(c => c.OrganizationId == _org && c.Code == "RACE").Select(c => c.Name).ToListAsync()).Should().Equal("Rival");
    }

    /// <summary>Runs once, inside the first save: after every service check, before the INSERT reaches the database.</summary>
    private sealed class BeforeFirstSave(Func<Task> action) : SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await action();
            }
            return result;
        }
    }
}
