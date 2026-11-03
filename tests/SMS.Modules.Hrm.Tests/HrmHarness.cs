using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Modules.Hrm.Services;
using SMS.Shared.Common;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Tests;

internal sealed class FakeUsers : IOrgUserDirectory
{
    public Dictionary<int, OrgUserInfo> Users { get; } = [];
    public void Add(int id, string name, params string[] permissions) => Users[id] = new OrgUserInfo(id, name, $"u{id}@x.test", true, permissions);
    public Task<IReadOnlyList<OrgUserInfo>> GetUsersAsync(Guid organizationId, IReadOnlyCollection<int> userIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OrgUserInfo>>(userIds.Where(Users.ContainsKey).Select(i => Users[i]).ToList());
    public Task<IReadOnlyList<OrgUserInfo>> SearchAsync(Guid organizationId, string? search, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OrgUserInfo>>(Users.Values.ToList());
}

/// <summary>One organization on a fresh in-memory database, with real services. Build a second harness over the same <see cref="DbName"/> for another org.</summary>
internal sealed class HrmHarness
{
    public const string AesKey = "unit-test-key-not-a-real-secret";

    public Guid Org { get; }
    public string DbName { get; }
    public HrmDbContext Db { get; }
    public FakeUsers Users { get; } = new();
    public IHrmSensitiveData Sensitive { get; }
    public IHrmSeeder Seeder { get; }
    public IHrmScopeService Scope { get; }
    public IBranchService Branches { get; }
    public ICostCenterService CostCenters { get; }
    public IDepartmentService Departments { get; }
    public IDesignationService Designations { get; }
    public IGradeService Grades { get; }
    public IHrmSettingsService Settings { get; }
    public IHrUserScopeService UserScopes { get; }
    public IEmployeeService Employees { get; }
    public IEmploymentRecordService Employment { get; }
    public IEmployeeBankAccountService Bank { get; }
    public IHrmEmployeeDirectory Directory { get; }

    public HrmHarness(Guid? org = null, string? dbName = null)
    {
        Org = org ?? Guid.NewGuid();
        DbName = dbName ?? "hrm-" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<HrmDbContext>()
            .UseInMemoryDatabase(DbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        Db = new HrmDbContext(options, new StaticTenantContext { OrganizationId = Org });
        var encryption = new AesEncryptionService(Options.Create(new AppSettings { AesEncryptionKey = AesKey }));
        Sensitive = new HrmSensitiveData(encryption, Options.Create(new AppSettings { AesEncryptionKey = AesKey }));
        Seeder = new HrmSeeder(Db);
        Scope = new HrmScopeService(Db);
        Branches = new BranchService(Db);
        CostCenters = new CostCenterService(Db, Seeder);
        Departments = new DepartmentService(Db);
        Designations = new DesignationService(Db);
        Grades = new GradeService(Db);
        Settings = new HrmSettingsService(Db, Seeder);
        UserScopes = new HrUserScopeService(Db);
        var employment = new EmploymentRecordService(Db, Scope);
        Employment = employment;
        Employees = new EmployeeService(Db, Scope, Sensitive, Seeder, employment, Users);
        Bank = new EmployeeBankAccountService(Db, Scope, Sensitive);
        Directory = new HrmEmployeeDirectory(Db, Sensitive, Seeder);
    }

    /// <summary>A caller holding every HR code, all-employee scope.</summary>
    public HrmCaller Admin(int userId = 900) => new(Org, userId, P.HrmCodes, "HR Admin");
    public HrmCaller As(int userId, params string[] permissions) => new(Org, userId, permissions, "User " + userId);

    /// <summary>The structure most tests need.</summary>
    public async Task<Base> BaseAsync()
    {
        var admin = Admin();
        var branch = await Branches.CreateAsync(admin, new BranchUpsert { Code = "KHI", Name = "Karachi" });
        var branch2 = await Branches.CreateAsync(admin, new BranchUpsert { Code = "LHE", Name = "Lahore" });
        var center = await CostCenters.CreateAsync(admin, new CostCenterUpsert { Code = "OPS", Name = "Operations" });
        var ops = await Departments.CreateAsync(admin, new DepartmentUpsert { Code = "OPS", Name = "Operations", CostCenterUuid = center.Uuid, BranchUuid = branch.Uuid });
        var field = await Departments.CreateAsync(admin, new DepartmentUpsert { Code = "FIELD", Name = "Field", ParentUuid = ops.Uuid });
        var sales = await Departments.CreateAsync(admin, new DepartmentUpsert { Code = "SALES", Name = "Sales" });
        var clerk = await Designations.CreateAsync(admin, new DesignationUpsert { Code = "CLERK", Name = "Clerk", Level = 1 });
        var boss = await Designations.CreateAsync(admin, new DesignationUpsert { Code = "BOSS", Name = "Manager", Level = 5 });
        return new Base(branch.Uuid, branch2.Uuid, center.Uuid, ops.Uuid, field.Uuid, sales.Uuid, clerk.Uuid, boss.Uuid);
    }

    public sealed record Base(Guid Karachi, Guid Lahore, Guid CostCenter, Guid Ops, Guid Field, Guid Sales, Guid Clerk, Guid Boss);

    public EmploymentRecordCreate Hire(Base b, DateOnly? from = null, Guid? department = null, Guid? branch = null, Guid? manager = null) => new()
    {
        EffectiveFrom = from ?? new DateOnly(2026, 1, 1), DepartmentUuid = department ?? b.Ops, DesignationUuid = b.Clerk, BranchUuid = branch ?? b.Karachi, ManagerUuid = manager,
        ChangeReason = "Hire"
    };

    public async Task<EmployeeDetail> EmployeeAsync(Base b, string first = "Ali", string last = "Khan", int? userId = null, Guid? department = null, Guid? branch = null, Guid? manager = null, string? cnic = null)
    {
        if (userId is { } id && !Users.Users.ContainsKey(id)) Users.Add(id, first);
        return await Employees.CreateAsync(Admin(), new EmployeeUpsert
        {
            FirstName = first, LastName = last, UserId = userId, Cnic = cnic, Employment = Hire(b, department: department, branch: branch, manager: manager)
        });
    }
}
