using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Services.Tal;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Controllers;

// API-CONTRACT §8. Each controller gates the module and its sub-feature: both must be on.

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Recruitment)]
[Route("api/hrm/vacancies")]
public sealed class VacanciesController : HrmControllerBase
{
    private readonly IRecruitmentService _svc;
    public VacanciesController(ITenantContext tenant, IRecruitmentService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List vacancies")]
    [HttpGet, RequirePermission(P.HRM_RECRUITMENT_VIEW, P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> List([FromQuery] string? status, CancellationToken ct) => Done(await _svc.ListVacanciesAsync(Caller, status, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Get a vacancy")]
    [HttpGet("{uuid:guid}"), RequirePermission(P.HRM_RECRUITMENT_VIEW, P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) => Done(await _svc.GetVacancyAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("A vacancy's applications grouped by stage")]
    [HttpGet("{uuid:guid}/board"), RequirePermission(P.HRM_RECRUITMENT_VIEW, P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Board(Guid uuid, CancellationToken ct) => Done(await _svc.GetBoardAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a vacancy")]
    [HttpPost, RequirePermission(P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Create([FromBody] VacancyUpsert req, CancellationToken ct) => Done(await _svc.CreateVacancyAsync(Caller, req, ct), "Vacancy created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a vacancy")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] VacancyUpsert req, CancellationToken ct) => Done(await _svc.UpdateVacancyAsync(Caller, uuid, req, ct), "Vacancy saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Change a vacancy's status")]
    [HttpPatch("{uuid:guid}/status"), RequirePermission(P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Status(Guid uuid, [FromBody] TalStatusChange req, CancellationToken ct) => Done(await _svc.SetVacancyStatusAsync(Caller, uuid, req.Status, ct));
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Recruitment)]
[Route("api/hrm/candidates")]
public sealed class CandidatesController : HrmControllerBase
{
    private readonly IRecruitmentService _svc;
    public CandidatesController(ITenantContext tenant, IRecruitmentService svc) : base(tenant) => _svc = svc;

    /// <summary>409 with the matches as the result, which the exception middleware cannot carry.</summary>
    private IActionResult Duplicate(CandidateDuplicateException ex) => Conflict(new ApiResponse<IReadOnlyList<CandidateDuplicateMatch>> { Success = false, Message = ex.Message, Result = ex.Matches });

    [Microsoft.AspNetCore.Http.EndpointSummary("List or search candidates")]
    [HttpGet, RequirePermission(P.HRM_RECRUITMENT_VIEW, P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> List([FromQuery] string? search, CancellationToken ct) => Done(await _svc.ListCandidatesAsync(Caller, search, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Get a candidate")]
    [HttpGet("{uuid:guid}"), RequirePermission(P.HRM_RECRUITMENT_VIEW, P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) => Done(await _svc.GetCandidateAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a candidate (409 on a likely duplicate unless forced)")]
    [HttpPost, RequirePermission(P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Create([FromBody] CandidateUpsert req, CancellationToken ct)
    {
        try { return Done(await _svc.CreateCandidateAsync(Caller, req, ct), "Candidate created."); }
        catch (CandidateDuplicateException ex) { return Duplicate(ex); }
    }

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a candidate")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] CandidateUpsert req, CancellationToken ct)
    {
        try { return Done(await _svc.UpdateCandidateAsync(Caller, uuid, req, ct), "Candidate saved."); }
        catch (CandidateDuplicateException ex) { return Duplicate(ex); }
    }

    [Microsoft.AspNetCore.Http.EndpointSummary("Delete a candidate")]
    [HttpDelete("{uuid:guid}"), RequirePermission(P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Delete(Guid uuid, CancellationToken ct)
    {
        await _svc.DeleteCandidateAsync(Caller, uuid, ct);
        return Done(true, "Candidate removed.");
    }
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Recruitment)]
[Route("api/hrm/applications")]
public sealed class JobApplicationsController : HrmControllerBase
{
    private readonly IRecruitmentService _svc;
    public JobApplicationsController(ITenantContext tenant, IRecruitmentService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List job applications")]
    [HttpGet, RequirePermission(P.HRM_RECRUITMENT_VIEW, P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> List([FromQuery] Guid? vacancyUuid, [FromQuery] string? stage, CancellationToken ct) => Done(await _svc.ListApplicationsAsync(Caller, vacancyUuid, stage, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Get a job application")]
    [HttpGet("{uuid:guid}"), RequirePermission(P.HRM_RECRUITMENT_VIEW, P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) => Done(await _svc.GetApplicationAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Apply a candidate to a vacancy")]
    [HttpPost, RequirePermission(P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Create([FromBody] ApplicationCreate req, CancellationToken ct) => Done(await _svc.CreateApplicationAsync(Caller, req, ct), "Application created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Move an application to another stage")]
    [HttpPatch("{uuid:guid}/stage"), RequirePermission(P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Stage(Guid uuid, [FromBody] ApplicationStageChange req, CancellationToken ct) => Done(await _svc.ChangeStageAsync(Caller, uuid, req, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Rate an application from 1 to 5")]
    [HttpPatch("{uuid:guid}/rating"), RequirePermission(P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Rating(Guid uuid, [FromBody] ApplicationRatingChange req, CancellationToken ct) => Done(await _svc.SetRatingAsync(Caller, uuid, req.Rating, ct));

    /// <summary>Both codes: two attributes are AND, one attribute's list is any-of.</summary>
    [Microsoft.AspNetCore.Http.EndpointSummary("Hire: convert an application into an employee")]
    [HttpPost("{uuid:guid}/convert"), RequirePermission(P.HRM_RECRUITMENT_MANAGE), RequirePermission(P.HRM_EMPLOYEE_EDIT)]
    public async Task<IActionResult> Convert(Guid uuid, [FromBody] ConvertToEmployeeRequest req, CancellationToken ct) => Done(await _svc.ConvertToEmployeeAsync(Caller, uuid, req, ct), "Employee created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("List an application's interviews")]
    [HttpGet("{uuid:guid}/interviews"), RequirePermission(P.HRM_RECRUITMENT_VIEW, P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Interviews(Guid uuid, CancellationToken ct) => Done(await _svc.ListInterviewsAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Schedule an interview")]
    [HttpPost("{uuid:guid}/interviews"), RequirePermission(P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> AddInterview(Guid uuid, [FromBody] InterviewUpsert req, CancellationToken ct) => Done(await _svc.CreateInterviewAsync(Caller, uuid, req, ct), "Interview scheduled.");

    [Microsoft.AspNetCore.Http.EndpointSummary("List an application's offers")]
    [HttpGet("{uuid:guid}/offers"), RequirePermission(P.HRM_RECRUITMENT_VIEW, P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Offers(Guid uuid, CancellationToken ct) => Done(await _svc.ListOffersAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create an offer")]
    [HttpPost("{uuid:guid}/offers"), RequirePermission(P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> AddOffer(Guid uuid, [FromBody] OfferUpsert req, CancellationToken ct) => Done(await _svc.CreateOfferAsync(Caller, uuid, req, ct), "Offer created.");
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Recruitment)]
[Route("api/hrm/interviews")]
public sealed class InterviewsController : HrmControllerBase
{
    private readonly IRecruitmentService _svc;
    public InterviewsController(ITenantContext tenant, IRecruitmentService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("The caller's own assigned interviews")]
    [HttpGet("me"), RequirePermission(P.HRM_RECRUITMENT_VIEW)]
    public async Task<IActionResult> Mine(CancellationToken ct) => Done(await _svc.ListMyInterviewsAsync(Caller, ct));

    // VIEW lets in the assigned interviewer, for the outcome only; the service refuses any other VIEW holder.
    [Microsoft.AspNetCore.Http.EndpointSummary("Update an interview or save its feedback")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_RECRUITMENT_MANAGE, P.HRM_RECRUITMENT_VIEW)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] InterviewUpsert req, CancellationToken ct) => Done(await _svc.UpdateInterviewAsync(Caller, uuid, req, ct), "Interview saved.");
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Recruitment)]
[Route("api/hrm/offers")]
public sealed class OffersController : HrmControllerBase
{
    private readonly IRecruitmentService _svc;
    public OffersController(ITenantContext tenant, IRecruitmentService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("Get an offer")]
    [HttpGet("{uuid:guid}"), RequirePermission(P.HRM_RECRUITMENT_VIEW, P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) => Done(await _svc.GetOfferAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Update an offer")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] OfferUpsert req, CancellationToken ct) => Done(await _svc.UpdateOfferAsync(Caller, uuid, req, ct), "Offer saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Change an offer's status")]
    [HttpPatch("{uuid:guid}/status"), RequirePermission(P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Status(Guid uuid, [FromBody] TalStatusChange req, CancellationToken ct) => Done(await _svc.SetOfferStatusAsync(Caller, uuid, req.Status, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Generate an offer letter")]
    [HttpPost("{uuid:guid}/letter"), RequirePermission(P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> MakeLetter(Guid uuid, [FromBody] OfferLetterRequest? req, CancellationToken ct) => Done(await _svc.GenerateLetterAsync(Caller, uuid, req ?? new OfferLetterRequest(), ct), "Offer letter generated.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Download an offer letter as PDF")]
    [HttpGet("{uuid:guid}/letter"), RequirePermission(P.HRM_RECRUITMENT_VIEW, P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Letter(Guid uuid, CancellationToken ct)
    {
        var file = await _svc.ReadLetterAsync(Caller, uuid, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Performance)]
[Route("api/hrm/review-cycles")]
public sealed class ReviewCyclesController : HrmControllerBase
{
    private readonly IPerformanceService _svc;
    public ReviewCyclesController(ITenantContext tenant, IPerformanceService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List review cycles")]
    [HttpGet, RequirePermission(P.HRM_PERFORMANCE_VIEW, P.HRM_PERFORMANCE_MANAGE)]
    public async Task<IActionResult> List(CancellationToken ct) => Done(await _svc.ListCyclesAsync(Caller, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Get a review cycle")]
    [HttpGet("{uuid:guid}"), RequirePermission(P.HRM_PERFORMANCE_VIEW, P.HRM_PERFORMANCE_MANAGE)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) => Done(await _svc.GetCycleAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a review cycle")]
    [HttpPost, RequirePermission(P.HRM_PERFORMANCE_MANAGE)]
    public async Task<IActionResult> Create([FromBody] ReviewCycleUpsert req, CancellationToken ct) => Done(await _svc.CreateCycleAsync(Caller, req, ct), "Review cycle created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a review cycle")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_PERFORMANCE_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] ReviewCycleUpsert req, CancellationToken ct) => Done(await _svc.UpdateCycleAsync(Caller, uuid, req, ct), "Review cycle saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Open a cycle and create its reviews")]
    [HttpPost("{uuid:guid}/open"), RequirePermission(P.HRM_PERFORMANCE_MANAGE)]
    public async Task<IActionResult> Open(Guid uuid, CancellationToken ct) => Done(await _svc.OpenCycleAsync(Caller, uuid, ct), "Review cycle opened.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Close a review cycle")]
    [HttpPost("{uuid:guid}/close"), RequirePermission(P.HRM_PERFORMANCE_MANAGE)]
    public async Task<IActionResult> Close(Guid uuid, CancellationToken ct) => Done(await _svc.CloseCycleAsync(Caller, uuid, ct), "Review cycle closed.");
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Performance)]
[Route("api/hrm/reviews")]
public sealed class ReviewsController : HrmControllerBase
{
    private readonly IPerformanceService _svc;
    public ReviewsController(ITenantContext tenant, IPerformanceService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List reviews in scope")]
    [HttpGet, RequirePermission(P.HRM_PERFORMANCE_VIEW, P.HRM_PERFORMANCE_MANAGE)]
    public async Task<IActionResult> List([FromQuery] Guid? cycleUuid, [FromQuery] string? status, CancellationToken ct) => Done(await _svc.ListReviewsAsync(Caller, cycleUuid, status, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("The caller's own reviews")]
    [HttpGet("me"), RequirePermission(P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Mine(CancellationToken ct) => Done(await _svc.ListOwnReviewsAsync(Caller, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Reviews where the caller is the reviewer")]
    [HttpGet("assigned"), RequirePermission(P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Assigned(CancellationToken ct) => Done(await _svc.ListAssignedReviewsAsync(Caller, ct));

    // The self-service code lets in the employee and a line manager who is the reviewer; the service answers 404 to anyone else.
    [Microsoft.AspNetCore.Http.EndpointSummary("Get a review")]
    [HttpGet("{uuid:guid}"), RequirePermission(P.HRM_PERFORMANCE_VIEW, P.HRM_PERFORMANCE_MANAGE, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) => Done(await _svc.GetReviewAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a review's comments or reviewer")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_PERFORMANCE_MANAGE, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] ReviewUpdate req, CancellationToken ct) => Done(await _svc.UpdateReviewAsync(Caller, uuid, req, ct), "Review saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Submit a review (computes the rating)")]
    [HttpPost("{uuid:guid}/submit"), RequirePermission(P.HRM_PERFORMANCE_MANAGE, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Submit(Guid uuid, CancellationToken ct) => Done(await _svc.SubmitReviewAsync(Caller, uuid, ct), "Review submitted.");

    [Microsoft.AspNetCore.Http.EndpointSummary("The employee acknowledges a review")]
    [HttpPost("{uuid:guid}/acknowledge"), RequirePermission(P.HRM_SELF_SERVICE, P.HRM_PERFORMANCE_MANAGE)]
    public async Task<IActionResult> Acknowledge(Guid uuid, CancellationToken ct) => Done(await _svc.AcknowledgeReviewAsync(Caller, uuid, ct), "Review acknowledged.");
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Performance)]
[Route("api/hrm/goals")]
public sealed class GoalsController : HrmControllerBase
{
    private readonly IPerformanceService _svc;
    public GoalsController(ITenantContext tenant, IPerformanceService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List goals")]
    [HttpGet, RequirePermission(P.HRM_PERFORMANCE_VIEW, P.HRM_PERFORMANCE_MANAGE, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> List([FromQuery] Guid? cycleUuid, [FromQuery] Guid? employeeUuid, CancellationToken ct) => Done(await _svc.ListGoalsAsync(Caller, cycleUuid, employeeUuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a goal")]
    [HttpPost, RequirePermission(P.HRM_PERFORMANCE_MANAGE, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Create([FromBody] GoalUpsert req, CancellationToken ct) => Done(await _svc.CreateGoalAsync(Caller, req, ct), "Goal created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a goal or its rating")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_PERFORMANCE_MANAGE, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] GoalUpsert req, CancellationToken ct) => Done(await _svc.UpdateGoalAsync(Caller, uuid, req, ct), "Goal saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Delete a goal")]
    [HttpDelete("{uuid:guid}"), RequirePermission(P.HRM_PERFORMANCE_MANAGE, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Delete(Guid uuid, CancellationToken ct)
    {
        await _svc.DeleteGoalAsync(Caller, uuid, ct);
        return Done(true, "Goal removed.");
    }
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Performance)]
[Route("api/hrm/performance")]
public sealed class PerformanceDashboardController : HrmControllerBase
{
    private readonly IPerformanceService _svc;
    public PerformanceDashboardController(ITenantContext tenant, IPerformanceService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("Completion and rating distribution of a cycle")]
    [HttpGet("dashboard"), RequirePermission(P.HRM_PERFORMANCE_VIEW, P.HRM_PERFORMANCE_MANAGE)]
    public async Task<IActionResult> Dashboard([FromQuery] Guid cycleUuid, CancellationToken ct) => Done(await _svc.GetDashboardAsync(Caller, cycleUuid, ct));
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Training)]
[Route("api/hrm/courses")]
public sealed class CoursesController : HrmControllerBase
{
    private readonly ITrainingService _svc;
    public CoursesController(ITenantContext tenant, ITrainingService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List courses")]
    [HttpGet, RequirePermission(P.HRM_TRAINING_VIEW, P.HRM_TRAINING_MANAGE, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken ct) => Done(await _svc.ListCoursesAsync(Caller, includeInactive, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Get a course")]
    [HttpGet("{uuid:guid}"), RequirePermission(P.HRM_TRAINING_VIEW, P.HRM_TRAINING_MANAGE, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) => Done(await _svc.GetCourseAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a course")]
    [HttpPost, RequirePermission(P.HRM_TRAINING_MANAGE)]
    public async Task<IActionResult> Create([FromBody] CourseUpsert req, CancellationToken ct) => Done(await _svc.CreateCourseAsync(Caller, req, ct), "Course created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a course")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_TRAINING_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] CourseUpsert req, CancellationToken ct) => Done(await _svc.UpdateCourseAsync(Caller, uuid, req, ct), "Course saved.");
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Training)]
[Route("api/hrm/enrollments")]
public sealed class EnrollmentsController : HrmControllerBase
{
    private readonly ITrainingService _svc;
    public EnrollmentsController(ITenantContext tenant, ITrainingService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List course enrollments")]
    [HttpGet, RequirePermission(P.HRM_TRAINING_VIEW, P.HRM_TRAINING_MANAGE)]
    public async Task<IActionResult> List([FromQuery] Guid? courseUuid, [FromQuery] Guid? employeeUuid, CancellationToken ct) => Done(await _svc.ListEnrollmentsAsync(Caller, courseUuid, employeeUuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("The caller's own enrollments")]
    [HttpGet("me"), RequirePermission(P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Mine(CancellationToken ct) => Done(await _svc.ListOwnEnrollmentsAsync(Caller, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Enroll an employee in a course")]
    [HttpPost, RequirePermission(P.HRM_TRAINING_MANAGE)]
    public async Task<IActionResult> Enroll([FromBody] EnrollmentCreate req, CancellationToken ct) => Done(await _svc.EnrollAsync(Caller, req, ct), "Enrolled.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Mark an enrollment complete")]
    [HttpPost("{uuid:guid}/complete"), RequirePermission(P.HRM_TRAINING_MANAGE)]
    public async Task<IActionResult> Complete(Guid uuid, [FromBody] EnrollmentComplete req, CancellationToken ct) => Done(await _svc.CompleteAsync(Caller, uuid, req, ct), "Enrollment completed.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Cancel an enrollment")]
    [HttpPost("{uuid:guid}/cancel"), RequirePermission(P.HRM_TRAINING_MANAGE)]
    public async Task<IActionResult> Cancel(Guid uuid, CancellationToken ct) => Done(await _svc.CancelAsync(Caller, uuid, ct), "Enrollment cancelled.");
}
