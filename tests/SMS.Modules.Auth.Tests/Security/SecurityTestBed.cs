using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Models;
using SMS.Modules.Auth.Repositories;
using SMS.Modules.Auth.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;

namespace SMS.Modules.Auth.Tests.Security;

/// <summary>
/// One in-memory Auth database with two organizations, the built-in global roles that matter for escalation
/// (System Admin carries the platform codes, Org Admin does not), a custom role in each organization, and a
/// platform super admin. Each request is simulated the way production runs it: a fresh DbContext and
/// AuthService whose tenant context matches the caller (anonymous = filter bypassed), sharing one
/// reset-code throttle the way the real singleton is shared.
/// </summary>
internal sealed class SecurityTestBed
{
    public static readonly Guid OrgA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    public static readonly Guid OrgB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    // Permission ids
    public const int SystemConfigure = 1, UserManage = 2, PlatformSuperAdmin = 3, PoView = 4, PoApprove = 5, SaleOrderConfigWrite = 6;

    // Role ids — 1/7/10/11 are the built-in global ones; 20 belongs to org A, 21 to org B (and, like ORG-PSO's
    // hand-made "Test Role", carries PLATFORM_SUPER_ADMIN).
    public const int SystemAdminRole = 1, RequesterRole = 7, OrgAdminRole = 10, SupplyDeptAdminRole = 11, OrgACustomRole = 20, OrgBPowerRole = 21;

    // User ids
    public const int SuperAdminId = 1, OrgAdminAId = 100, RequesterAId = 101, OtherAdminAId = 102, UserBId = 200;
    public const string Password = "Original#Pass1";

    private static readonly object HangfireGate = new();
    private static bool _hangfireReady;

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly PasswordHasher<UserAccount> _hasher = new();

    public PasswordResetThrottle Throttle { get; } = new();
    public Mock<IEmailService> Email { get; } = new();

    public SecurityTestBed()
    {
        lock (HangfireGate)
        {
            // Password reset and admin reset enqueue an e-mail job; any storage will do, nothing runs it.
            if (!_hangfireReady) { GlobalConfiguration.Configuration.UseInMemoryStorage(); _hangfireReady = true; }
        }
        Seed();
    }

    public static AuthCaller Caller(int userId, Guid organizationId, bool isSuperAdmin = false, params string[] permissions) =>
        new(userId, organizationId, isSuperAdmin, permissions.ToHashSet(StringComparer.Ordinal));

    /// <summary>Org A's admin: every business permission an Org Admin has, none of the platform ones.</summary>
    public static AuthCaller OrgAdminA => Caller(OrgAdminAId, OrgA, false, PermissionCodes.USER_MANAGE, PermissionCodes.PO_VIEW, PermissionCodes.PO_APPROVE);

    public static AuthCaller SuperAdmin => Caller(SuperAdminId, OrgA, true,
        PermissionCodes.SYSTEM_CONFIGURE, PermissionCodes.USER_MANAGE, PermissionCodes.PLATFORM_SUPER_ADMIN, PermissionCodes.PO_VIEW, PermissionCodes.PO_APPROVE);

    public AuthDbContext Db(Guid organizationId, bool bypassFilter) =>
        new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(_dbName).Options,
            new StaticTenantContext { OrganizationId = organizationId, IsSuperAdmin = bypassFilter });

    /// <summary>What an anonymous request sees: no tenant, so the filter is bypassed (see TenantContext).</summary>
    public AuthDbContext AnonymousDb() => Db(TenantDefaults.ScmDemoOrganizationId, bypassFilter: true);

    public AuthService Service(AuthDbContext db)
    {
        var settings = Options.Create(new AppSettings { Secret = "security-test-bed-secret-at-least-32-bytes!" });
        var orgStatus = new Mock<IOrganizationStatusService>();
        orgStatus.Setup(x => x.IsOrganizationActiveAsync(It.IsAny<Guid>())).ReturnsAsync(true);
        var superAdmins = new Mock<ISuperAdminService>();
        superAdmins.Setup(x => x.IsSuperAdminAsync(It.IsAny<int>())).ReturnsAsync((int id) => id == SuperAdminId);
        return new AuthService(new AuthRepository(db, _hasher), Email.Object, settings, new TokenService(settings), _hasher,
            orgStatus.Object, superAdmins.Object, Throttle);
    }

    /// <summary>A request made by <paramref name="caller"/>: its DbContext is scoped exactly as the caller's token would scope it.</summary>
    public async Task<T> As<T>(AuthCaller caller, Func<AuthService, Task<T>> call)
    {
        await using var db = Db(caller.OrganizationId, caller.IsSuperAdmin);
        return await call(Service(db));
    }

    public async Task As(AuthCaller caller, Func<AuthService, Task> call)
    {
        await using var db = Db(caller.OrganizationId, caller.IsSuperAdmin);
        await call(Service(db));
    }

    /// <summary>An anonymous request (forgot/reset password).</summary>
    public T Anonymous<T>(Func<AuthService, T> call)
    {
        using var db = AnonymousDb();
        return call(Service(db));
    }

    /// <summary>Reads the database as it really is — no filter, nothing tracked from an earlier request.</summary>
    public T Read<T>(Func<AuthDbContext, T> read)
    {
        using var db = AnonymousDb();
        return read(db);
    }

    public void Write(Action<AuthDbContext> write)
    {
        using var db = AnonymousDb();
        write(db);
        db.SaveChanges();
    }

    public UserAccount User(int userId) => Read(db => db.UserAccounts.IgnoreQueryFilters().AsNoTracking().Single(u => u.UserID == userId));

    private void Seed() => Write(db =>
    {
        db.Permissions.AddRange(
            new Permission { PermissionID = SystemConfigure,      Name = "Configure System",  Code = PermissionCodes.SYSTEM_CONFIGURE },
            new Permission { PermissionID = UserManage,           Name = "Manage Users",      Code = PermissionCodes.USER_MANAGE },
            new Permission { PermissionID = PlatformSuperAdmin,   Name = "Platform",          Code = PermissionCodes.PLATFORM_SUPER_ADMIN },
            new Permission { PermissionID = PoView,               Name = "View POs",          Code = PermissionCodes.PO_VIEW },
            new Permission { PermissionID = PoApprove,            Name = "Approve POs",       Code = PermissionCodes.PO_APPROVE },
            new Permission { PermissionID = SaleOrderConfigWrite, Name = "Change SO config",  Code = PermissionCodes.SALE_ORDER_CONFIG_WRITE });

        db.Roles.AddRange(
            new Role { RoleID = SystemAdminRole,     Name = "System Admin",        RoleCode = "SYSTEM_ADMIN",      IsGlobal = true },
            new Role { RoleID = RequesterRole,       Name = "Requester",           RoleCode = "REQUESTER",         IsGlobal = true },
            new Role { RoleID = OrgAdminRole,        Name = "Organization Admin",  RoleCode = "ORG_ADMIN",         IsGlobal = true },
            new Role { RoleID = SupplyDeptAdminRole, Name = "Supply Dept Admin",   RoleCode = "SUPPLY_DEPT_ADMIN", IsGlobal = true },
            new Role { RoleID = OrgACustomRole,      Name = "Org A Buyer",         RoleCode = "ORG_A_BUYER",       IsGlobal = false, OrganizationId = OrgA },
            new Role { RoleID = OrgBPowerRole,       Name = "Org B Test Role",     RoleCode = "ORG_B_TEST",        IsGlobal = false, OrganizationId = OrgB });

        void Grant(int roleId, params int[] permissionIds)
        {
            foreach (var p in permissionIds)
                db.RolePermissions.Add(new RolePermission { RoleID = roleId, PermissionID = p, IsAllowed = true, OrganizationId = TenantDefaults.ScmDemoOrganizationId });
        }
        Grant(SystemAdminRole, SystemConfigure, UserManage, PlatformSuperAdmin, PoView, PoApprove);
        Grant(RequesterRole, PoView);
        Grant(OrgAdminRole, UserManage, PoView, PoApprove);
        Grant(SupplyDeptAdminRole, SaleOrderConfigWrite);
        Grant(OrgACustomRole, PoView);
        Grant(OrgBPowerRole, PlatformSuperAdmin, UserManage);

        UserAccount U(int id, string first, string email, int roleId, Guid org)
        {
            var u = new UserAccount
            {
                UserID = id, FirstName = first, LastName = "Test", Email = email, Phone = "555-0100", Address = "1 Test St",
                RoleID = roleId, OrganizationId = org, IsActive = true, IsDelete = false, CreatedBy = 0,
                CreatedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), SupplierType = "INTERNAL"
            };
            u.Password = _hasher.HashPassword(u, Password);
            return u;
        }
        db.UserAccounts.AddRange(
            U(SuperAdminId,  "System", "admin@sms.local",       SystemAdminRole, OrgA),
            U(OrgAdminAId,   "Olivia", "olivia.admin@a.test",   OrgAdminRole,    OrgA),
            U(RequesterAId,  "Rita",   "rita.requester@a.test", RequesterRole,   OrgA),
            U(OtherAdminAId, "Oscar",  "oscar.admin@a.test",    OrgAdminRole,    OrgA),
            U(UserBId,       "Bruno",  "bruno@b.test",          RequesterRole,   OrgB));
    });
}
