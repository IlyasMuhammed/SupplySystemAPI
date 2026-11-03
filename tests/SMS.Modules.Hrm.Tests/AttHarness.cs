using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Modules.Hrm.Services;
using SMS.Modules.Hrm.Services.Att;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Tests;

/// <summary>Fixed "now". 30 June 2026 17:00 in Karachi. June 2026 starts on a Monday.</summary>
internal sealed class FakeClock : IAttendanceClock
{
    public DateTime UtcNow { get; set; } = new(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);
    public Task<TimeZoneInfo> ZoneForAsync(Guid organizationId, string? countryCode, CancellationToken ct = default) => new AttendanceClock().ZoneForAsync(organizationId, countryCode, ct);
}

internal sealed class FakeGate : IModuleGate
{
    public HashSet<string> Off { get; } = [];
    public Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default) => Task.FromResult(!Off.Contains(featureCode));
}

/// <summary>The engine's contract as HRM sees it: only the assigned approver acts, the final step calls the status handler.</summary>
internal sealed class FakeWorkflow : IWorkflowActionService
{
    private readonly Dictionary<Guid, (Guid Doc, int Approver)> _approvals = [];
    public IDocumentStatusHandler? Handler { get; set; }
    public SubmitDocumentCommand? LastSubmit { get; private set; }
    public Exception? RefuseSubmitWith { get; set; }

    public async Task<Guid> SubmitAsync(SubmitDocumentCommand command)
    {
        if (RefuseSubmitWith is not null) throw RefuseSubmitWith;
        LastSubmit = command;
        var id = Guid.NewGuid();
        _approvals[id] = (command.DocumentId, command.NamedApprovers![HrmWorkflowCodes.NamedApprover]);
        await Handler!.UpdateStatusAsync(command.DocumentId, "PENDING");
        return id;
    }

    private (Guid Key, int Approver) Find(Guid doc)
    {
        var match = _approvals.First(a => a.Value.Doc == doc);
        return (match.Key, match.Value.Approver);
    }

    public int ApproverOf(Guid doc) => Find(doc).Approver;

    public async Task<Guid> ApproveByDocumentAsync(string interfaceCode, Guid documentId, int approvedBy, string? remarks = null)
    {
        var (key, approver) = Find(documentId);
        if (approver != approvedBy) throw new ForbiddenException("You are not an assigned approver for the current step.");
        await Handler!.UpdateStatusAsync(documentId, "APPROVED");
        return key;
    }

    public async Task<Guid> RejectByDocumentAsync(string interfaceCode, Guid documentId, int rejectedBy, string rejectionReason)
    {
        var (key, approver) = Find(documentId);
        if (approver != rejectedBy) throw new ForbiddenException("You are not an assigned approver for the current step.");
        await Handler!.UpdateStatusAsync(documentId, "REJECTED");
        return key;
    }

    public async Task<Guid> CancelAsync(CancelCommand command)
    {
        await Handler!.UpdateStatusAsync(_approvals[command.ApprovalUUID].Doc, "CANCELLED");
        return command.ApprovalUUID;
    }

    public Task<Guid> ApproveAsync(ApproveCommand command) => throw new NotSupportedException();
    public Task<Guid> RejectAsync(RejectCommand command) => throw new NotSupportedException();
    public Task<Guid> RecallAsync(RecallCommand command) => throw new NotSupportedException();
    public Task<Guid> ReissueAsync(ReissueCommand command) => throw new NotSupportedException();
    public Task<Guid> DelegateAsync(DelegateCommand command) => throw new NotSupportedException();
}

/// <summary>ATT's real services over any HrmDbContext (in-memory or LocalDB). Fixed clock; seeded; no holidays unless a test adds them.</summary>
internal sealed class AttHarness
{
    public const int Approver = 950;

    public Guid Org { get; }
    public HrmDbContext Db { get; }
    public FakeClock Clock { get; } = new();
    public FakeUsers Users { get; } = new();
    public FakeGate Gate { get; } = new();
    public FakeWorkflow Workflow { get; } = new();
    public IHrmSeeder Seeder { get; }
    public IHrmScopeService Scope { get; }
    public IEmployeeService Employees { get; }
    public IBranchService Branches { get; }
    public IDepartmentService Departments { get; }
    public IDesignationService Designations { get; }
    public IGradeService Grades { get; }
    public IHrmSettingsService Settings { get; }
    public IHrUserScopeService UserScopes { get; }
    public IAttendanceRecalculator Recalc { get; }
    public IShiftService Shifts { get; }
    public IHolidayService Holidays { get; }
    public IAttendanceService Attendance { get; }
    public IOvertimeService Overtime { get; }
    public AttendancePayInput Pay { get; }
    public ILeaveTypeService LeaveTypes { get; }
    public ILeaveService Leave { get; }
    public LeaveRequestStatusHandler Handler { get; }

    public AttHarness(HrmDbContext db, Guid org)
    {
        Db = db;
        Org = org;
        var aes = Options.Create(new AppSettings { AesEncryptionKey = HrmHarness.AesKey });
        var sensitive = new HrmSensitiveData(new AesEncryptionService(aes), aes);
        Seeder = new HrmSeeder(db);
        Scope = new HrmScopeService(db);
        var employment = new EmploymentRecordService(db, Scope);
        Employees = new EmployeeService(db, Scope, sensitive, Seeder, employment, Users);
        Branches = new BranchService(db);
        Departments = new DepartmentService(db);
        Designations = new DesignationService(db);
        Grades = new GradeService(db);
        Settings = new HrmSettingsService(db, Seeder);
        UserScopes = new HrUserScopeService(db);
        Recalc = new AttendanceRecalculator(db, Clock);
        Shifts = new ShiftService(db, Scope, employment, employment, Recalc);
        Holidays = new HolidayService(db, Recalc);
        Attendance = new AttendanceService(db, Scope, Recalc, Clock);
        Overtime = new OvertimeService(db, Scope, Clock);
        Pay = new AttendancePayInput(db, Clock, Gate);
        LeaveTypes = new LeaveTypeService(db);
        var applier = new LeaveDecisionApplier(db, Recalc);
        Handler = new LeaveRequestStatusHandler(db, applier);
        Workflow.Handler = Handler;
        Leave = new LeaveService(db, Scope, new HrmApprovalGateway(db, Workflow, Users), Workflow, applier, Clock);
        Users.Add(Approver, "Approver", P.HRM_LEAVE_APPROVE);
    }

    public static AttHarness Memory(string? dbName = null, Guid? org = null)
    {
        var id = org ?? Guid.NewGuid();
        var options = new DbContextOptionsBuilder<HrmDbContext>()
            .UseInMemoryDatabase(dbName ?? "att-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
        return new AttHarness(new HrmDbContext(options, new StaticTenantContext { OrganizationId = id }), id);
    }

    public HrmCaller Admin(int userId = 900) => new(Org, userId, P.HrmCodes, "HR Admin");
    public HrmCaller ApproverCaller() => new(Org, Approver, P.HrmCodes, "Approver");
    public HrmCaller As(int userId, params string[] permissions) => new(Org, userId, permissions, "User " + userId);

    public Guid Branch { get; private set; }
    public Guid Department { get; private set; }
    public Guid Designation { get; private set; }

    /// <summary>Seeds, drops the seeded holidays (each test states its calendar) and makes the structure.</summary>
    public async Task<AttHarness> ReadyAsync()
    {
        await Seeder.EnsureAsync(Org);
        Db.Holidays.RemoveRange(await Db.Holidays.IgnoreQueryFilters().Where(h => h.OrganizationId == Org).ToListAsync());
        await Db.SaveChangesAsync();
        var admin = Admin();
        Branch = (await Branches.CreateAsync(admin, new BranchUpsert { Code = "KHI", Name = "Karachi" })).Uuid;
        Department = (await Departments.CreateAsync(admin, new DepartmentUpsert { Code = "OPS", Name = "Operations", BranchUuid = Branch })).Uuid;
        Designation = (await Designations.CreateAsync(admin, new DesignationUpsert { Code = "CLERK", Name = "Clerk", Level = 1 })).Uuid;
        return this;
    }

    public async Task<EmployeeDetail> HireAsync(string first = "Ali", int? userId = null, string? gender = null, DateOnly? from = null, Guid? gradeUuid = null, Guid? shiftUuid = null, Guid? department = null)
    {
        if (userId is { } id && !Users.Users.ContainsKey(id)) Users.Add(id, first);
        return await Employees.CreateAsync(Admin(), new EmployeeUpsert
        {
            FirstName = first, LastName = "Test", UserId = userId, Gender = gender, JoinDate = from ?? new DateOnly(2026, 1, 1),
            Employment = new EmploymentRecordCreate
            {
                EffectiveFrom = from ?? new DateOnly(2026, 1, 1), DepartmentUuid = department ?? Department, DesignationUuid = Designation, BranchUuid = Branch, ChangeReason = "Hire",
                GradeUuid = gradeUuid, ShiftUuid = shiftUuid
            }
        });
    }

    /// <summary>A Karachi wall-clock time as UTC (UTC+5).</summary>
    public static DateTime Pkt(int month, int day, int hour, int minute = 0) => new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-5);

    public async Task PunchAsync(Guid employee, int month, int day, (int H, int M) @in, (int H, int M) @out)
    {
        await Attendance.RecordEventAsync(Admin(), new AttendanceEventRequest { EmployeeUuid = employee, EventTimeUtc = Pkt(month, day, @in.H, @in.M), EventType = "IN" });
        await Attendance.RecordEventAsync(Admin(), new AttendanceEventRequest { EmployeeUuid = employee, EventTimeUtc = Pkt(month, day, @out.H, @out.M), EventType = "OUT" });
    }

    public async Task<int> IdOfAsync(Guid employeeUuid) =>
        await Db.Employees.IgnoreQueryFilters().Where(e => e.OrganizationId == Org && e.Uuid == employeeUuid).Select(e => e.Id).FirstAsync();

    public async Task<Guid> LeaveTypeAsync(string code) =>
        await Db.LeaveTypes.IgnoreQueryFilters().Where(t => t.OrganizationId == Org && t.Code == code).Select(t => t.Uuid).FirstAsync();
}
