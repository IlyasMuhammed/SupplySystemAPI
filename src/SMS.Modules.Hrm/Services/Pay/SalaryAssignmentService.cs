using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Hrm.Services.Pay;

public interface ISalaryAssignmentService
{
    Task<IReadOnlyList<SalaryAssignmentModel>> ListAsync(HrmCaller caller, Guid employeeUuid, CancellationToken ct = default);
    /// <summary>Task 189 — the approved assignment in force today, or null.</summary>
    Task<SalaryAssignmentModel?> GetCurrentAsync(HrmCaller caller, Guid employeeUuid, CancellationToken ct = default);
    Task<SalaryAssignmentModel> CreateAsync(HrmCaller caller, Guid employeeUuid, SalaryAssignmentCreate request, CancellationToken ct = default);
    Task<SalaryAssignmentModel> SubmitAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<SalaryAssignmentModel> ApproveAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<SalaryAssignmentModel> RejectAsync(HrmCaller caller, Guid uuid, string? note, CancellationToken ct = default);
    Task<IReadOnlyList<SalaryAssignmentModel>> BulkRevisionAsync(HrmCaller caller, BulkRevisionRequest request, CancellationToken ct = default);
}

/// <summary>
/// S2, effective-dated: every change is a new row. Draft → PendingApproval → Approved (the previous one closes at start − 1 and becomes
/// Superseded) or Rejected. A superseded row is still history a recalculation of its own month needs, so <see cref="PayrollAssignments.Payable"/>
/// counts both statuses and lets the dates decide.
/// </summary>
internal sealed class SalaryAssignmentService : HrmServiceBase, ISalaryAssignmentService
{
    private readonly IHrmScopeService _scope;
    private readonly IHrmApprovalGateway _approvals;
    private readonly SalaryDecisions _decisions;
    private readonly SMS.WorkflowEngine.Services.IWorkflowActionService? _workflow;

    public SalaryAssignmentService(HrmDbContext db, IHrmScopeService scope, IHrmApprovalGateway approvals, SalaryDecisions decisions,
        SMS.WorkflowEngine.Services.IWorkflowActionService? workflow = null) : base(db)
    {
        _scope = scope;
        _approvals = approvals;
        _decisions = decisions;
        _workflow = workflow;
    }

    /// <summary>
    /// REV49-3: a decision on the salary screen does not pass through the engine, so the SALARY_CHANGE approval it opened at submit
    /// would stay pending in the approver's inbox for ever. It is closed here; the handler ignores the callback (the row is no longer pending).
    /// Best effort: the decision is already saved, and an engine refusal (already closed) changes nothing.
    /// </summary>
    private async Task CloseApprovalAsync(SalaryAssignment row, HrmCaller caller, string reason)
    {
        if (_workflow is null || row.ApprovalUuid is not { } approval || caller.UserId <= 0) return;
        try
        {
            await _workflow.CancelAsync(new SMS.WorkflowEngine.Models.CancelCommand { ApprovalUUID = approval, CancelledBy = caller.UserId, CancellationReason = reason });
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    private IQueryable<SalaryAssignment> Query(Guid org) => Db.SalaryAssignments.IgnoreQueryFilters()
        .Include(a => a.Employee).Include(a => a.Structure).Include(a => a.Lines).ThenInclude(l => l.Component)
        .Where(a => a.OrganizationId == org);

    internal static SalaryAssignmentModel Map(SalaryAssignment a) => new()
    {
        Uuid = a.Uuid, EmployeeUuid = a.Employee!.Uuid, EmployeeName = a.Employee.FullName, StructureUuid = a.Structure!.Uuid, StructureName = a.Structure.Name,
        EffectiveFrom = a.EffectiveFrom, EffectiveTo = a.EffectiveTo, ApprovalStatus = a.ApprovalStatus, ChangeReason = a.ChangeReason,
        ApprovedByName = a.ApprovedByName, ApprovedAt = a.ApprovedAt,
        Lines = a.Lines.Select(l => new SalaryAssignmentLineModel { ComponentUuid = l.Component!.Uuid, ComponentCode = l.Component.Code, Amount = l.Amount }).OrderBy(l => l.ComponentCode).ToList(),
        Gross = a.Lines.Where(l => l.Component!.Type == SalaryComponentTypes.Earning && l.Component.InGross).Sum(l => l.Amount)
    };

    /// <summary>Own salary with self-service; anyone else's needs HRM_SALARY_VIEW and the HR scope (404 outside it).</summary>
    private async Task<Employee> ReadableAsync(HrmCaller caller, Guid employeeUuid, CancellationToken ct)
    {
        if (caller.Has(PermissionCodes.HRM_SALARY_VIEW)) return await _scope.RequireEmployeeAsync(caller, employeeUuid, ct);
        var own = await _scope.RequireOwnEmployeeAsync(caller, ct);
        if (own.Uuid != employeeUuid) throw new ForbiddenException();
        return own;
    }

    public async Task<IReadOnlyList<SalaryAssignmentModel>> ListAsync(HrmCaller caller, Guid employeeUuid, CancellationToken ct = default)
    {
        var employee = await ReadableAsync(caller, employeeUuid, ct);
        var rows = await Query(caller.OrganizationId).AsNoTracking().Where(a => a.EmployeeId == employee.Id).OrderByDescending(a => a.EffectiveFrom).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<SalaryAssignmentModel?> GetCurrentAsync(HrmCaller caller, Guid employeeUuid, CancellationToken ct = default)
    {
        var employee = await ReadableAsync(caller, employeeUuid, ct);
        var today = HrmValidate.Today;
        var row = await Query(caller.OrganizationId).AsNoTracking()
            .Where(a => a.EmployeeId == employee.Id && PayrollAssignments.PayableStatuses.Contains(a.ApprovalStatus) && a.EffectiveFrom <= today && (a.EffectiveTo == null || a.EffectiveTo >= today))
            .OrderByDescending(a => a.EffectiveFrom).FirstOrDefaultAsync(ct);
        return row is null ? null : Map(row);
    }

    public async Task<SalaryAssignmentModel> CreateAsync(HrmCaller caller, Guid employeeUuid, SalaryAssignmentCreate request, CancellationToken ct = default)
    {
        var employee = await _scope.RequireEmployeeAsync(caller, employeeUuid, ct);
        var org = caller.OrganizationId;
        var structure = await Db.SalaryStructures.IgnoreQueryFilters().Include(s => s.Lines).ThenInclude(l => l.Component)
            .FirstOrDefaultAsync(s => s.OrganizationId == org && s.Uuid == request.StructureUuid, ct)
            ?? throw new NotFoundException("Salary structure", request.StructureUuid);
        if (!structure.IsActive) throw new BadRequestException("This salary structure is inactive.");
        if (request.Lines.Count == 0) throw new BadRequestException("An assignment needs at least one amount.");
        if (request.Lines.Select(l => l.ComponentUuid).Distinct().Count() != request.Lines.Count) throw new BadRequestException("A component can appear once.");

        await _decisions.EnsureNoOverlapAsync(org, employee.Id, request.EffectiveFrom, null, ct);

        var row = new SalaryAssignment
        {
            EmployeeId = employee.Id, StructureId = structure.Id, EffectiveFrom = request.EffectiveFrom,
            ChangeReason = HrmValidate.Optional(request.ChangeReason, 500, "Reason"), ApprovalStatus = SalaryAssignmentStatuses.Draft
        };
        Stamp(row, caller, isNew: true);
        foreach (var l in request.Lines)
        {
            var structureLine = structure.Lines.FirstOrDefault(s => s.Component!.Uuid == l.ComponentUuid)
                ?? throw new BadRequestException("A salary amount names a component that is not in the structure.");
            if (l.Amount < 0m) throw new BadRequestException("A salary amount cannot be negative.");
            var line = new SalaryAssignmentLine { ComponentId = structureLine.ComponentId, Amount = Money(l.Amount) };
            Stamp(line, caller, isNew: true);
            row.Lines.Add(line);
        }
        var gross = row.Lines.Where(l => structure.Lines.First(s => s.ComponentId == l.ComponentId).Component is { Type: SalaryComponentTypes.Earning, InGross: true }).Sum(l => l.Amount);
        await _decisions.CheckGradeBandAsync(org, employee.Id, request.EffectiveFrom, gross, ct);

        Db.SalaryAssignments.Add(row);
        await Db.SaveChangesAsync(ct);
        return Map(await Query(org).AsNoTracking().FirstAsync(a => a.Id == row.Id, ct));
    }

    public async Task<SalaryAssignmentModel> SubmitAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await RequireAsync(caller, uuid, ct);
        if (row.ApprovalStatus != SalaryAssignmentStatuses.Draft) throw HrmErrors.Conflict("SALARY_NOT_DRAFT", "Only a draft can be submitted.");
        var reference = $"SAL-{row.Employee!.EmployeeNumber}-{row.EffectiveFrom:yyyyMMdd}";
        // Once the engine holds the approval, linking it to the row must not be cancelled by a disconnecting caller (it would leave an approval
        // nobody can tie to the assignment, and a Draft that can be submitted again). So the submit and the save run to the end.
        var finish = CancellationToken.None;
        var approval = await _approvals.SubmitAsync(caller, HrmWorkflowCodes.SalaryChange, row.Uuid, reference, row.EmployeeId, PermissionCodes.HRM_SALARY_APPROVE, null, finish);
        row.ApprovalUuid = approval;
        row.ApprovalStatus = SalaryAssignmentStatuses.PendingApproval;
        Stamp(row, caller);
        await SaveAsync(finish, "This salary assignment");
        return Map(row);
    }

    public async Task<SalaryAssignmentModel> ApproveAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await RequireAsync(caller, uuid, ct);
        // Nobody approves their own pay, whichever route they take.
        if (await _scope.OwnEmployeeIdAsync(caller, ct) == row.EmployeeId) throw new ForbiddenException("You cannot approve your own salary.");
        if (row.ApprovalStatus != SalaryAssignmentStatuses.PendingApproval) throw HrmErrors.Conflict("SALARY_NOT_PENDING", "Only a salary change waiting for approval can be approved.");
        await _decisions.ApproveAsync(row, caller, ct);
        await CloseApprovalAsync(row, caller, "Approved on the salary screen.");
        return Map(row);
    }

    public async Task<SalaryAssignmentModel> RejectAsync(HrmCaller caller, Guid uuid, string? note, CancellationToken ct = default)
    {
        var row = await RequireAsync(caller, uuid, ct);
        if (row.ApprovalStatus != SalaryAssignmentStatuses.PendingApproval) throw HrmErrors.Conflict("SALARY_NOT_PENDING", "Only a salary change waiting for approval can be rejected.");
        row.ApprovalStatus = SalaryAssignmentStatuses.Rejected;
        if (!string.IsNullOrWhiteSpace(note)) row.ChangeReason = HrmValidate.Optional($"{row.ChangeReason} Rejected: {note}".Trim(), 500, "Reason");
        Stamp(row, caller);
        await SaveAsync(ct, "This salary assignment");
        await CloseApprovalAsync(row, caller, "Rejected on the salary screen.");
        return Map(row);
    }

    public async Task<IReadOnlyList<SalaryAssignmentModel>> BulkRevisionAsync(HrmCaller caller, BulkRevisionRequest request, CancellationToken ct = default)
    {
        if ((request.Percent is null) == (request.Amount is null)) throw new BadRequestException("Give a percent or an amount, not both.");
        var code = HrmValidate.Code(request.ComponentCode);
        if (request.EmployeeUuids.Count == 0) throw new BadRequestException("Choose at least one employee.");
        var org = caller.OrganizationId;
        var results = new List<SalaryAssignmentModel>();
        await Db.InTransactionAsync(async () =>
        {
            results.Clear();
            Db.ChangeTracker.Clear();
            foreach (var employeeUuid in request.EmployeeUuids.Distinct())
            {
                var employee = await _scope.RequireEmployeeAsync(caller, employeeUuid, ct);
                var current = await Query(org).AsNoTracking()
                    .Where(a => a.EmployeeId == employee.Id && a.ApprovalStatus == SalaryAssignmentStatuses.Approved && a.EffectiveFrom < request.EffectiveFrom && (a.EffectiveTo == null || a.EffectiveTo >= request.EffectiveFrom))
                    .OrderByDescending(a => a.EffectiveFrom).FirstOrDefaultAsync(ct)
                    ?? throw HrmErrors.Conflict("SALARY_NONE", $"{employee.EmployeeNumber} has no approved salary to revise.");
                if (!current.Lines.Any(l => l.Component!.Code == code)) throw HrmErrors.Bad("SALARY_COMPONENT_MISSING", $"{employee.EmployeeNumber}'s salary has no {code}.");
                await _decisions.EnsureNoOverlapAsync(org, employee.Id, request.EffectiveFrom, null, ct);

                var row = new SalaryAssignment
                {
                    EmployeeId = employee.Id, StructureId = current.StructureId, EffectiveFrom = request.EffectiveFrom,
                    ChangeReason = HrmValidate.Optional(request.Reason, 500, "Reason"), ApprovalStatus = SalaryAssignmentStatuses.Draft
                };
                Stamp(row, caller, isNew: true);
                foreach (var l in current.Lines)
                {
                    var amount = l.Component!.Code != code ? l.Amount : request.Percent is { } p ? Money(l.Amount + l.Amount * p / 100m) : Money(l.Amount + request.Amount!.Value);
                    if (amount < 0m) throw new BadRequestException($"{employee.EmployeeNumber}'s {code} would go below zero.");
                    var line = new SalaryAssignmentLine { ComponentId = l.ComponentId, Amount = amount };
                    Stamp(line, caller, isNew: true);
                    row.Lines.Add(line);
                }
                Db.SalaryAssignments.Add(row);
                await Db.SaveChangesAsync(ct);
                results.Add(Map(await Query(org).AsNoTracking().FirstAsync(a => a.Id == row.Id, ct)));
            }
        }, ct);
        return results;
    }

    private static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private async Task<SalaryAssignment> RequireAsync(HrmCaller caller, Guid uuid, CancellationToken ct)
    {
        var row = await Query(caller.OrganizationId).FirstOrDefaultAsync(a => a.Uuid == uuid, ct) ?? throw new NotFoundException("Salary assignment", uuid);
        if (!await _scope.CanAccessAsync(caller, row.EmployeeId, ct)) throw new NotFoundException("Salary assignment", uuid);
        return row;
    }

}

/// <summary>
/// The state change of an approval, with no way to the workflow engine. The engine's status handlers are built by the engine's own services, so a handler that reached
/// IHrmApprovalGateway would close a DI cycle (IWorkflowActionService -> IDocumentStatusService -> handlers -> gateway -> IWorkflowActionService) and stop the host.
/// </summary>
internal sealed class SalaryDecisions : HrmServiceBase
{
    public SalaryDecisions(HrmDbContext db) : base(db) { }

    /// <summary>Used by the route and the workflow handler. Atomic, and the row's RowVersion makes a concurrent decision lose with a 409.</summary>
    internal async Task ApproveAsync(SalaryAssignment row, HrmCaller caller, CancellationToken ct)
    {
        var org = row.OrganizationId;
        await EnsureNoOverlapAsync(org, row.EmployeeId, row.EffectiveFrom, row.Id, ct);
        var gross = row.Lines.Where(l => l.Component is { Type: SalaryComponentTypes.Earning, InGross: true }).Sum(l => l.Amount);
        await CheckGradeBandAsync(org, row.EmployeeId, row.EffectiveFrom, gross, ct);

        var previous = await Db.SalaryAssignments.IgnoreQueryFilters()
            .Where(a => a.OrganizationId == org && a.EmployeeId == row.EmployeeId && a.Id != row.Id && a.ApprovalStatus == SalaryAssignmentStatuses.Approved && a.EffectiveFrom < row.EffectiveFrom)
            .ToListAsync(ct);
        foreach (var p in previous)
        {
            if (p.EffectiveTo is null || p.EffectiveTo >= row.EffectiveFrom) p.EffectiveTo = row.EffectiveFrom.AddDays(-1);
            p.ApprovalStatus = SalaryAssignmentStatuses.Superseded;
            Stamp(p, caller);
        }
        row.ApprovalStatus = SalaryAssignmentStatuses.Approved;
        row.ApprovedBy = caller.UserIdOrNull;
        row.ApprovedByName = caller.UserName;
        row.ApprovedAt = DateTime.UtcNow;
        Stamp(row, caller);
        await SaveAsync(ct, "This salary assignment");
    }

    /// <summary>A live row on the same date, or an approved one starting on or after it: dates would interleave.</summary>
    internal async Task EnsureNoOverlapAsync(Guid org, int employeeId, DateOnly from, int? self, CancellationToken ct)
    {
        var clash = await Db.SalaryAssignments.IgnoreQueryFilters().AsNoTracking().AnyAsync(a => a.OrganizationId == org && a.EmployeeId == employeeId && a.Id != (self ?? -1)
            && ((a.EffectiveFrom == from && a.ApprovalStatus != SalaryAssignmentStatuses.Rejected)
                || ((a.ApprovalStatus == SalaryAssignmentStatuses.Approved || a.ApprovalStatus == SalaryAssignmentStatuses.Superseded) && a.EffectiveFrom >= from)), ct);
        if (clash) throw HrmErrors.Conflict("SALARY_OVERLAP", "This employee already has a salary starting on or after that date.");
    }

    internal async Task CheckGradeBandAsync(Guid org, int employeeId, DateOnly on, decimal gross, CancellationToken ct)
    {
        var grade = await Db.EmploymentRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && r.EmployeeId == employeeId && r.EffectiveFrom <= on && (r.EffectiveTo == null || r.EffectiveTo >= on))
            .Select(r => r.Grade).FirstOrDefaultAsync(ct);
        if (grade is null) return;
        if (grade.MinSalary is { } min && gross < min) throw HrmErrors.Bad("SALARY_BELOW_GRADE_MIN", $"The gross is below the band of grade {grade.Code}.");
        if (grade.MaxSalary is { } max && gross > max) throw HrmErrors.Bad("SALARY_ABOVE_GRADE_MAX", $"The gross is above the band of grade {grade.Code}.");
    }
}

/// <summary>The statuses payroll reads; the dates pick the row. Superseded rows were approved, and still answer for their own months.</summary>
internal static class PayrollAssignments
{
    public static readonly string[] PayableStatuses = [SalaryAssignmentStatuses.Approved, SalaryAssignmentStatuses.Superseded];
}

/// <summary>E-15 — workflow SALARY_CHANGE. The engine has only its tenant: the row's own organization is used, and the actor is the workflow.</summary>
internal sealed class SalaryChangeStatusHandler : IDocumentStatusHandler
{
    private readonly HrmDbContext _db;
    private readonly SalaryDecisions _decisions;
    private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? _http;
    private readonly HrmUserNames? _names;

    public SalaryChangeStatusHandler(HrmDbContext db, SalaryDecisions decisions, Microsoft.AspNetCore.Http.IHttpContextAccessor? http = null, HrmUserNames? names = null)
    {
        _db = db;
        _decisions = decisions;
        _http = http;
        _names = names;
    }

    public string InterfaceCode => HrmWorkflowCodes.SalaryChange;

    public async Task<string?> GetStatusAsync(Guid documentId) =>
        await _db.SalaryAssignments.IgnoreQueryFilters().AsNoTracking().Where(a => a.Uuid == documentId).Select(a => a.ApprovalStatus).FirstOrDefaultAsync();

    public async Task UpdateStatusAsync(Guid documentId, string newStatus)
    {
        var row = await _db.SalaryAssignments.IgnoreQueryFilters().Include(a => a.Lines).ThenInclude(l => l.Component)
            .FirstOrDefaultAsync(a => a.Uuid == documentId);
        if (row is null) return;
        // The decider when the decision came through a request (the inbox); an engine job has none and the actor is the workflow.
        var user = _http?.HttpContext?.User;
        var by = user?.Identity?.IsAuthenticated == true ? user.GetUserId() : 0;
        var name = by > 0 && _names is not null ? await _names.NameAsync(row.OrganizationId, by) : null;
        var caller = new HrmCaller(row.OrganizationId, by, [], name ?? "Workflow");
        switch (newStatus.Trim().ToUpperInvariant())
        {
            case "APPROVED" when row.ApprovalStatus == SalaryAssignmentStatuses.PendingApproval:
                await _decisions.ApproveAsync(row, caller, default);
                break;
            case "REJECTED" when row.ApprovalStatus == SalaryAssignmentStatuses.PendingApproval:
                row.ApprovalStatus = SalaryAssignmentStatuses.Rejected;
                await _db.SaveChangesAsync();
                break;
            case "CANCELLED" or "RECALLED" when row.ApprovalStatus == SalaryAssignmentStatuses.PendingApproval:
                row.ApprovalStatus = SalaryAssignmentStatuses.Draft;
                row.ApprovalUuid = null;
                await _db.SaveChangesAsync();
                break;
        }
    }
}
