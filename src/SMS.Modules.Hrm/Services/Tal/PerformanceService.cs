using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Authorization;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Hrm.Services.Tal;

public interface IPerformanceService
{
    Task<IReadOnlyList<ReviewCycleModel>> ListCyclesAsync(HrmCaller caller, CancellationToken ct = default);
    Task<ReviewCycleModel> GetCycleAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<ReviewCycleModel> CreateCycleAsync(HrmCaller caller, ReviewCycleUpsert request, CancellationToken ct = default);
    Task<ReviewCycleModel> UpdateCycleAsync(HrmCaller caller, Guid uuid, ReviewCycleUpsert request, CancellationToken ct = default);
    Task<ReviewCycleModel> OpenCycleAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<ReviewCycleModel> CloseCycleAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);

    Task<IReadOnlyList<ReviewModel>> ListReviewsAsync(HrmCaller caller, Guid? cycleUuid, string? status, CancellationToken ct = default);
    Task<IReadOnlyList<ReviewModel>> ListOwnReviewsAsync(HrmCaller caller, CancellationToken ct = default);
    /// <summary>Only the reviews where the caller's employee is the reviewer, whatever their HR scope (D-TAL-14).</summary>
    Task<IReadOnlyList<ReviewModel>> ListAssignedReviewsAsync(HrmCaller caller, CancellationToken ct = default);
    Task<ReviewModel> GetReviewAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<ReviewModel> UpdateReviewAsync(HrmCaller caller, Guid uuid, ReviewUpdate request, CancellationToken ct = default);
    Task<ReviewModel> SubmitReviewAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<ReviewModel> AcknowledgeReviewAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);

    Task<IReadOnlyList<GoalModel>> ListGoalsAsync(HrmCaller caller, Guid? cycleUuid, Guid? employeeUuid, CancellationToken ct = default);
    Task<GoalModel> CreateGoalAsync(HrmCaller caller, GoalUpsert request, CancellationToken ct = default);
    Task<GoalModel> UpdateGoalAsync(HrmCaller caller, Guid uuid, GoalUpsert request, CancellationToken ct = default);
    Task DeleteGoalAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);

    Task<PerformanceDashboard> GetDashboardAsync(HrmCaller caller, Guid cycleUuid, CancellationToken ct = default);
}

/// <summary>
/// Cycles, reviews, goals (D-TAL-5, 6). The manager side of a review is the reviewer, or a HRM_PERFORMANCE_MANAGE holder with the
/// employee in scope; nobody is on the manager side of their own review. The employee sets only self ratings and their comments,
/// and sees the manager's side only once the review is submitted.
/// </summary>
internal sealed class PerformanceService : HrmServiceBase, IPerformanceService
{
    private const int ListCap = 500;
    private const decimal MaxRating = 5m;

    private readonly IHrmScopeService _scope;
    private readonly IHrmEmployeeDirectory _directory;

    public PerformanceService(HrmDbContext db, IHrmScopeService scope, IHrmEmployeeDirectory directory) : base(db)
    {
        _scope = scope;
        _directory = directory;
    }

    /// <summary>Task 220: Σ(weight × managerRating) / Σ weight, 2 dp. Dropped goals do not count. Null when something is unrated or nothing has weight.</summary>
    internal static decimal? WeightedRating(IEnumerable<(decimal Weight, decimal? ManagerRating, string Status)> goals)
    {
        var live = goals.Where(g => g.Status != GoalStatuses.Dropped && g.Weight > 0).ToList();
        if (live.Count == 0 || live.Any(g => g.ManagerRating is null)) return null;
        return Math.Round(live.Sum(g => g.Weight * g.ManagerRating!.Value) / live.Sum(g => g.Weight), 2, MidpointRounding.AwayFromZero);
    }

    // ── Who ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    private IQueryable<int> ScopedEmployeeIds(HrmCaller caller, HrmScope scope) => _scope.Apply(Db.Employees, caller, scope).Select(e => e.Id);

    private IQueryable<PerformanceReview> Reviews(Guid org) => Db.PerformanceReviews.IgnoreQueryFilters().Where(r => r.OrganizationId == org);

    private static bool HoldsReviewCode(HrmCaller caller) => caller.HasAny(PermissionCodes.HRM_PERFORMANCE_VIEW, PermissionCodes.HRM_PERFORMANCE_MANAGE);

    /// <summary>On the manager's side of this employee's review? Never for one's own.</summary>
    private async Task<bool> IsManagerSideAsync(HrmCaller caller, int employeeId, int? reviewerId, int? ownId, CancellationToken ct)
    {
        if (ownId == employeeId) return false;
        if (reviewerId is not null && reviewerId == ownId) return true;
        return caller.Has(PermissionCodes.HRM_PERFORMANCE_MANAGE) && await _scope.CanAccessAsync(caller, employeeId, ct);
    }

    /// <summary>A review code + scope, the employee, or the reviewer. Anyone else: 404.</summary>
    private async Task<bool> CanReadAsync(HrmCaller caller, int employeeId, int? reviewerId, int? ownId, CancellationToken ct)
    {
        if (ownId == employeeId || (reviewerId is not null && reviewerId == ownId)) return true;
        return HoldsReviewCode(caller) && await _scope.CanAccessAsync(caller, employeeId, ct);
    }

    // ── Cycles ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<Dictionary<int, ReviewCompletion>> CompletionAsync(HrmCaller caller, CancellationToken ct)
    {
        var scope = await _scope.GetAsync(caller, ct);
        var employees = ScopedEmployeeIds(caller, scope);
        var rows = await Reviews(caller.OrganizationId).AsNoTracking().Where(r => employees.Contains(r.EmployeeId)).Select(r => new { r.ReviewCycleId, r.Status }).ToListAsync(ct);
        return rows.GroupBy(r => r.ReviewCycleId).ToDictionary(g => g.Key, g => new ReviewCompletion
        {
            Total = g.Count(), Submitted = g.Count(r => r.Status != ReviewStatuses.Draft), Acknowledged = g.Count(r => r.Status == ReviewStatuses.Acknowledged)
        });
    }

    private static ReviewCycleModel MapCycle(ReviewCycle c, IReadOnlyDictionary<int, ReviewCompletion> completion) => new()
    {
        Uuid = c.Uuid, Name = c.Name, Period = c.Period, StartDate = c.StartDate, EndDate = c.EndDate, Status = c.Status,
        Completion = completion.TryGetValue(c.Id, out var value) ? value : new ReviewCompletion()
    };

    public async Task<IReadOnlyList<ReviewCycleModel>> ListCyclesAsync(HrmCaller caller, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var completion = await CompletionAsync(caller, ct);
        var rows = await Db.ReviewCycles.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == org).OrderByDescending(c => c.StartDate).ThenByDescending(c => c.Id).ToListAsync(ct);
        return rows.Select(c => MapCycle(c, completion)).ToList();
    }

    private async Task<ReviewCycle> RequireCycleAsync(Guid org, Guid uuid, CancellationToken ct) =>
        await Db.ReviewCycles.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.OrganizationId == org && c.Uuid == uuid, ct) ?? throw new NotFoundException("Review cycle", uuid);

    public async Task<ReviewCycleModel> GetCycleAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default) =>
        MapCycle(await RequireCycleAsync(caller.OrganizationId, uuid, ct), await CompletionAsync(caller, ct));

    private static void ApplyCycle(ReviewCycle row, ReviewCycleUpsert r)
    {
        row.Name = HrmValidate.Name(r.Name, 100, "Name");
        row.Period = HrmValidate.OneOf(r.Period ?? ReviewPeriods.Annual, ReviewPeriods.All, "period");
        row.StartDate = r.StartDate ?? throw new BadRequestException("The start date is required.");
        row.EndDate = r.EndDate ?? throw new BadRequestException("The end date is required.");
        if (row.EndDate < row.StartDate) throw HrmErrors.Bad("CYCLE_DATES_INVALID", "The end date cannot be before the start date.");
    }

    public async Task<ReviewCycleModel> CreateCycleAsync(HrmCaller caller, ReviewCycleUpsert request, CancellationToken ct = default)
    {
        var row = new ReviewCycle();
        ApplyCycle(row, request);
        Stamp(row, caller, isNew: true);
        Db.ReviewCycles.Add(row);
        await SaveAsync(ct);
        return await GetCycleAsync(caller, row.Uuid, ct);
    }

    public async Task<ReviewCycleModel> UpdateCycleAsync(HrmCaller caller, Guid uuid, ReviewCycleUpsert request, CancellationToken ct = default)
    {
        var row = await RequireCycleAsync(caller.OrganizationId, uuid, ct);
        if (row.Status != ReviewCycleStatuses.Draft) throw HrmErrors.Conflict("CYCLE_NOT_DRAFT", "Only a draft cycle can be changed.");
        ApplyCycle(row, request);
        Stamp(row, caller);
        await SaveAsync(ct, "This review cycle");
        return await GetCycleAsync(caller, uuid, ct);
    }

    /// <summary>
    /// Creates a Draft review for everyone employed in the period (not Terminated), the current line manager as reviewer. Repeatable on an
    /// open cycle: it adds only who is missing, so someone hired later can be picked up. Runs for the whole organization by design:
    /// HRM_PERFORMANCE_MANAGE opens the cycle, and scope limits who then sees each review.
    /// </summary>
    public async Task<ReviewCycleModel> OpenCycleAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        await Db.InTransactionAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            var cycle = await RequireCycleAsync(org, uuid, ct);
            if (cycle.Status == ReviewCycleStatuses.Closed) throw HrmErrors.Conflict("CYCLE_CLOSED", "A closed cycle cannot be opened again.");
            var existing = await Reviews(org).AsNoTracking().Where(r => r.ReviewCycleId == cycle.Id).Select(r => r.EmployeeId).ToListAsync(ct);
            var employed = await _directory.GetEmployedInPeriodAsync(org, cycle.StartDate, cycle.EndDate, null, ct);
            foreach (var terms in employed.Where(t => t.Status != EmployeeStatuses.Terminated && !existing.Contains(t.EmployeeId)))
            {
                var review = new PerformanceReview { EmployeeId = terms.EmployeeId, ReviewCycleId = cycle.Id, ReviewerEmployeeId = terms.ManagerEmployeeId };
                Stamp(review, caller, isNew: true);
                Db.PerformanceReviews.Add(review);
            }
            cycle.Status = ReviewCycleStatuses.Open;
            Stamp(cycle, caller);
            await SaveAsync(ct, "This review cycle");
        }, ct);
        return await GetCycleAsync(caller, uuid, ct);
    }

    public async Task<ReviewCycleModel> CloseCycleAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await RequireCycleAsync(caller.OrganizationId, uuid, ct);
        if (row.Status != ReviewCycleStatuses.Open) throw HrmErrors.Conflict("CYCLE_NOT_OPEN", "Only an open cycle can be closed.");
        row.Status = ReviewCycleStatuses.Closed;
        Stamp(row, caller);
        await SaveAsync(ct, "This review cycle");
        return await GetCycleAsync(caller, uuid, ct);
    }

    // ── Reviews ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The manager's side stays hidden from the employee while the review is a draft.</summary>
    private static bool HidesManagerSide(int employeeId, string reviewStatus, int? ownId) => employeeId == ownId && reviewStatus == ReviewStatuses.Draft;

    private async Task<List<ReviewModel>> MapReviewsAsync(Guid org, IQueryable<PerformanceReview> query, int? ownId, CancellationToken ct)
    {
        var rows = await query.AsNoTracking().OrderByDescending(r => r.Id).Take(ListCap).Select(r => new
        {
            r.EmployeeId, r.ReviewCycleId,
            Model = new ReviewModel
            {
                Uuid = r.Uuid, EmployeeUuid = r.Employee!.Uuid, EmployeeName = r.Employee.FirstName + " " + r.Employee.LastName, CycleUuid = r.Cycle!.Uuid, CycleName = r.Cycle.Name,
                ReviewerUuid = r.Reviewer == null ? null : r.Reviewer.Uuid, ReviewerName = r.Reviewer == null ? null : r.Reviewer.FirstName + " " + r.Reviewer.LastName,
                OverallRating = r.OverallRating, Status = r.Status, ManagerComments = r.ManagerComments, EmployeeComments = r.EmployeeComments
            }
        }).ToListAsync(ct);

        var employeeIds = rows.Select(r => r.EmployeeId).Distinct().ToList();
        var cycleIds = rows.Select(r => r.ReviewCycleId).Distinct().ToList();
        var goals = await Db.Goals.IgnoreQueryFilters().AsNoTracking()
            .Where(g => g.OrganizationId == org && employeeIds.Contains(g.EmployeeId) && cycleIds.Contains(g.ReviewCycleId))
            .Select(g => new { g.EmployeeId, g.ReviewCycleId, Model = new GoalModel
            {
                Uuid = g.Uuid, EmployeeUuid = g.Employee!.Uuid, CycleUuid = g.Cycle!.Uuid, Title = g.Title, Description = g.Description, Weight = g.Weight,
                SelfRating = g.SelfRating, ManagerRating = g.ManagerRating, Status = g.Status
            } }).ToListAsync(ct);

        foreach (var row in rows)
        {
            var hide = HidesManagerSide(row.EmployeeId, row.Model.Status, ownId);
            row.Model.Goals = goals.Where(g => g.EmployeeId == row.EmployeeId && g.ReviewCycleId == row.ReviewCycleId).Select(g => g.Model).ToList();
            if (!hide) continue;
            row.Model.ManagerComments = null;
            row.Model.OverallRating = null;
            foreach (var goal in row.Model.Goals) goal.ManagerRating = null;
        }
        return rows.Select(r => r.Model).ToList();
    }

    public async Task<IReadOnlyList<ReviewModel>> ListReviewsAsync(HrmCaller caller, Guid? cycleUuid, string? status, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var scope = await _scope.GetAsync(caller, ct);
        var employees = ScopedEmployeeIds(caller, scope);
        var q = Reviews(org).Where(r => employees.Contains(r.EmployeeId));
        if (cycleUuid is { } cycle) q = q.Where(r => r.Cycle!.Uuid == cycle);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = HrmValidate.OneOf(status, ReviewStatuses.All, "status");
            q = q.Where(r => r.Status == s);
        }
        return await MapReviewsAsync(org, q, scope.OwnEmployeeId, ct);
    }

    public async Task<IReadOnlyList<ReviewModel>> ListOwnReviewsAsync(HrmCaller caller, CancellationToken ct = default)
    {
        var own = await _scope.RequireOwnEmployeeAsync(caller, ct);
        return await MapReviewsAsync(caller.OrganizationId, Reviews(caller.OrganizationId).Where(r => r.EmployeeId == own.Id), own.Id, ct);
    }

    public async Task<IReadOnlyList<ReviewModel>> ListAssignedReviewsAsync(HrmCaller caller, CancellationToken ct = default)
    {
        var own = await _scope.RequireOwnEmployeeAsync(caller, ct);
        return await MapReviewsAsync(caller.OrganizationId, Reviews(caller.OrganizationId).Where(r => r.ReviewerEmployeeId == own.Id), own.Id, ct);
    }

    private async Task<(PerformanceReview Review, int? OwnId)> RequireReviewAsync(HrmCaller caller, Guid uuid, CancellationToken ct)
    {
        var row = await Reviews(caller.OrganizationId).Include(r => r.Cycle).FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Review", uuid);
        var own = await _scope.OwnEmployeeIdAsync(caller, ct);
        if (!await CanReadAsync(caller, row.EmployeeId, row.ReviewerEmployeeId, own, ct)) throw new NotFoundException("Review", uuid);
        return (row, own);
    }

    private async Task<ReviewModel> MapOneAsync(HrmCaller caller, Guid uuid, int? ownId, CancellationToken ct) =>
        (await MapReviewsAsync(caller.OrganizationId, Reviews(caller.OrganizationId).Where(r => r.Uuid == uuid), ownId, ct)).First();

    public async Task<ReviewModel> GetReviewAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var (_, own) = await RequireReviewAsync(caller, uuid, ct);
        return await MapOneAsync(caller, uuid, own, ct);
    }

    /// <summary>The manager edits comments in Draft; the employee edits theirs until acknowledging. Fields of the other side are ignored, because the form sends the whole record.</summary>
    public async Task<ReviewModel> UpdateReviewAsync(HrmCaller caller, Guid uuid, ReviewUpdate request, CancellationToken ct = default)
    {
        var (row, own) = await RequireReviewAsync(caller, uuid, ct);
        if (row.Cycle!.Status == ReviewCycleStatuses.Closed) throw HrmErrors.Conflict("CYCLE_CLOSED", "This review cycle is closed.");
        if (row.Status == ReviewStatuses.Acknowledged) throw HrmErrors.Conflict("REVIEW_LOCKED", "An acknowledged review cannot be changed.");

        if (row.EmployeeId == own)
        {
            row.EmployeeComments = HrmValidate.Optional(request.EmployeeComments, 4000, "Comments");
        }
        else
        {
            if (!await IsManagerSideAsync(caller, row.EmployeeId, row.ReviewerEmployeeId, own, ct)) throw new ForbiddenException("Only the reviewer or HR can change this review.");
            if (row.Status != ReviewStatuses.Draft) throw HrmErrors.Conflict("REVIEW_LOCKED", "A submitted review cannot be changed.");
            row.ManagerComments = HrmValidate.Optional(request.ManagerComments, 4000, "Comments");
            if (request.ReviewerUuid is { } reviewer && reviewer != Guid.Empty)
            {
                caller.Require(PermissionCodes.HRM_PERFORMANCE_MANAGE, "Only HR can change the reviewer.");
                var next = await _scope.RequireEmployeeAsync(caller, reviewer, ct);
                if (next.Id == row.EmployeeId) throw new BadRequestException("An employee cannot review themselves.");
                row.ReviewerEmployeeId = next.Id;
            }
        }
        Stamp(row, caller);
        await SaveAsync(ct, "This review");
        return await MapOneAsync(caller, uuid, own, ct);
    }

    public async Task<ReviewModel> SubmitReviewAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var (row, own) = await RequireReviewAsync(caller, uuid, ct);
        if (!await IsManagerSideAsync(caller, row.EmployeeId, row.ReviewerEmployeeId, own, ct)) throw new ForbiddenException("Only the reviewer or HR can submit this review.");
        if (row.Status != ReviewStatuses.Draft) throw HrmErrors.Conflict("REVIEW_NOT_DRAFT", "This review has already been submitted.");
        if (row.Cycle!.Status != ReviewCycleStatuses.Open) throw HrmErrors.Conflict("CYCLE_NOT_OPEN", "This review cycle is not open.");

        var goals = await Db.Goals.IgnoreQueryFilters().AsNoTracking()
            .Where(g => g.OrganizationId == caller.OrganizationId && g.EmployeeId == row.EmployeeId && g.ReviewCycleId == row.ReviewCycleId)
            .Select(g => new { g.Weight, g.ManagerRating, g.Status }).ToListAsync(ct);
        row.OverallRating = WeightedRating(goals.Select(g => (g.Weight, g.ManagerRating, g.Status)))
            ?? throw HrmErrors.Conflict("REVIEW_GOALS_UNRATED", "Every goal with a weight needs a manager rating, and at least one goal needs a weight.");
        row.Status = ReviewStatuses.Submitted;
        row.SubmittedAt = DateTime.UtcNow;
        Stamp(row, caller);
        await SaveAsync(ct, "This review");
        return await MapOneAsync(caller, uuid, own, ct);
    }

    public async Task<ReviewModel> AcknowledgeReviewAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var (row, own) = await RequireReviewAsync(caller, uuid, ct);
        if (row.EmployeeId != own) throw new ForbiddenException("Only the employee can acknowledge their review.");
        if (row.Status != ReviewStatuses.Submitted) throw HrmErrors.Conflict("REVIEW_NOT_SUBMITTED", "Only a submitted review can be acknowledged.");
        row.Status = ReviewStatuses.Acknowledged;
        row.AcknowledgedAt = DateTime.UtcNow;
        Stamp(row, caller);
        await SaveAsync(ct, "This review");
        return await MapOneAsync(caller, uuid, own, ct);
    }

    // ── Goals ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static decimal? CheckRating(decimal? rating)
    {
        if (rating is < 0 or > MaxRating) throw HrmErrors.Bad("GOAL_RATING_INVALID", $"A rating is from 0 to {MaxRating:0}.");
        return rating;
    }

    public async Task<IReadOnlyList<GoalModel>> ListGoalsAsync(HrmCaller caller, Guid? cycleUuid, Guid? employeeUuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var scope = await _scope.GetAsync(caller, ct);
        var own = scope.OwnEmployeeId;
        var q = Db.Goals.IgnoreQueryFilters().AsNoTracking().Where(g => g.OrganizationId == org);
        if (HoldsReviewCode(caller))
        {
            var employees = ScopedEmployeeIds(caller, scope);
            q = q.Where(g => employees.Contains(g.EmployeeId));
        }
        else
        {
            // Self service: own goals and those of the people this caller reviews.
            var reviewed = Reviews(org).Where(r => own != null && r.ReviewerEmployeeId == own).Select(r => new { r.EmployeeId, r.ReviewCycleId });
            q = q.Where(g => g.EmployeeId == own || reviewed.Any(r => r.EmployeeId == g.EmployeeId && r.ReviewCycleId == g.ReviewCycleId));
        }
        if (cycleUuid is { } cycle) q = q.Where(g => g.Cycle!.Uuid == cycle);
        if (employeeUuid is { } employee) q = q.Where(g => g.Employee!.Uuid == employee);

        var rows = await q.OrderByDescending(g => g.Id).Take(ListCap).Select(g => new
        {
            g.EmployeeId, g.ReviewCycleId,
            Model = new GoalModel
            {
                Uuid = g.Uuid, EmployeeUuid = g.Employee!.Uuid, CycleUuid = g.Cycle!.Uuid, Title = g.Title, Description = g.Description, Weight = g.Weight,
                SelfRating = g.SelfRating, ManagerRating = g.ManagerRating, Status = g.Status
            }
        }).ToListAsync(ct);

        if (own is not null && rows.Any(r => r.EmployeeId == own))
        {
            var ownSubmitted = await Reviews(org).AsNoTracking().Where(r => r.EmployeeId == own && r.Status != ReviewStatuses.Draft).Select(r => r.ReviewCycleId).ToListAsync(ct);
            foreach (var row in rows.Where(r => r.EmployeeId == own && !ownSubmitted.Contains(r.ReviewCycleId))) row.Model.ManagerRating = null;
        }
        return rows.Select(r => r.Model).ToList();
    }

    /// <summary>The cycle is not closed and the employee's review, if there is one, is still a draft.</summary>
    private async Task<PerformanceReview?> CheckEditableAsync(Guid org, int employeeId, int cycleId, CancellationToken ct)
    {
        var status = await Db.ReviewCycles.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == org && c.Id == cycleId).Select(c => c.Status).FirstAsync(ct);
        if (status == ReviewCycleStatuses.Closed) throw HrmErrors.Conflict("CYCLE_CLOSED", "This review cycle is closed.");
        var review = await Reviews(org).AsNoTracking().FirstOrDefaultAsync(r => r.EmployeeId == employeeId && r.ReviewCycleId == cycleId, ct);
        if (review is not null && review.Status != ReviewStatuses.Draft) throw HrmErrors.Conflict("REVIEW_LOCKED", "The review is submitted: its goals can no longer change.");
        return review;
    }

    private static void ApplyGoalManagerSide(Goal row, GoalUpsert r)
    {
        row.Title = HrmValidate.Name(r.Title, 200, "Title");
        row.Description = HrmValidate.Optional(r.Description, 2000, "Description");
        var weight = r.Weight ?? throw HrmErrors.Bad("GOAL_WEIGHT_INVALID", "A goal needs a weight.");
        if (weight < 0) throw HrmErrors.Bad("GOAL_WEIGHT_INVALID", "A weight cannot be negative.");
        row.Weight = weight;
        row.ManagerRating = CheckRating(r.ManagerRating);
        row.Status = HrmValidate.OneOf(r.Status ?? row.Status, GoalStatuses.All, "status");
    }

    private async Task<GoalModel> MapGoalAsync(HrmCaller caller, Goal g, int? ownId, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        var employeeUuid = await Db.Employees.IgnoreQueryFilters().Where(e => e.OrganizationId == org && e.Id == g.EmployeeId).Select(e => e.Uuid).FirstAsync(ct);
        var cycleUuid = await Db.ReviewCycles.IgnoreQueryFilters().Where(c => c.OrganizationId == org && c.Id == g.ReviewCycleId).Select(c => c.Uuid).FirstAsync(ct);
        var status = await Reviews(org).Where(r => r.EmployeeId == g.EmployeeId && r.ReviewCycleId == g.ReviewCycleId).Select(r => r.Status).FirstOrDefaultAsync(ct);
        return new GoalModel
        {
            Uuid = g.Uuid, EmployeeUuid = employeeUuid, CycleUuid = cycleUuid, Title = g.Title, Description = g.Description, Weight = g.Weight, SelfRating = g.SelfRating,
            ManagerRating = HidesManagerSide(g.EmployeeId, status ?? ReviewStatuses.Draft, ownId) ? null : g.ManagerRating, Status = g.Status
        };
    }

    /// <summary>Only the manager's side sets a goal: its weight decides the rating, so an employee cannot add their own.</summary>
    public async Task<GoalModel> CreateGoalAsync(HrmCaller caller, GoalUpsert request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var employeeUuid = request.EmployeeUuid ?? throw new BadRequestException("Employee is required.");
        var cycleUuid = request.CycleUuid ?? throw new BadRequestException("Review cycle is required.");
        var employeeId = await Db.Employees.IgnoreQueryFilters().Where(e => e.OrganizationId == org && e.Uuid == employeeUuid && !e.IsDeleted).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Employee", employeeUuid);
        var cycle = await RequireCycleAsync(org, cycleUuid, ct);
        var review = await CheckEditableAsync(org, employeeId, cycle.Id, ct);
        var own = await _scope.OwnEmployeeIdAsync(caller, ct);
        if (!await CanReadAsync(caller, employeeId, review?.ReviewerEmployeeId, own, ct)) throw new NotFoundException("Employee", employeeUuid);
        if (!await IsManagerSideAsync(caller, employeeId, review?.ReviewerEmployeeId, own, ct)) throw new ForbiddenException("Only the reviewer or HR can set goals.");

        var row = new Goal { EmployeeId = employeeId, ReviewCycleId = cycle.Id };
        ApplyGoalManagerSide(row, request);
        Stamp(row, caller, isNew: true);
        Db.Goals.Add(row);
        await SaveAsync(ct);
        return await MapGoalAsync(caller, row, own, ct);
    }

    private async Task<(Goal Goal, PerformanceReview? Review, int? OwnId)> RequireGoalAsync(HrmCaller caller, Guid uuid, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        var row = await Db.Goals.IgnoreQueryFilters().FirstOrDefaultAsync(g => g.OrganizationId == org && g.Uuid == uuid, ct) ?? throw new NotFoundException("Goal", uuid);
        var review = await Reviews(org).AsNoTracking().FirstOrDefaultAsync(r => r.EmployeeId == row.EmployeeId && r.ReviewCycleId == row.ReviewCycleId, ct);
        var own = await _scope.OwnEmployeeIdAsync(caller, ct);
        if (!await CanReadAsync(caller, row.EmployeeId, review?.ReviewerEmployeeId, own, ct)) throw new NotFoundException("Goal", uuid);
        return (row, review, own);
    }

    /// <summary>The employee changes only the self rating; the manager's side everything but the self rating. The rest of the body is ignored.</summary>
    public async Task<GoalModel> UpdateGoalAsync(HrmCaller caller, Guid uuid, GoalUpsert request, CancellationToken ct = default)
    {
        var (row, review, own) = await RequireGoalAsync(caller, uuid, ct);
        await CheckEditableAsync(caller.OrganizationId, row.EmployeeId, row.ReviewCycleId, ct);
        if (row.EmployeeId == own)
            row.SelfRating = CheckRating(request.SelfRating);
        else if (await IsManagerSideAsync(caller, row.EmployeeId, review?.ReviewerEmployeeId, own, ct))
            ApplyGoalManagerSide(row, request);
        else
            throw new ForbiddenException("Only the employee, the reviewer or HR can change this goal.");
        Stamp(row, caller);
        await SaveAsync(ct, "This goal");
        return await MapGoalAsync(caller, row, own, ct);
    }

    public async Task DeleteGoalAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var (row, review, own) = await RequireGoalAsync(caller, uuid, ct);
        await CheckEditableAsync(caller.OrganizationId, row.EmployeeId, row.ReviewCycleId, ct);
        if (!await IsManagerSideAsync(caller, row.EmployeeId, review?.ReviewerEmployeeId, own, ct)) throw new ForbiddenException("Only the reviewer or HR can remove a goal.");
        Db.Goals.Remove(row);
        await SaveAsync(ct, "This goal");
    }

    // ── Dashboard ───────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<PerformanceDashboard> GetDashboardAsync(HrmCaller caller, Guid cycleUuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var cycle = await RequireCycleAsync(org, cycleUuid, ct);
        var scope = await _scope.GetAsync(caller, ct);
        var employees = ScopedEmployeeIds(caller, scope);
        var rows = await Reviews(org).AsNoTracking().Where(r => r.ReviewCycleId == cycle.Id && employees.Contains(r.EmployeeId)).Select(r => new { r.Status, r.OverallRating }).ToListAsync(ct);
        var completion = new ReviewCompletion
        {
            Total = rows.Count, Submitted = rows.Count(r => r.Status != ReviewStatuses.Draft), Acknowledged = rows.Count(r => r.Status == ReviewStatuses.Acknowledged)
        };
        var rated = rows.Where(r => r.Status != ReviewStatuses.Draft && r.OverallRating is not null).Select(r => (int)Math.Floor(r.OverallRating!.Value)).ToList();
        return new PerformanceDashboard
        {
            Completion = completion,
            CompletionPercent = completion.Total == 0 ? 0 : Math.Round(100m * completion.Submitted / completion.Total, 2, MidpointRounding.AwayFromZero),
            RatingDistribution = Enumerable.Range(0, 6).Select(n => new RatingBucket { Rating = n, Count = rated.Count(r => r == n) }).ToList()
        };
    }
}
