using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Hrm.Services.Tal;

public interface ITrainingService
{
    Task<IReadOnlyList<CourseModel>> ListCoursesAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default);
    Task<CourseModel> GetCourseAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<CourseModel> CreateCourseAsync(HrmCaller caller, CourseUpsert request, CancellationToken ct = default);
    Task<CourseModel> UpdateCourseAsync(HrmCaller caller, Guid uuid, CourseUpsert request, CancellationToken ct = default);

    Task<IReadOnlyList<EnrollmentModel>> ListEnrollmentsAsync(HrmCaller caller, Guid? courseUuid, Guid? employeeUuid, CancellationToken ct = default);
    Task<IReadOnlyList<EnrollmentModel>> ListOwnEnrollmentsAsync(HrmCaller caller, CancellationToken ct = default);
    Task<EnrollmentModel> EnrollAsync(HrmCaller caller, EnrollmentCreate request, CancellationToken ct = default);
    Task<EnrollmentModel> CompleteAsync(HrmCaller caller, Guid uuid, EnrollmentComplete request, CancellationToken ct = default);
    Task<EnrollmentModel> CancelAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
}

internal sealed class TrainingService : HrmServiceBase, ITrainingService
{
    private const int ListCap = 500;

    private readonly IHrmScopeService _scope;

    private readonly IHrmAttachmentGuard? _attachments;

    public TrainingService(HrmDbContext db, IHrmScopeService scope, IHrmAttachmentGuard? attachments = null) : base(db)
    {
        _scope = scope;
        _attachments = attachments;
    }

    private static CourseModel Map(Course c) => new()
    {
        Uuid = c.Uuid, Code = c.Code, Name = c.Name, Description = c.Description, Provider = c.Provider, DurationHours = c.DurationHours, Cost = c.Cost, IsActive = c.IsActive
    };

    public async Task<IReadOnlyList<CourseModel>> ListCoursesAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var rows = await Db.Courses.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == org && (includeInactive || c.IsActive)).OrderBy(c => c.Name).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    private async Task<Course> RequireCourseAsync(Guid org, Guid uuid, CancellationToken ct) =>
        await Db.Courses.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.OrganizationId == org && c.Uuid == uuid, ct) ?? throw new NotFoundException("Course", uuid);

    public async Task<CourseModel> GetCourseAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default) => Map(await RequireCourseAsync(caller.OrganizationId, uuid, ct));

    private async Task ApplyAsync(Course row, CourseUpsert r, Guid org, CancellationToken ct)
    {
        var code = HrmValidate.Code(r.Code);
        if (await Db.Courses.IgnoreQueryFilters().AnyAsync(c => c.OrganizationId == org && c.Code == code && c.Id != row.Id, ct))
            throw HrmErrors.Conflict("COURSE_CODE_TAKEN", $"Course code {code} is already in use.");
        row.Code = code;
        row.Name = HrmValidate.Name(r.Name, 200, "Name");
        row.Description = HrmValidate.Optional(r.Description, 2000, "Description");
        row.Provider = HrmValidate.Optional(r.Provider, 200, "Provider");
        if (r.DurationHours < 0) throw new BadRequestException("The duration cannot be negative.");
        if (r.Cost < 0) throw new BadRequestException("The cost cannot be negative.");
        row.DurationHours = r.DurationHours;
        row.Cost = r.Cost;
        row.IsActive = r.IsActive ?? row.IsActive;
    }

    private async Task SaveCourseAsync(string code, CancellationToken ct)
    {
        try
        {
            await SaveAsync(ct, "This course");
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            throw HrmErrors.Conflict("COURSE_CODE_TAKEN", $"Course code {code} is already in use.");
        }
    }

    public async Task<CourseModel> CreateCourseAsync(HrmCaller caller, CourseUpsert request, CancellationToken ct = default)
    {
        var row = new Course();
        await ApplyAsync(row, request, caller.OrganizationId, ct);
        Stamp(row, caller, isNew: true);
        Db.Courses.Add(row);
        await SaveCourseAsync(row.Code, ct);
        return Map(row);
    }

    public async Task<CourseModel> UpdateCourseAsync(HrmCaller caller, Guid uuid, CourseUpsert request, CancellationToken ct = default)
    {
        var row = await RequireCourseAsync(caller.OrganizationId, uuid, ct);
        await ApplyAsync(row, request, caller.OrganizationId, ct);
        Stamp(row, caller);
        await SaveCourseAsync(row.Code, ct);
        return Map(row);
    }

    private IQueryable<EnrollmentModel> Project(IQueryable<CourseEnrollment> q) => q.AsNoTracking().Select(e => new EnrollmentModel
    {
        Uuid = e.Uuid, EmployeeUuid = e.Employee!.Uuid, EmployeeName = e.Employee.FirstName + " " + e.Employee.LastName, CourseUuid = e.Course!.Uuid, CourseName = e.Course.Name,
        EnrolledDate = e.EnrolledDate, CompletedDate = e.CompletedDate, Status = e.Status, CertificateAttachmentUuid = e.CertificateAttachmentUuid
    });

    private IQueryable<CourseEnrollment> Enrollments(Guid org) => Db.CourseEnrollments.IgnoreQueryFilters().Where(e => e.OrganizationId == org);

    public async Task<IReadOnlyList<EnrollmentModel>> ListEnrollmentsAsync(HrmCaller caller, Guid? courseUuid, Guid? employeeUuid, CancellationToken ct = default)
    {
        var scope = await _scope.GetAsync(caller, ct);
        var employees = _scope.Apply(Db.Employees, caller, scope).Select(e => e.Id);
        var q = Enrollments(caller.OrganizationId).Where(e => employees.Contains(e.EmployeeId));
        if (courseUuid is { } course) q = q.Where(e => e.Course!.Uuid == course);
        if (employeeUuid is { } employee) q = q.Where(e => e.Employee!.Uuid == employee);
        return await Project(q.OrderByDescending(e => e.Id).Take(ListCap)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EnrollmentModel>> ListOwnEnrollmentsAsync(HrmCaller caller, CancellationToken ct = default)
    {
        var own = await _scope.RequireOwnEmployeeAsync(caller, ct);
        return await Project(Enrollments(caller.OrganizationId).Where(e => e.EmployeeId == own.Id).OrderByDescending(e => e.Id)).ToListAsync(ct);
    }

    private Task<EnrollmentModel> ModelAsync(Guid org, Guid uuid, CancellationToken ct) => Project(Enrollments(org).Where(e => e.Uuid == uuid)).FirstAsync(ct);

    public async Task<EnrollmentModel> EnrollAsync(HrmCaller caller, EnrollmentCreate request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var employee = await _scope.RequireEmployeeAsync(caller, request.EmployeeUuid ?? throw new BadRequestException("Employee is required."), ct);
        if (employee.Status is EmployeeStatuses.Terminated or EmployeeStatuses.Archived)
            throw HrmErrors.Conflict("ENROLLMENT_EMPLOYEE_INACTIVE", $"This employee is {employee.Status}.");
        var course = await RequireCourseAsync(org, request.CourseUuid ?? throw new BadRequestException("Course is required."), ct);
        if (!course.IsActive) throw HrmErrors.Conflict("COURSE_INACTIVE", "This course is inactive.");
        if (await Enrollments(org).AnyAsync(e => e.EmployeeId == employee.Id && e.CourseId == course.Id && e.Status == EnrollmentStatuses.Enrolled, ct))
            throw HrmErrors.Conflict("ENROLLMENT_DUPLICATE", "This employee is already enrolled in this course.");

        var date = request.EnrolledDate ?? HrmValidate.Today;
        var sameDay = await Enrollments(org).FirstOrDefaultAsync(e => e.EmployeeId == employee.Id && e.CourseId == course.Id && e.EnrolledDate == date, ct);
        if (sameDay is { Status: EnrollmentStatuses.Completed })
            throw HrmErrors.Conflict("ENROLLMENT_DUPLICATE", "This employee already completed this course from an enrollment on that date.");

        // The unique index is (employee, course, date), so enrolling again the day it was cancelled reuses the row.
        var row = sameDay;
        if (row is not null)
        {
            row.Status = EnrollmentStatuses.Enrolled;
            Stamp(row, caller);
        }
        else
        {
            row = new CourseEnrollment { EmployeeId = employee.Id, CourseId = course.Id, EnrolledDate = date };
            Stamp(row, caller, isNew: true);
            Db.CourseEnrollments.Add(row);
        }
        try
        {
            await SaveAsync(ct, "This enrollment");
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            throw HrmErrors.Conflict("ENROLLMENT_DUPLICATE", "This employee is already enrolled in this course.");
        }
        return await ModelAsync(org, row.Uuid, ct);
    }

    private async Task<CourseEnrollment> RequireEnrollmentAsync(HrmCaller caller, Guid uuid, CancellationToken ct)
    {
        var row = await Enrollments(caller.OrganizationId).FirstOrDefaultAsync(e => e.Uuid == uuid, ct) ?? throw new NotFoundException("Enrollment", uuid);
        if (!await _scope.CanAccessAsync(caller, row.EmployeeId, ct)) throw new NotFoundException("Enrollment", uuid);
        return row;
    }

    public async Task<EnrollmentModel> CompleteAsync(HrmCaller caller, Guid uuid, EnrollmentComplete request, CancellationToken ct = default)
    {
        var row = await RequireEnrollmentAsync(caller, uuid, ct);
        if (row.Status != EnrollmentStatuses.Enrolled) throw HrmErrors.Conflict("ENROLLMENT_NOT_ACTIVE", $"This enrollment is {row.Status}.");
        var completed = request.CompletedDate ?? throw new BadRequestException("The completion date is required.");
        if (completed < row.EnrolledDate) throw HrmErrors.Bad("ENROLLMENT_DATE_INVALID", "A course cannot be completed before the enrollment date.");
        if (completed > HrmValidate.Today) throw HrmErrors.Bad("ENROLLMENT_DATE_INVALID", "The completion date cannot be in the future.");
        var certificate = await _attachments.CheckedAsync(caller, request.CertificateAttachmentUuid, HrmAttachmentCodes.Enrollment);
        row.CompletedDate = completed;
        row.Status = EnrollmentStatuses.Completed;
        row.CertificateAttachmentUuid = certificate;
        Stamp(row, caller);
        await SaveAsync(ct, "This enrollment");
        return await ModelAsync(caller.OrganizationId, uuid, ct);
    }

    public async Task<EnrollmentModel> CancelAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await RequireEnrollmentAsync(caller, uuid, ct);
        if (row.Status != EnrollmentStatuses.Enrolled) throw HrmErrors.Conflict("ENROLLMENT_NOT_ACTIVE", $"This enrollment is {row.Status}.");
        row.Status = EnrollmentStatuses.Cancelled;
        Stamp(row, caller);
        await SaveAsync(ct, "This enrollment");
        return await ModelAsync(caller.OrganizationId, uuid, ct);
    }
}
