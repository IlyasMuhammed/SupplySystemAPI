using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Modules.Hrm.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.Modules.Hrm.Services.Att;

// API-CONTRACT §6.4. Balance = Σ ledger deltas of the leave year; the ledger is append-only.

public interface ILeaveTypeService
{
    Task<IReadOnlyList<LeaveTypeModel>> ListAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default);
    Task<LeaveTypeModel> CreateAsync(HrmCaller caller, LeaveTypeUpsert request, CancellationToken ct = default);
    Task<LeaveTypeModel> UpdateAsync(HrmCaller caller, Guid uuid, LeaveTypeUpsert request, CancellationToken ct = default);
    Task<LeaveTypeModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default);
    Task<LeavePolicyModel> AddPolicyAsync(HrmCaller caller, Guid leaveTypeUuid, LeavePolicyUpsert request, CancellationToken ct = default);
    Task<LeavePolicyModel> UpdatePolicyAsync(HrmCaller caller, Guid policyUuid, LeavePolicyUpsert request, CancellationToken ct = default);
}

public interface ILeaveService
{
    Task<decimal> GetBalanceAsync(HrmCaller caller, Guid employeeUuid, Guid leaveTypeUuid, int year, CancellationToken ct = default);
    Task<IReadOnlyList<LeaveBalanceModel>> BalancesAsync(HrmCaller caller, Guid? employeeUuid, int? year, CancellationToken ct = default);
    Task<IReadOnlyList<LeaveLedgerModel>> LedgerAsync(HrmCaller caller, Guid? employeeUuid, Guid? leaveTypeUuid, int? year, CancellationToken ct = default);
    Task<LeaveLedgerModel> AllocateAsync(HrmCaller caller, LeaveAllocateRequest request, CancellationToken ct = default);
    Task<LeaveBulkAllocateResult> BulkAllocateAsync(HrmCaller caller, LeaveBulkAllocateRequest request, CancellationToken ct = default);
    Task<LeaveCarryForwardResult> CarryForwardAsync(HrmCaller caller, LeaveCarryForwardRequest request, CancellationToken ct = default);
    Task<PaginatedResponse<LeaveRequestModel>> ListRequestsAsync(HrmCaller caller, LeaveRequestFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<LeaveRequestModel>> OwnRequestsAsync(HrmCaller caller, CancellationToken ct = default);
    Task<LeaveRequestModel> GetRequestAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<IReadOnlyList<LeaveCalendarEntry>> CalendarAsync(HrmCaller caller, DateOnly? from, DateOnly? to, Guid? departmentUuid, CancellationToken ct = default);
    Task<LeaveRequestModel> RequestLeaveAsync(HrmCaller caller, LeaveRequestCreate request, CancellationToken ct = default);
    Task<LeaveRequestModel> ApproveAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<LeaveRequestModel> RejectAsync(HrmCaller caller, Guid uuid, string? note, CancellationToken ct = default);
    Task<LeaveRequestModel> CancelAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
}

internal sealed class LeaveTypeService : HrmServiceBase, ILeaveTypeService
{
    public LeaveTypeService(HrmDbContext db) : base(db) { }

    private async Task<LeaveTypeModel> ModelAsync(Guid org, int id, CancellationToken ct) =>
        (await ListCoreAsync(org, true, id, ct)).Single();

    private async Task<List<LeaveTypeModel>> ListCoreAsync(Guid org, bool includeInactive, int? id, CancellationToken ct) =>
        await Db.LeaveTypes.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.OrganizationId == org && (includeInactive || t.IsActive) && (id == null || t.Id == id)).OrderBy(t => t.Name)
            .Select(t => new LeaveTypeModel
            {
                Uuid = t.Uuid, Code = t.Code, Name = t.Name, IsPaid = t.IsPaid, CarryForwardCap = t.CarryForwardCap, AttachmentAfterDays = t.AttachmentAfterDays,
                GenderRestriction = t.GenderRestriction, AllowNegativeBalance = t.AllowNegativeBalance, IsActive = t.IsActive,
                Policies = t.Policies.OrderBy(p => p.EffectiveFrom).Select(p => new LeavePolicyModel
                {
                    Uuid = p.Uuid, GradeUuid = p.Grade == null ? null : p.Grade.Uuid, Entitlement = p.Entitlement, AccrualMethod = p.AccrualMethod,
                    EffectiveFrom = p.EffectiveFrom, EffectiveTo = p.EffectiveTo
                }).ToList()
            }).ToListAsync(ct);

    public async Task<IReadOnlyList<LeaveTypeModel>> ListAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default) =>
        await ListCoreAsync(caller.OrganizationId, includeInactive, null, ct);

    private static void Apply(LeaveType row, LeaveTypeUpsert r)
    {
        row.Code = HrmValidate.Code(r.Code);
        row.Name = HrmValidate.Name(r.Name);
        if (r.CarryForwardCap < 0) throw new BadRequestException("The carry-forward cap cannot be negative.");
        if (r.AttachmentAfterDays < 0) throw new BadRequestException("The attachment rule cannot be negative.");
        row.IsPaid = r.IsPaid;
        row.CarryForwardCap = r.CarryForwardCap;
        row.AttachmentAfterDays = r.AttachmentAfterDays;
        row.GenderRestriction = string.IsNullOrWhiteSpace(r.GenderRestriction) ? null : HrmValidate.OneOf(r.GenderRestriction, Genders.All, "genderRestriction");
        row.AllowNegativeBalance = r.AllowNegativeBalance;
    }

    private async Task CheckCodeAsync(Guid org, string code, int id, CancellationToken ct)
    {
        if (await Db.LeaveTypes.IgnoreQueryFilters().AnyAsync(t => t.OrganizationId == org && t.Code == code && t.Id != id, ct))
            throw HrmErrors.Conflict("LEAVE_TYPE_CODE_TAKEN", $"A leave type with code {code} already exists.");
    }

    public async Task<LeaveTypeModel> CreateAsync(HrmCaller caller, LeaveTypeUpsert request, CancellationToken ct = default)
    {
        var row = new LeaveType();
        Apply(row, request);
        await CheckCodeAsync(caller.OrganizationId, row.Code, 0, ct);
        Stamp(row, caller, isNew: true);
        Db.LeaveTypes.Add(row);
        await SaveAsync(ct);
        return await ModelAsync(caller.OrganizationId, row.Id, ct);
    }

    private async Task<LeaveType> FindAsync(Guid org, Guid uuid, CancellationToken ct) =>
        await Db.LeaveTypes.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.OrganizationId == org && t.Uuid == uuid, ct) ?? throw new NotFoundException("Leave type", uuid);

    public async Task<LeaveTypeModel> UpdateAsync(HrmCaller caller, Guid uuid, LeaveTypeUpsert request, CancellationToken ct = default)
    {
        var row = await FindAsync(caller.OrganizationId, uuid, ct);
        Apply(row, request);
        await CheckCodeAsync(caller.OrganizationId, row.Code, row.Id, ct);
        Stamp(row, caller);
        await SaveAsync(ct, "This leave type");
        return await ModelAsync(caller.OrganizationId, row.Id, ct);
    }

    public async Task<LeaveTypeModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default)
    {
        var row = await FindAsync(caller.OrganizationId, uuid, ct);
        if (!isActive && row.IsActive)
        {
            var pending = await Db.LeaveRequests.IgnoreQueryFilters().CountAsync(r => r.OrganizationId == caller.OrganizationId && r.LeaveTypeId == row.Id && r.Status == LeaveRequestStatuses.Pending, ct);
            if (pending > 0) throw HrmErrors.Conflict("LEAVE_TYPE_IN_USE", $"{pending} request(s) of this type are pending; decide them first.");
        }
        row.IsActive = isActive;
        Stamp(row, caller);
        await SaveAsync(ct, "This leave type");
        return await ModelAsync(caller.OrganizationId, row.Id, ct);
    }

    private async Task<LeavePolicyModel> PolicyModelAsync(LeavePolicy p, CancellationToken ct)
    {
        var grade = p.GradeId is { } g ? await Db.Grades.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == g).Select(x => (Guid?)x.Uuid).FirstOrDefaultAsync(ct) : null;
        return new LeavePolicyModel { Uuid = p.Uuid, GradeUuid = grade, Entitlement = p.Entitlement, AccrualMethod = p.AccrualMethod, EffectiveFrom = p.EffectiveFrom, EffectiveTo = p.EffectiveTo };
    }

    private static void ValidatePolicy(LeavePolicyUpsert r)
    {
        if (r.Entitlement < 0) throw new BadRequestException("The entitlement cannot be negative.");
        if (r.EffectiveFrom is null) throw new BadRequestException("The effective date is required.");
        if (r.EffectiveTo is { } to && to < r.EffectiveFrom) throw new BadRequestException("The end date is before the effective date.");
    }

    public async Task<LeavePolicyModel> AddPolicyAsync(HrmCaller caller, Guid leaveTypeUuid, LeavePolicyUpsert request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var type = await FindAsync(org, leaveTypeUuid, ct);
        ValidatePolicy(request);
        var from = request.EffectiveFrom!.Value;
        var gradeId = await OptionalIdAsync(Db.Grades, org, request.GradeUuid, "Grade", ct);

        var previous = await Db.LeavePolicies.IgnoreQueryFilters()
            .Where(p => p.OrganizationId == org && p.LeaveTypeId == type.Id && p.GradeId == gradeId).OrderByDescending(p => p.EffectiveFrom).FirstOrDefaultAsync(ct);
        if (previous is not null)
        {
            if (from <= previous.EffectiveFrom) throw HrmErrors.Conflict("LEAVE_POLICY_OVERLAP", $"A new policy must start after the latest one ({previous.EffectiveFrom:yyyy-MM-dd}).");
            if (previous.EffectiveTo is { } to && from <= to) throw HrmErrors.Conflict("LEAVE_POLICY_OVERLAP", $"The latest policy runs until {to:yyyy-MM-dd}.");
            if (previous.EffectiveTo is null) previous.EffectiveTo = from.AddDays(-1);
            Stamp(previous, caller);
        }
        var row = new LeavePolicy
        {
            LeaveTypeId = type.Id, GradeId = gradeId, Entitlement = request.Entitlement,
            AccrualMethod = HrmValidate.OneOf(request.AccrualMethod ?? LeaveAccrualMethods.Annual, LeaveAccrualMethods.All, "accrualMethod"),
            EffectiveFrom = from, EffectiveTo = request.EffectiveTo
        };
        Stamp(row, caller, isNew: true);
        Db.LeavePolicies.Add(row);
        await SaveAsync(ct, "This leave policy");
        return await PolicyModelAsync(row, ct);
    }

    public async Task<LeavePolicyModel> UpdatePolicyAsync(HrmCaller caller, Guid policyUuid, LeavePolicyUpsert request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var row = await Db.LeavePolicies.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.OrganizationId == org && p.Uuid == policyUuid, ct)
            ?? throw new NotFoundException("Leave policy", policyUuid);
        ValidatePolicy(request);
        row.Entitlement = request.Entitlement;
        row.AccrualMethod = HrmValidate.OneOf(request.AccrualMethod ?? row.AccrualMethod, LeaveAccrualMethods.All, "accrualMethod");
        row.EffectiveFrom = request.EffectiveFrom!.Value;
        row.EffectiveTo = request.EffectiveTo;
        row.GradeId = await OptionalIdAsync(Db.Grades, org, request.GradeUuid, "Grade", ct);
        Stamp(row, caller);
        await SaveAsync(ct, "This leave policy");
        return await PolicyModelAsync(row, ct);
    }
}

/// <summary>Task 139: what a policy gives for a year.</summary>
internal static class LeaveEntitlement
{
    /// <summary>The newest policy in force in the year: the employee's grade first, else the one for every grade.</summary>
    public static LeavePolicy? PolicyFor(IEnumerable<LeavePolicy> policies, int? gradeId, int year)
    {
        var start = new DateOnly(year, 1, 1);
        var end = new DateOnly(year, 12, 31);
        var inForce = policies.Where(p => p.EffectiveFrom <= end && (p.EffectiveTo is null || p.EffectiveTo >= start)).OrderByDescending(p => p.EffectiveFrom).ToList();
        return inForce.FirstOrDefault(p => gradeId is not null && p.GradeId == gradeId) ?? inForce.FirstOrDefault(p => p.GradeId is null);
    }

    /// <summary>Annual: the whole entitlement. Monthly: one twelfth for each month reached, from the join month. None: nothing is granted.</summary>
    public static decimal DueBy(LeavePolicy policy, int year, DateOnly asOf, DateOnly? joinDate)
    {
        if (policy.AccrualMethod == LeaveAccrualMethods.None) return 0m;
        if (policy.AccrualMethod == LeaveAccrualMethods.Annual) return policy.Entitlement;
        if (asOf.Year < year) return 0m;
        var lastMonth = asOf.Year > year ? 12 : asOf.Month;
        var firstMonth = joinDate is { } join && join.Year == year ? join.Month : joinDate is { } j && j.Year > year ? 13 : 1;
        var months = Math.Max(0, lastMonth - firstMonth + 1);
        return Math.Round(policy.Entitlement * months / 12m, 2);
    }

    /// <summary>Task 151: what a closing year's balance becomes.</summary>
    public static (decimal Carried, decimal Expired) CarryForward(decimal balance, decimal? cap, decimal alreadyCarried)
    {
        if (balance <= 0) return (0m, 0m);
        var room = Math.Max(0m, (cap ?? 0m) - alreadyCarried);
        var carried = Math.Min(balance, room);
        return (carried, balance - carried);
    }
}

/// <summary>
/// Applies a decision to a request: the ledger entry, the status, the attendance days. Shared by the workflow's status handler and
/// <see cref="LeaveService"/>, and it must not depend on the workflow engine (engine → status service → handler would be a DI cycle).
/// </summary>
internal sealed class LeaveDecisionApplier
{
    private readonly HrmDbContext _db;
    private readonly IAttendanceRecalculator _recalc;

    public LeaveDecisionApplier(HrmDbContext db, IAttendanceRecalculator recalc)
    {
        _db = db;
        _recalc = recalc;
    }

    private void AddLedger(LeaveRequest row, decimal delta, string reason, int? by, string? byName)
    {
        _db.LeaveLedgerEntries.Add(new LeaveLedgerEntry
        {
            OrganizationId = row.OrganizationId, EmployeeId = row.EmployeeId, LeaveTypeId = row.LeaveTypeId, Delta = delta, Reason = reason,
            Date = DateOnly.FromDateTime(DateTime.UtcNow), Year = row.FromDate.Year, ReferenceUuid = row.Uuid, Note = row.RequestNumber,
            CreatedBy = by, CreatedByName = byName, ModifiedBy = by, ModifiedByName = byName
        });
    }

    private Task RecomputeAsync(LeaveRequest row, CancellationToken ct) =>
        _recalc.RecomputeAsync(row.OrganizationId, [row.EmployeeId], row.FromDate, row.ToDate, ct: ct);

    /// <summary>Task 148. Idempotent: anything but a pending request is left alone (the engine can repeat a status).</summary>
    public async Task ApproveAsync(Guid uuid, int? by, string? byName, CancellationToken ct = default)
    {
        var row = await _db.LeaveRequests.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Uuid == uuid, ct);
        if (row is null || row.Status != LeaveRequestStatuses.Pending) return;
        await _db.InTransactionAsync(async () =>
        {
            row.Status = LeaveRequestStatuses.Approved;
            row.DecidedBy = by;
            row.DecidedByName = byName;
            row.DecidedAt = DateTime.UtcNow;
            if (!_db.ChangeTracker.Entries<LeaveLedgerEntry>().Any(e => e.State == EntityState.Added && e.Entity.ReferenceUuid == uuid))
                AddLedger(row, -row.Days, LeaveLedgerReasons.Consumption, by, byName);
            await _db.SaveChangesAsync(ct);
        }, ct);
        await RecomputeAsync(row, ct);
    }

    /// <summary>Rejected, cancelled or recalled while pending: nothing was deducted, so only the status moves.</summary>
    public async Task ClosePendingAsync(Guid uuid, string status, int? by, string? byName, CancellationToken ct = default)
    {
        var row = await _db.LeaveRequests.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Uuid == uuid, ct);
        if (row is null || row.Status != LeaveRequestStatuses.Pending) return;
        row.Status = status;
        row.DecidedBy = by;
        row.DecidedByName = byName;
        row.DecidedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Task 150: an approved request is cancelled, its days go back.</summary>
    public async Task CancelApprovedAsync(LeaveRequest row, int? by, string? byName, CancellationToken ct = default)
    {
        await _db.InTransactionAsync(async () =>
        {
            row.Status = LeaveRequestStatuses.Cancelled;
            row.DecidedBy = by;
            row.DecidedByName = byName;
            row.DecidedAt = DateTime.UtcNow;
            if (!_db.ChangeTracker.Entries<LeaveLedgerEntry>().Any(e => e.State == EntityState.Added && e.Entity.ReferenceUuid == row.Uuid))
                AddLedger(row, row.Days, LeaveLedgerReasons.CancellationRestore, by, byName);
            await _db.SaveChangesAsync(ct);
        }, ct);
        await RecomputeAsync(row, ct);
    }
}

/// <summary>E-15: LEAVE_REQUEST statuses from the workflow engine. The engine's tenant is all it has: rows are found by uuid and keep their own organization.</summary>
internal sealed class LeaveRequestStatusHandler : IDocumentStatusHandler
{
    private readonly HrmDbContext _db;
    private readonly LeaveDecisionApplier _applier;
    private readonly IHttpContextAccessor? _http;
    private readonly HrmUserNames? _names;

    public LeaveRequestStatusHandler(HrmDbContext db, LeaveDecisionApplier applier, IHttpContextAccessor? http = null, HrmUserNames? names = null)
    {
        _db = db;
        _applier = applier;
        _http = http;
        _names = names;
    }

    public string InterfaceCode => HrmWorkflowCodes.LeaveRequest;

    public async Task<string?> GetStatusAsync(Guid documentId)
    {
        var status = await _db.LeaveRequests.IgnoreQueryFilters().AsNoTracking().Where(r => r.Uuid == documentId).Select(r => r.Status).FirstOrDefaultAsync();
        return status?.ToUpperInvariant();
    }

    public async Task UpdateStatusAsync(Guid documentId, string newStatus)
    {
        // The decider, when the decision came through a request (the inbox). An engine job has none.
        var user = _http?.HttpContext?.User;
        int? by = user?.Identity?.IsAuthenticated == true ? user.GetUserId() : null;
        var byName = user?.FindFirst("name")?.Value ?? user?.Identity?.Name;
        if (_names is not null && by is not null)
        {
            // The engine has only its tenant: the row's own organization.
            var org = await _db.LeaveRequests.IgnoreQueryFilters().AsNoTracking().Where(r => r.Uuid == documentId).Select(r => r.OrganizationId).FirstOrDefaultAsync();
            byName = await _names.NameAsync(org, by, byName);
        }

        switch (newStatus.ToUpperInvariant())
        {
            case "APPROVED":
                await _applier.ApproveAsync(documentId, by, byName);
                break;
            case "REJECTED":
                await _applier.ClosePendingAsync(documentId, LeaveRequestStatuses.Rejected, by, byName);
                break;
            // A recall returns the document to its initiator; a leave request has no draft to return to, so it closes and they ask again.
            case "CANCELLED" or "RECALLED" or "DRAFT":
                await _applier.ClosePendingAsync(documentId, LeaveRequestStatuses.Cancelled, by, byName);
                break;
        }
    }
}

internal sealed class LeaveService : HrmServiceBase, ILeaveService
{
    private readonly IHrmScopeService _scope;
    private readonly IHrmApprovalGateway _approvals;
    private readonly IWorkflowActionService _workflow;
    private readonly LeaveDecisionApplier _applier;
    private readonly IAttendanceClock _clock;
    private readonly IHrmAttachmentGuard? _attachments;

    public LeaveService(HrmDbContext db, IHrmScopeService scope, IHrmApprovalGateway approvals, IWorkflowActionService workflow, LeaveDecisionApplier applier, IAttendanceClock clock, IHrmAttachmentGuard? attachments = null) : base(db)
    {
        _scope = scope;
        _approvals = approvals;
        _workflow = workflow;
        _applier = applier;
        _clock = clock;
        _attachments = attachments;
    }

    private static readonly string[] ViewCodes = [PermissionCodes.HRM_LEAVE_VIEW, PermissionCodes.HRM_LEAVE_APPROVE, PermissionCodes.HRM_LEAVE_MANAGE];

    /// <summary>
    /// Serializes balance-changing work per key: a check-then-insert under READ COMMITTED would let two requests spend the same days.
    /// Released with the transaction. In-memory tests have neither.
    /// </summary>
    private async Task LockAsync(string resource, CancellationToken ct)
    {
        if (!Db.Database.IsRelational()) return;
        await Db.Database.ExecuteSqlInterpolatedAsync($"EXEC sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000", ct);
    }

    private DateOnly Today => DateOnly.FromDateTime(_clock.UtcNow);

    /// <summary>Null or own = the caller's record (self-service or a view code). Anyone else: a view code and the scope.</summary>
    private async Task<Employee> ResolveEmployeeAsync(HrmCaller caller, Guid? employeeUuid, CancellationToken ct)
    {
        if (employeeUuid is null) return await _scope.RequireOwnEmployeeAsync(caller, ct);
        if (caller.HasAny(ViewCodes)) return await _scope.RequireEmployeeAsync(caller, employeeUuid.Value, ct);
        var own = await _scope.RequireOwnEmployeeAsync(caller, ct);
        if (own.Uuid != employeeUuid) throw new ForbiddenException("You can only see your own leave.");
        return own;
    }

    private async Task<LeaveType> RequireTypeAsync(Guid org, Guid uuid, CancellationToken ct) =>
        await Db.LeaveTypes.IgnoreQueryFilters().AsNoTracking().Include(t => t.Policies).FirstOrDefaultAsync(t => t.OrganizationId == org && t.Uuid == uuid, ct)
            ?? throw new NotFoundException("Leave type", uuid);

    private Task<decimal> BalanceAsync(Guid org, int employeeId, int leaveTypeId, int year, CancellationToken ct) =>
        Db.LeaveLedgerEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.OrganizationId == org && l.EmployeeId == employeeId && l.LeaveTypeId == leaveTypeId && l.Year == year).SumAsync(l => l.Delta, ct);

    private Task<decimal> PendingAsync(Guid org, int employeeId, int leaveTypeId, int year, CancellationToken ct)
    {
        var start = new DateOnly(year, 1, 1);
        var end = new DateOnly(year, 12, 31);
        return Db.LeaveRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && r.EmployeeId == employeeId && r.LeaveTypeId == leaveTypeId && r.Status == LeaveRequestStatuses.Pending && r.FromDate >= start && r.FromDate <= end)
            .SumAsync(r => r.Days, ct);
    }

    public async Task<decimal> GetBalanceAsync(HrmCaller caller, Guid employeeUuid, Guid leaveTypeUuid, int year, CancellationToken ct = default)
    {
        var employee = await ResolveEmployeeAsync(caller, employeeUuid, ct);
        var type = await RequireTypeAsync(caller.OrganizationId, leaveTypeUuid, ct);
        return await BalanceAsync(caller.OrganizationId, employee.Id, type.Id, year, ct);
    }

    public async Task<IReadOnlyList<LeaveBalanceModel>> BalancesAsync(HrmCaller caller, Guid? employeeUuid, int? year, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var employee = await ResolveEmployeeAsync(caller, employeeUuid, ct);
        var y = year ?? Today.Year;
        var start = new DateOnly(y, 1, 1);
        var end = new DateOnly(y, 12, 31);
        var types = await Db.LeaveTypes.IgnoreQueryFilters().AsNoTracking().Where(t => t.OrganizationId == org && t.IsActive).OrderBy(t => t.Name).ToListAsync(ct);
        var ledger = await Db.LeaveLedgerEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.OrganizationId == org && l.EmployeeId == employee.Id && l.Year == y).Select(l => new { l.LeaveTypeId, l.Reason, l.Delta }).ToListAsync(ct);
        var pending = await Db.LeaveRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && r.EmployeeId == employee.Id && r.Status == LeaveRequestStatuses.Pending && r.FromDate >= start && r.FromDate <= end)
            .Select(r => new { r.LeaveTypeId, r.Days }).ToListAsync(ct);
        return types.Select(t =>
        {
            var rows = ledger.Where(l => l.LeaveTypeId == t.Id).ToList();
            decimal Sum(params string[] reasons) => rows.Where(l => reasons.Contains(l.Reason)).Sum(l => l.Delta);
            return new LeaveBalanceModel
            {
                LeaveTypeUuid = t.Uuid, LeaveTypeCode = t.Code, LeaveTypeName = t.Name,
                Allocated = Sum(LeaveLedgerReasons.Allocation, LeaveLedgerReasons.Accrual, LeaveLedgerReasons.Opening),
                Used = -Sum(LeaveLedgerReasons.Consumption, LeaveLedgerReasons.CancellationRestore, LeaveLedgerReasons.Encashment),
                Carried = Sum(LeaveLedgerReasons.CarryForward),
                Adjusted = Sum(LeaveLedgerReasons.Adjustment, LeaveLedgerReasons.Expiry),
                Balance = rows.Sum(l => l.Delta),
                Pending = pending.Where(p => p.LeaveTypeId == t.Id).Sum(p => p.Days)
            };
        }).ToList();
    }

    public async Task<IReadOnlyList<LeaveLedgerModel>> LedgerAsync(HrmCaller caller, Guid? employeeUuid, Guid? leaveTypeUuid, int? year, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var employee = await ResolveEmployeeAsync(caller, employeeUuid, ct);
        var query = Db.LeaveLedgerEntries.IgnoreQueryFilters().AsNoTracking().Where(l => l.OrganizationId == org && l.EmployeeId == employee.Id);
        if (leaveTypeUuid is { } t) query = query.Where(l => l.LeaveType!.Uuid == t);
        if (year is { } y) query = query.Where(l => l.Year == y);
        return await query.OrderByDescending(l => l.Year).ThenByDescending(l => l.Id).Select(l => new LeaveLedgerModel
        {
            Uuid = l.Uuid, LeaveTypeUuid = l.LeaveType!.Uuid, LeaveTypeCode = l.LeaveType.Code, Delta = l.Delta, Reason = l.Reason, Date = l.Date, Year = l.Year,
            ReferenceUuid = l.ReferenceUuid, Note = l.Note
        }).ToListAsync(ct);
    }

    private static void ValidateYear(int year)
    {
        if (year is < 2000 or > 2100) throw new BadRequestException("Give a valid leave year.");
    }

    private LeaveLedgerEntry Ledger(HrmCaller caller, int employeeId, int leaveTypeId, decimal delta, string reason, int year, string? note = null)
    {
        var row = new LeaveLedgerEntry { EmployeeId = employeeId, LeaveTypeId = leaveTypeId, Delta = delta, Reason = reason, Date = Today, Year = year, Note = note };
        Stamp(row, caller, isNew: true);
        return row;
    }

    private static LeaveLedgerModel MapLedger(LeaveLedgerEntry l, LeaveType t) => new()
    {
        Uuid = l.Uuid, LeaveTypeUuid = t.Uuid, LeaveTypeCode = t.Code, Delta = l.Delta, Reason = l.Reason, Date = l.Date, Year = l.Year, ReferenceUuid = l.ReferenceUuid, Note = l.Note
    };

    public async Task<LeaveLedgerModel> AllocateAsync(HrmCaller caller, LeaveAllocateRequest request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        ValidateYear(request.Year);
        var employee = await _scope.RequireEmployeeAsync(caller, request.EmployeeUuid, ct);
        var type = await RequireTypeAsync(org, request.LeaveTypeUuid, ct);
        var note = HrmValidate.Optional(request.Note, 500, "Note");
        if (request.Adjustment ? request.Days == 0 : request.Days <= 0) throw new BadRequestException(request.Adjustment ? "An adjustment cannot be zero." : "Give the days to allocate.");

        LeaveLedgerEntry row = null!;
        await Db.InTransactionAsync(async () =>
        {
            await LockAsync($"hrm-leave-{employee.Id}", ct);
            var given = await Db.LeaveLedgerEntries.IgnoreQueryFilters().AnyAsync(l => l.OrganizationId == org && l.EmployeeId == employee.Id && l.LeaveTypeId == type.Id && l.Year == request.Year
                && (l.Reason == LeaveLedgerReasons.Allocation || l.Reason == LeaveLedgerReasons.Accrual), ct);
            if (given && !request.Adjustment)
                throw HrmErrors.Conflict("LEAVE_ALREADY_ALLOCATED", $"{type.Name} is already allocated for {request.Year}; send an adjustment to change it.");
            if (row is not null) Db.Entry(row).State = EntityState.Detached;
            row = Ledger(caller, employee.Id, type.Id, request.Days, request.Adjustment ? LeaveLedgerReasons.Adjustment : LeaveLedgerReasons.Allocation, request.Year, note);
            Db.LeaveLedgerEntries.Add(row);
            await Db.SaveChangesAsync(ct);
        }, ct);
        return MapLedger(row, type);
    }

    public async Task<LeaveBulkAllocateResult> BulkAllocateAsync(HrmCaller caller, LeaveBulkAllocateRequest request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        ValidateYear(request.Year);
        var type = await RequireTypeAsync(org, request.LeaveTypeUuid, ct);
        var scope = await _scope.GetAsync(caller, ct);
        var yearEnd = new DateOnly(request.Year, 12, 31);
        var yearStart = new DateOnly(request.Year, 1, 1);
        var employed = EmployeeStatuses.Employed.ToList();
        var employees = await _scope.Apply(Db.Employees.AsNoTracking(), caller, scope)
            .Where(e => employed.Contains(e.Status) && (e.JoinDate == null || e.JoinDate <= yearEnd) && (e.LeaveDate == null || e.LeaveDate >= yearStart))
            .Select(e => new { e.Id, e.Gender, e.JoinDate }).ToListAsync(ct);
        var ids = employees.Select(e => e.Id).ToList();
        var grades = (await Db.EmploymentRecords.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.OrganizationId == org && ids.Contains(r.EmployeeId)).Select(r => new { r.EmployeeId, r.EffectiveFrom, r.GradeId }).ToListAsync(ct))
            .GroupBy(r => r.EmployeeId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).First().GradeId);
        var asOf = Today;

        var result = new LeaveBulkAllocateResult();
        await Db.InTransactionAsync(async () =>
        {
            result.Allocated = result.Skipped = 0;
            foreach (var added in Db.ChangeTracker.Entries<LeaveLedgerEntry>().Where(e => e.State == EntityState.Added).ToList()) added.State = EntityState.Detached;
            await LockAsync($"hrm-leave-bulk-{org:N}-{type.Id}", ct);
            var ledger = await Db.LeaveLedgerEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.OrganizationId == org && l.LeaveTypeId == type.Id && l.Year == request.Year && ids.Contains(l.EmployeeId)
                    && (l.Reason == LeaveLedgerReasons.Allocation || l.Reason == LeaveLedgerReasons.Accrual))
                .Select(l => new { l.EmployeeId, l.Reason, l.Delta }).ToListAsync(ct);
            foreach (var e in employees)
            {
                var policy = LeaveEntitlement.PolicyFor(type.Policies, grades.GetValueOrDefault(e.Id), request.Year);
                var restricted = type.GenderRestriction is { } g && e.Gender != g;
                var due = policy is null || restricted ? 0m : LeaveEntitlement.DueBy(policy, request.Year, asOf, e.JoinDate);
                var mine = ledger.Where(l => l.EmployeeId == e.Id).ToList();
                // Annual: once. Monthly: tops up to what is due by now, so running it each month is safe.
                var delta = policy?.AccrualMethod == LeaveAccrualMethods.Monthly ? due - mine.Sum(l => l.Delta) : mine.Count > 0 ? 0m : due;
                if (delta <= 0) { result.Skipped++; continue; }
                Db.LeaveLedgerEntries.Add(Ledger(caller, e.Id, type.Id, delta,
                    policy!.AccrualMethod == LeaveAccrualMethods.Monthly ? LeaveLedgerReasons.Accrual : LeaveLedgerReasons.Allocation, request.Year));
                result.Allocated++;
            }
            await Db.SaveChangesAsync(ct);
        }, ct);
        return result;
    }

    public async Task<LeaveCarryForwardResult> CarryForwardAsync(HrmCaller caller, LeaveCarryForwardRequest request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        ValidateYear(request.FromYear);
        if (request.ToYear != request.FromYear + 1) throw new BadRequestException("Carry forward moves a balance into the next year.");
        var scope = await _scope.GetAsync(caller, ct);
        var ids = await _scope.Apply(Db.Employees.AsNoTracking(), caller, scope).Where(e => e.Status != EmployeeStatuses.Terminated && e.Status != EmployeeStatuses.Archived && e.Status != EmployeeStatuses.Candidate)
            .Select(e => e.Id).ToListAsync(ct);
        var types = await Db.LeaveTypes.IgnoreQueryFilters().AsNoTracking().Where(t => t.OrganizationId == org).ToListAsync(ct);

        var result = new LeaveCarryForwardResult();
        await Db.InTransactionAsync(async () =>
        {
            result.Processed = 0;
            result.Carried = result.Expired = 0m;
            foreach (var added in Db.ChangeTracker.Entries<LeaveLedgerEntry>().Where(e => e.State == EntityState.Added).ToList()) added.State = EntityState.Detached;
            await LockAsync($"hrm-leave-carry-{org:N}", ct);
            var balances = await Db.LeaveLedgerEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.OrganizationId == org && l.Year == request.FromYear && ids.Contains(l.EmployeeId))
                .GroupBy(l => new { l.EmployeeId, l.LeaveTypeId }).Select(g => new { g.Key.EmployeeId, g.Key.LeaveTypeId, Balance = g.Sum(l => l.Delta) }).ToListAsync(ct);
            var alreadyCarried = await Db.LeaveLedgerEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.OrganizationId == org && l.Year == request.ToYear && l.Reason == LeaveLedgerReasons.CarryForward && ids.Contains(l.EmployeeId))
                .GroupBy(l => new { l.EmployeeId, l.LeaveTypeId }).Select(g => new { g.Key.EmployeeId, g.Key.LeaveTypeId, Days = g.Sum(l => l.Delta) }).ToListAsync(ct);

            // Closing a year brings its balance to zero, so a second run finds nothing: that is what makes it idempotent. A leave
            // cancelled after the close gives a new positive balance, and the next run handles exactly that, within what is left of the cap.
            foreach (var b in balances.Where(b => b.Balance > 0))
            {
                var type = types.First(t => t.Id == b.LeaveTypeId);
                var carriedBefore = alreadyCarried.FirstOrDefault(c => c.EmployeeId == b.EmployeeId && c.LeaveTypeId == b.LeaveTypeId)?.Days ?? 0m;
                var (carried, expired) = LeaveEntitlement.CarryForward(b.Balance, type.CarryForwardCap, carriedBefore);
                Db.LeaveLedgerEntries.Add(Ledger(caller, b.EmployeeId, b.LeaveTypeId, -b.Balance, LeaveLedgerReasons.Expiry, request.FromYear, $"Year {request.FromYear} closed"));
                if (carried > 0) Db.LeaveLedgerEntries.Add(Ledger(caller, b.EmployeeId, b.LeaveTypeId, carried, LeaveLedgerReasons.CarryForward, request.ToYear, $"From {request.FromYear}"));
                result.Processed++;
                result.Carried += carried;
                result.Expired += expired;
            }
            await Db.SaveChangesAsync(ct);
        }, ct);
        return result;
    }

    private IQueryable<LeaveRequestModel> Project(IQueryable<LeaveRequest> rows) => rows.Select(r => new LeaveRequestModel
    {
        Uuid = r.Uuid, RequestNumber = r.RequestNumber, EmployeeUuid = r.Employee!.Uuid, EmployeeName = r.Employee.FirstName + " " + r.Employee.LastName, LeaveTypeUuid = r.LeaveType!.Uuid,
        LeaveTypeName = r.LeaveType.Name, FromDate = r.FromDate, ToDate = r.ToDate, HalfDay = r.HalfDay, Days = r.Days, Reason = r.Reason, Status = r.Status, AttachmentUuid = r.AttachmentUuid,
        ApprovalUuid = r.ApprovalUuid, DecidedByName = r.DecidedByName, DecidedAt = r.DecidedAt, DecisionNote = r.DecisionNote
    });

    private IQueryable<LeaveRequest> Requests(Guid org) => Db.LeaveRequests.IgnoreQueryFilters().AsNoTracking().Where(r => r.OrganizationId == org);

    public async Task<PaginatedResponse<LeaveRequestModel>> ListRequestsAsync(HrmCaller caller, LeaveRequestFilter filter, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);
        var scope = await _scope.GetAsync(caller, ct);
        var employees = _scope.Apply(Db.Employees.AsNoTracking(), caller, scope);
        if (filter.EmployeeUuid is { } uuid) employees = employees.Where(e => e.Uuid == uuid);
        var ids = await employees.Select(e => e.Id).ToListAsync(ct);

        var query = Requests(org).Where(r => ids.Contains(r.EmployeeId));
        if (filter.Status is not null)
        {
            var status = HrmValidate.OneOf(filter.Status, LeaveRequestStatuses.All, "status");
            query = query.Where(r => r.Status == status);
        }
        if (filter.From is { } from) query = query.Where(r => r.ToDate >= from);
        if (filter.To is { } to) query = query.Where(r => r.FromDate <= to);
        var total = await query.CountAsync(ct);
        var data = await Project(query.OrderByDescending(r => r.FromDate).ThenByDescending(r => r.Id)).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PaginatedResponse<LeaveRequestModel> { Data = data, TotalRecords = total, Page = page, PageSize = pageSize, TotalPages = (int)Math.Ceiling(total / (double)pageSize) };
    }

    public async Task<IReadOnlyList<LeaveRequestModel>> OwnRequestsAsync(HrmCaller caller, CancellationToken ct = default)
    {
        var employee = await _scope.RequireOwnEmployeeAsync(caller, ct);
        return await Project(Requests(caller.OrganizationId).Where(r => r.EmployeeId == employee.Id).OrderByDescending(r => r.FromDate)).ToListAsync(ct);
    }

    private async Task RequireViewAsync(HrmCaller caller, int employeeId, Guid uuid, CancellationToken ct)
    {
        var own = await _scope.OwnEmployeeIdAsync(caller, ct);
        if (own == employeeId) return;
        if (!caller.HasAny(ViewCodes) || !await _scope.CanAccessAsync(caller, employeeId, ct)) throw new NotFoundException("Leave request", uuid);
    }

    public async Task<LeaveRequestModel> GetRequestAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var employeeId = await Requests(caller.OrganizationId).Where(r => r.Uuid == uuid).Select(r => (int?)r.EmployeeId).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Leave request", uuid);
        await RequireViewAsync(caller, employeeId, uuid, ct);
        return await Project(Requests(caller.OrganizationId).Where(r => r.Uuid == uuid)).FirstAsync(ct);
    }

    public async Task<IReadOnlyList<LeaveCalendarEntry>> CalendarAsync(HrmCaller caller, DateOnly? from, DateOnly? to, Guid? departmentUuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var start = from ?? new DateOnly(Today.Year, Today.Month, 1);
        var end = to ?? start.AddMonths(1).AddDays(-1);
        if (end < start || end.DayNumber - start.DayNumber > 366) throw new BadRequestException("Give a range of at most a year.");
        var scope = await _scope.GetAsync(caller, ct);
        var employees = _scope.Apply(Db.Employees.AsNoTracking(), caller, scope);
        if (departmentUuid is { } dep) employees = employees.Where(e => e.EmploymentRecords.Any(r => (r.EffectiveTo == null || r.EffectiveTo >= start) && r.EffectiveFrom <= end && r.Department!.Uuid == dep));
        var ids = await employees.Select(e => e.Id).ToListAsync(ct);
        return await Requests(org).Where(r => ids.Contains(r.EmployeeId) && r.Status == LeaveRequestStatuses.Approved && r.FromDate <= end && r.ToDate >= start)
            .OrderBy(r => r.FromDate).Select(r => new LeaveCalendarEntry
            {
                EmployeeUuid = r.Employee!.Uuid, EmployeeName = r.Employee.FirstName + " " + r.Employee.LastName, FromDate = r.FromDate, ToDate = r.ToDate, HalfDay = r.HalfDay
            }).ToListAsync(ct);
    }

    private async Task<string> NextNumberAsync(Guid org, int year, CancellationToken ct)
    {
        var prefix = $"LV-{year}-";
        var numbers = await Db.LeaveRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && r.RequestNumber.StartsWith(prefix)).Select(r => r.RequestNumber).ToListAsync(ct);
        var max = numbers.Select(n => int.TryParse(n[prefix.Length..], out var v) ? v : 0).DefaultIfEmpty(0).Max();
        return $"{prefix}{max + 1:D5}";
    }

    public async Task<LeaveRequestModel> RequestLeaveAsync(HrmCaller caller, LeaveRequestCreate request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        Employee employee;
        var ownId = await _scope.OwnEmployeeIdAsync(caller, ct);
        if (request.EmployeeUuid is { } uuid && uuid != Guid.Empty)
        {
            employee = await _scope.RequireEmployeeAsync(caller, uuid, ct);
            if (employee.Id != ownId) caller.Require(PermissionCodes.HRM_LEAVE_MANAGE, "Only leave managers can request leave for someone else.");
        }
        else employee = await _scope.RequireOwnEmployeeAsync(caller, ct);
        if (employee.Id == ownId) caller.Require(PermissionCodes.HRM_SELF_SERVICE);
        if (!EmployeeStatuses.Employed.Contains(employee.Status)) throw new BadRequestException("This employee is not on the books.");

        var type = await RequireTypeAsync(org, request.LeaveTypeUuid, ct);
        if (!type.IsActive) throw HrmErrors.Bad("LEAVE_TYPE_INACTIVE", $"{type.Name} is no longer in use.");
        if (type.GenderRestriction is { } gender && employee.Gender != gender) throw HrmErrors.Bad("LEAVE_GENDER_RESTRICTED", $"{type.Name} does not apply to this employee.");

        var from = request.FromDate ?? throw new BadRequestException("The start date is required.");
        var to = request.ToDate ?? from;
        if (to < from) throw new BadRequestException("The end date is before the start date.");
        if (from.Year != to.Year) throw HrmErrors.Bad("LEAVE_SPANS_YEARS", "Balances are per leave year: split this into one request for each year.");
        if (request.HalfDay && from != to) throw new BadRequestException("A half day is a single date.");

        var data = await AttendanceData.LoadAsync(Db, _clock, org, [employee.Id], from, to, ct);
        var workingDays = 0;
        for (var date = from; date <= to; date = date.AddDays(1)) if (data.IsWorkingDay(employee.Id, date)) workingDays++;
        if (workingDays == 0) throw HrmErrors.Bad("LEAVE_NO_WORKING_DAYS", "These dates hold no working day: holidays and weekly offs do not count as leave.");
        var days = request.HalfDay ? 0.5m : workingDays;

        var attachment = await _attachments.CheckedAsync(caller, request.AttachmentUuid, HrmAttachmentCodes.LeaveRequest);
        if (type.AttachmentAfterDays is { } after && days > after && attachment is null)
            throw HrmErrors.Bad("LEAVE_ATTACHMENT_REQUIRED", $"{type.Name} over {after} day(s) needs a supporting document.");

        var reason = HrmValidate.Optional(request.Reason, 500, "Reason");
        LeaveRequest row = null!;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Db.InTransactionAsync(async () =>
                {
                    await LockAsync($"hrm-leave-{employee.Id}", ct);
                    var overlap = await Db.LeaveRequests.IgnoreQueryFilters().AsNoTracking().AnyAsync(r => r.OrganizationId == org && r.EmployeeId == employee.Id
                        && (r.Status == LeaveRequestStatuses.Pending || r.Status == LeaveRequestStatuses.Approved) && r.FromDate <= to && r.ToDate >= from, ct);
                    if (overlap) throw HrmErrors.Conflict("LEAVE_OVERLAP", "This employee already has leave on those dates.");

                    // Unpaid leave spends no quota. Requests still pending count, so two requests cannot both claim the last days.
                    if (type.IsPaid && !type.AllowNegativeBalance)
                    {
                        var available = await BalanceAsync(org, employee.Id, type.Id, from.Year, ct) - await PendingAsync(org, employee.Id, type.Id, from.Year, ct);
                        if (days > available)
                            throw HrmErrors.Conflict("LEAVE_INSUFFICIENT_BALANCE", $"{type.Name}: {available:0.##} day(s) available, {days:0.##} requested.");
                    }

                    if (row is not null) Db.Entry(row).State = EntityState.Detached;
                    row = new LeaveRequest
                    {
                        RequestNumber = await NextNumberAsync(org, from.Year, ct), EmployeeId = employee.Id, LeaveTypeId = type.Id, FromDate = from, ToDate = to, HalfDay = request.HalfDay,
                        Days = days, Reason = reason, Status = LeaveRequestStatuses.Pending, AttachmentUuid = attachment
                    };
                    Stamp(row, caller, isNew: true);
                    Db.LeaveRequests.Add(row);
                    await Db.SaveChangesAsync(ct);
                }, ct);
                break;
            }
            catch (DbUpdateException ex) when (attempt < 3 && IsDuplicate(ex))
            {
                Db.Entry(row).State = EntityState.Detached;
            }
        }

        // The engine has its own context, so this cannot share the transaction. It is last: if it refuses, the request is taken back.
        // The request row is already saved, so this step runs to the end even if the caller disconnects (a slow call that the browser
        // gave up on used to leave a Pending request with no approval, which nobody could approve). `ct` only guards the work above.
        var finish = CancellationToken.None;
        try
        {
            row.ApprovalUuid = await _approvals.SubmitAsync(caller, HrmWorkflowCodes.LeaveRequest, row.Uuid, row.RequestNumber, employee.Id, PermissionCodes.HRM_LEAVE_APPROVE, days, finish);
            await SaveAsync(finish, "This leave request");
        }
        catch (Exception)
        {
            Db.LeaveRequests.Remove(row);
            await Db.SaveChangesAsync(finish);
            throw;
        }
        return await GetRequestModelAsync(org, row.Uuid, ct);
    }

    private Task<LeaveRequestModel> GetRequestModelAsync(Guid org, Guid uuid, CancellationToken ct) =>
        Project(Requests(org).Where(r => r.Uuid == uuid)).FirstAsync(ct);

    /// <summary>The request a decider may act on: in the organization, in their scope, still open, and not their own.</summary>
    private async Task<LeaveRequest> DecidableAsync(HrmCaller caller, Guid uuid, CancellationToken ct)
    {
        var row = await Db.LeaveRequests.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.OrganizationId == caller.OrganizationId && r.Uuid == uuid, ct)
            ?? throw new NotFoundException("Leave request", uuid);
        if (!await _scope.CanAccessAsync(caller, row.EmployeeId, ct)) throw new NotFoundException("Leave request", uuid);
        if (row.Status != LeaveRequestStatuses.Pending) throw HrmErrors.Conflict("LEAVE_ALREADY_DECIDED", $"This request is already {row.Status.ToLowerInvariant()}.");
        if (await _scope.OwnEmployeeIdAsync(caller, ct) == row.EmployeeId) throw new ForbiddenException("You cannot decide your own leave.");
        return row;
    }

    public async Task<LeaveRequestModel> ApproveAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var row = await DecidableAsync(caller, uuid, ct);
        var type = await Db.LeaveTypes.IgnoreQueryFilters().AsNoTracking().FirstAsync(t => t.Id == row.LeaveTypeId, ct);
        // The balance may have moved since the request (an adjustment, a carry-forward close).
        if (type.IsPaid && !type.AllowNegativeBalance)
        {
            var balance = await BalanceAsync(org, row.EmployeeId, row.LeaveTypeId, row.FromDate.Year, ct);
            if (row.Days > balance) throw HrmErrors.Conflict("LEAVE_INSUFFICIENT_BALANCE", $"{type.Name}: {balance:0.##} day(s) left, this request needs {row.Days:0.##}.");
        }
        // The engine decides whether this person may act on the current step, and calls the status handler on the final one.
        await _workflow.ApproveByDocumentAsync(HrmWorkflowCodes.LeaveRequest, row.Uuid, caller.UserId);
        if (row.Status == LeaveRequestStatuses.Approved && row.DecidedByName is null)
        {
            row.DecidedBy = caller.UserIdOrNull;
            row.DecidedByName = caller.UserName;
            await SaveAsync(ct, "This leave request");
        }
        return await GetRequestModelAsync(org, uuid, ct);
    }

    public async Task<LeaveRequestModel> RejectAsync(HrmCaller caller, Guid uuid, string? note, CancellationToken ct = default)
    {
        var text = HrmValidate.Name(note, 500, "A note");
        var row = await DecidableAsync(caller, uuid, ct);
        await _workflow.RejectByDocumentAsync(HrmWorkflowCodes.LeaveRequest, row.Uuid, caller.UserId, text);
        row.DecisionNote = text;
        row.DecidedBy ??= caller.UserIdOrNull;
        row.DecidedByName ??= caller.UserName;
        await SaveAsync(ct, "This leave request");
        return await GetRequestModelAsync(caller.OrganizationId, uuid, ct);
    }

    public async Task<LeaveRequestModel> CancelAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var row = await Db.LeaveRequests.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.OrganizationId == org && r.Uuid == uuid, ct)
            ?? throw new NotFoundException("Leave request", uuid);
        var own = await _scope.OwnEmployeeIdAsync(caller, ct) == row.EmployeeId;
        if (!own)
        {
            if (!await _scope.CanAccessAsync(caller, row.EmployeeId, ct)) throw new NotFoundException("Leave request", uuid);
            caller.Require(PermissionCodes.HRM_LEAVE_MANAGE, "Only the employee or a leave manager can cancel this request.");
        }

        if (row.Status == LeaveRequestStatuses.Pending)
        {
            if (row.ApprovalUuid is { } approval)
                await _workflow.CancelAsync(new CancelCommand { ApprovalUUID = approval, CancelledBy = caller.UserId, CancellationReason = "Leave request cancelled." });
            else
                await _applier.ClosePendingAsync(uuid, LeaveRequestStatuses.Cancelled, caller.UserIdOrNull, caller.UserName, ct);
        }
        else if (row.Status == LeaveRequestStatuses.Approved)
        {
            await _applier.CancelApprovedAsync(row, caller.UserIdOrNull, caller.UserName, ct);
        }
        else throw HrmErrors.Conflict("LEAVE_ALREADY_DECIDED", $"This request is already {row.Status.ToLowerInvariant()}.");

        return await GetRequestModelAsync(org, uuid, ct);
    }
}
