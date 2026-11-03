using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Hrm.Services.Life;

public interface IIncrementService
{
    Task<IReadOnlyList<IncrementModel>> ListAsync(HrmCaller caller, string? status, Guid? cycleUuid, CancellationToken ct = default);
    Task<IncrementModel> CreateAsync(HrmCaller caller, IncrementCreate request, CancellationToken ct = default);
    Task<IReadOnlyList<IncrementModel>> BulkAsync(HrmCaller caller, IncrementBulkRequest request, CancellationToken ct = default);
    Task<IncrementModel> ApproveAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<IncrementModel> RejectAsync(HrmCaller caller, Guid uuid, string? note, CancellationToken ct = default);
    Task<IncrementModel> ApplyAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
}

internal sealed class IncrementService : HrmServiceBase, IIncrementService
{
    private const string BasicCode = "BASIC";
    private readonly IHrmScopeService _scope;

    private readonly LifeUserNames? _names;

    public IncrementService(HrmDbContext db, IHrmScopeService scope, LifeUserNames? names = null) : base(db)
    {
        _scope = scope;
        _names = names;
    }

    private async Task<string?> NameAsync(HrmCaller caller, CancellationToken ct) => _names is null ? caller.UserName : await _names.NameAsync(caller.OrganizationId, caller.UserIdOrNull, caller.UserName, ct);

    private IQueryable<IncrementProposal> Query(Guid org) => Db.IncrementProposals.IgnoreQueryFilters().Include(i => i.Employee).Include(i => i.Review).Include(i => i.AppliedAssignment).Where(i => i.OrganizationId == org);

    private Task<decimal?> BasicAsync(Guid org, int employeeId, DateOnly on, CancellationToken ct) => Db.SalaryAssignments.IgnoreQueryFilters().AsNoTracking()
        .Where(a => a.OrganizationId == org && a.EmployeeId == employeeId && a.ApprovalStatus == SalaryAssignmentStatuses.Approved && a.EffectiveFrom <= on && (a.EffectiveTo == null || a.EffectiveTo >= on))
        .SelectMany(a => a.Lines).Where(l => l.Component!.Code == BasicCode).Select(l => (decimal?)l.Amount).FirstOrDefaultAsync(ct);

    private async Task<IncrementModel> MapAsync(Guid org, IncrementProposal p, CancellationToken ct) => new()
    {
        Uuid = p.Uuid, EmployeeUuid = p.Employee!.Uuid, EmployeeName = p.Employee.FullName, CurrentBasic = await BasicAsync(org, p.EmployeeId, p.EffectiveDate.AddDays(-1), ct), NewBasic = p.NewBasic, Percent = p.Percent,
        EffectiveDate = p.EffectiveDate, ReviewUuid = p.Review?.Uuid, Status = p.Status, Reason = p.Reason, DecidedByName = p.DecidedByName, AppliedAssignmentUuid = p.AppliedAssignment?.Uuid
    };

    private async Task<IncrementProposal> RequireAsync(HrmCaller caller, Guid uuid, CancellationToken ct)
    {
        var row = await Query(caller.OrganizationId).FirstOrDefaultAsync(i => i.Uuid == uuid, ct) ?? throw new NotFoundException("Increment", uuid);
        if (!await _scope.CanAccessAsync(caller, row.EmployeeId, ct)) throw new NotFoundException("Increment", uuid);
        return row;
    }

    public async Task<IReadOnlyList<IncrementModel>> ListAsync(HrmCaller caller, string? status, Guid? cycleUuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var scope = await _scope.GetAsync(caller, ct);
        var people = _scope.Apply(Db.Employees, caller, scope).Select(e => e.Id);
        var query = Query(org).AsNoTracking().Where(i => people.Contains(i.EmployeeId));
        if (!string.IsNullOrWhiteSpace(status)) { var s = HrmValidate.OneOf(status, IncrementStatuses.All, "status"); query = query.Where(i => i.Status == s); }
        if (cycleUuid is { } c) query = query.Where(i => i.Review!.Cycle!.Uuid == c);
        var rows = await query.OrderByDescending(i => i.Id).ToListAsync(ct);
        var result = new List<IncrementModel>();
        foreach (var row in rows) result.Add(await MapAsync(org, row, ct));
        return result;
    }

    public async Task<IncrementModel> CreateAsync(HrmCaller caller, IncrementCreate request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var employee = await _scope.RequireEmployeeAsync(caller, request.EmployeeUuid, ct);
        if ((request.NewBasic is null) == (request.Percent is null)) throw HrmErrors.Bad("INCREMENT_VALUE_REQUIRED", "Give either the new basic salary or a percent.");
        if (request.NewBasic is <= 0 || request.Percent is <= -100) throw HrmErrors.Bad("INCREMENT_VALUE_REQUIRED", "The new salary must be positive.");
        if (request.EffectiveDate == default) throw new BadRequestException("The effective date is required.");
        var row = new IncrementProposal
        {
            EmployeeId = employee.Id, NewBasic = request.NewBasic, Percent = request.Percent, EffectiveDate = request.EffectiveDate,
            ReviewId = await OptionalIdAsync(Db.PerformanceReviews, org, request.ReviewUuid, "Review", ct), Reason = HrmValidate.Optional(request.Reason, 500, "Reason")
        };
        Stamp(row, caller, isNew: true);
        Db.IncrementProposals.Add(row);
        await SaveAsync(ct);
        return await MapAsync(org, await RequireAsync(caller, row.Uuid, ct), ct);
    }

    /// <summary>One proposal per submitted review whose rounded rating is in the map. Repeating it adds none for a review that already has one.</summary>
    public async Task<IReadOnlyList<IncrementModel>> BulkAsync(HrmCaller caller, IncrementBulkRequest request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var scope = await _scope.GetAsync(caller, ct);
        var people = _scope.Apply(Db.Employees, caller, scope).Select(e => e.Id);
        var today = HrmValidate.Today;
        var effective = request.EffectiveDate ?? new DateOnly(today.Year, today.Month, 1).AddMonths(1);
        var reviews = await Db.PerformanceReviews.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && r.Cycle!.Uuid == request.ReviewCycleUuid && r.OverallRating != null && r.Status != ReviewStatuses.Draft && people.Contains(r.EmployeeId))
            .Select(r => new { r.Id, r.EmployeeId, r.OverallRating }).ToListAsync(ct);
        var done = await Db.IncrementProposals.IgnoreQueryFilters().AsNoTracking().Where(i => i.OrganizationId == org && i.ReviewId != null && i.Status != IncrementStatuses.Rejected).Select(i => i.ReviewId!.Value).ToListAsync(ct);
        var added = new List<IncrementProposal>();
        foreach (var review in reviews.Where(r => !done.Contains(r.Id)))
        {
            var key = Math.Round(review.OverallRating!.Value, 0, MidpointRounding.AwayFromZero).ToString("0");
            if (!request.PercentByRating.TryGetValue(key, out var percent) || percent <= -100) continue;
            var row = new IncrementProposal { EmployeeId = review.EmployeeId, ReviewId = review.Id, Percent = percent, EffectiveDate = effective, Reason = $"Review rating {key}" };
            Stamp(row, caller, isNew: true);
            Db.IncrementProposals.Add(row);
            added.Add(row);
        }
        await SaveAsync(ct);
        var result = new List<IncrementModel>();
        foreach (var row in added) result.Add(await MapAsync(org, await RequireAsync(caller, row.Uuid, ct), ct));
        return result;
    }

    private async Task<IncrementProposal> DecideAsync(HrmCaller caller, Guid uuid, string status, string? note, CancellationToken ct)
    {
        var row = await RequireAsync(caller, uuid, ct);
        if (await _scope.OwnEmployeeIdAsync(caller, ct) == row.EmployeeId) throw new ForbiddenException("You cannot decide your own increment.");
        if (row.Status != IncrementStatuses.Draft) throw HrmErrors.Conflict("INCREMENT_NOT_DRAFT", "Only a draft proposal can be decided.");
        row.Status = status;
        row.DecidedBy = caller.UserIdOrNull;
        row.DecidedByName = await NameAsync(caller, ct);
        row.DecidedAt = DateTime.UtcNow;
        if (HrmValidate.Optional(note, 500, "Note") is { } text) row.Reason = text;
        Stamp(row, caller);
        await SaveAsync(ct, "This proposal");
        return row;
    }

    public async Task<IncrementModel> ApproveAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default) =>
        await MapAsync(caller.OrganizationId, await DecideAsync(caller, uuid, IncrementStatuses.Approved, null, ct), ct);

    public async Task<IncrementModel> RejectAsync(HrmCaller caller, Guid uuid, string? note, CancellationToken ct = default) =>
        await MapAsync(caller.OrganizationId, await DecideAsync(caller, uuid, IncrementStatuses.Rejected, note, ct), ct);

    /// <summary>A new Approved assignment copying the one in force with BASIC changed; the old one is closed at start − 1 and Superseded (task 368). Atomic.</summary>
    public async Task<IncrementModel> ApplyAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var row = await RequireAsync(caller, uuid, ct);
        if (row.Status != IncrementStatuses.Approved) throw HrmErrors.Conflict("INCREMENT_NOT_APPROVED", "Only an approved proposal can be applied.");

        var approverName = await NameAsync(caller, ct);
        await Db.InTransactionAsync(async () =>
        {
            var on = row.EffectiveDate;
            var current = await Db.SalaryAssignments.IgnoreQueryFilters().Include(a => a.Lines).ThenInclude(l => l.Component)
                .Where(a => a.OrganizationId == org && a.EmployeeId == row.EmployeeId && a.ApprovalStatus == SalaryAssignmentStatuses.Approved && a.EffectiveFrom <= on && (a.EffectiveTo == null || a.EffectiveTo >= on))
                .FirstOrDefaultAsync(ct) ?? throw HrmErrors.Conflict("INCREMENT_NO_SALARY", "This employee has no approved salary on the effective date.");
            if (current.EffectiveFrom >= on) throw HrmErrors.Conflict("SALARY_OVERLAP", "A salary already starts on or after this date.");
            if (await Db.SalaryAssignments.IgnoreQueryFilters().AnyAsync(a => a.OrganizationId == org && a.EmployeeId == row.EmployeeId && a.ApprovalStatus == SalaryAssignmentStatuses.Approved && a.EffectiveFrom > on, ct))
                throw HrmErrors.Conflict("SALARY_OVERLAP", "A salary already starts after this date.");
            var basicLine = current.Lines.FirstOrDefault(l => l.Component!.Code == BasicCode) ?? throw HrmErrors.Conflict("INCREMENT_NO_SALARY", "The current salary has no BASIC line.");
            var newBasic = row.NewBasic ?? LifeMoney.Round(basicLine.Amount * (1m + row.Percent!.Value / 100m));

            current.EffectiveTo = on.AddDays(-1);
            current.ApprovalStatus = SalaryAssignmentStatuses.Superseded;
            Stamp(current, caller);
            await SaveAsync(ct, "The current salary");

            var next = new SalaryAssignment
            {
                EmployeeId = row.EmployeeId, StructureId = current.StructureId, EffectiveFrom = on, ApprovalStatus = SalaryAssignmentStatuses.Approved, ChangeReason = row.Reason ?? "Increment",
                ApprovedBy = caller.UserIdOrNull, ApprovedByName = approverName, ApprovedAt = DateTime.UtcNow, IncrementProposalId = row.Id,
                Lines = current.Lines.Select(l => new SalaryAssignmentLine { OrganizationId = org, ComponentId = l.ComponentId, Amount = l.Id == basicLine.Id ? newBasic : l.Amount }).ToList()
            };
            Stamp(next, caller, isNew: true);
            foreach (var l in next.Lines) Stamp(l, caller, isNew: true);
            Db.SalaryAssignments.Add(next);
            await SaveAsync(ct, "The salary");

            row.AppliedAssignmentId = next.Id;
            row.Status = IncrementStatuses.Applied;
            row.NewBasic = newBasic;
            Stamp(row, caller);
            await SaveAsync(ct, "This proposal"); // RowVersion: of two concurrent applies one rolls back whole.
        }, ct);
        return await MapAsync(org, await RequireAsync(caller, uuid, ct), ct);
    }
}




