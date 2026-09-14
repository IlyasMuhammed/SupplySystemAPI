using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Auth.Repositories;

internal sealed class AuthRepository : IAuthRepository
{
    private readonly AuthDbContext _db;
    private readonly IPasswordHasher<UserAccount> _hasher;

    public AuthRepository(AuthDbContext db, IPasswordHasher<UserAccount> hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    // ── Existing synchronous methods ──────────────────────────────────────────

    public UserAccount? AuthenticateUserAccount(LoginVM model)
    {
        var account = _db.UserAccounts.FirstOrDefault(u => u.Email == model.Username && u.IsActive);
        if (account == null) return null;
        var result = _hasher.VerifyHashedPassword(account, account.Password, model.Password);
        return result == PasswordVerificationResult.Success ? account : null;
    }

    public UserAccount? FindByEmail(string email) =>
        _db.UserAccounts.FirstOrDefault(u => u.Email == email.Trim() && u.IsActive);

    public int ActivationTokenValidation(string token)
    {
        var account = _db.UserAccounts.FirstOrDefault(x => x.AccountActivationToken == token);
        if (account == null) return 0;
        if (account.AccountActivationTokenTime < DateTime.UtcNow) return -1;
        account.AccountActivationToken = null;
        account.IsActive = true;
        account.UpdateDate = DateTime.UtcNow;
        _db.SaveChanges();
        return 1;
    }

    public int SetPasswordVerification(ForgotPasswordModel dto)
    {
        var account = _db.UserAccounts.FirstOrDefault(u =>
            u.Email == dto.Email.Trim() && u.IsActive && u.PasswordResetToken == dto.verificationCode);
        if (account == null) return 0;
        if (account.PasswordResetTokenTime < DateTime.UtcNow) return -1;
        account.Password = _hasher.HashPassword(account, dto.Password);
        account.UpdateDate = DateTime.UtcNow;
        account.PasswordResetToken = null;
        _db.SaveChanges();
        return 1;
    }

    public int CreateUserAccount(UserAccountModel model)
    {
        var user = new UserAccount
        {
            Email = model.Email,
            Address = model.Address,
            CreatedBy = 1,
            FirstName = model.FirstName,
            LastName = model.LastName,
            MiddleName = model.MiddleName,
            IsActive = false,
            IsDelete = false,
            Phone = model.Phone,
            AccountActivationToken = model.Token,
            AccountActivationTokenTime = DateTime.UtcNow.AddMinutes(60),
            RoleID = (int)EnumRole.Requester,
            ZipCode = model.ZipCode,
            CreatedDate = DateTime.UtcNow
        };
        user.Password = _hasher.HashPassword(user, model.Password);
        _db.UserAccounts.Add(user);
        _db.SaveChanges();
        return 1;
    }

    public int UpdateUserAccount(UserAccount userAccount)
    {
        _db.UserAccounts.Update(userAccount);
        _db.SaveChanges();
        return 1;
    }

    public int UpdatePersonalInformation(UpdatePersonalInfoModel dto)
    {
        var account = _db.UserAccounts.FirstOrDefault(x => x.UserID == dto.UserID);
        if (account == null) return -1;
        account.FirstName = dto.FirstName;
        account.LastName = dto.LastName;
        account.Email = dto.Email;
        account.Phone = dto.Phone;
        account.PaymentMethod = dto.PaymentMethodId;
        account.UpdateDate = DateTime.UtcNow;
        _db.SaveChanges();
        return 1;
    }

    public int UpdatePasswordInformation(UpdatePasswordModel dto)
    {
        var account = _db.UserAccounts.FirstOrDefault(x => x.UserID == dto.UserID);
        if (account == null) return -1;
        var verified = _hasher.VerifyHashedPassword(account, account.Password, dto.CurrentPassword);
        if (verified != PasswordVerificationResult.Success) return -3;
        account.Password = _hasher.HashPassword(account, dto.NewPassword);
        account.UpdateDate = DateTime.UtcNow;
        _db.SaveChanges();
        return 1;
    }

    public PaginatedResponse<UserAccountModel> GetAllUsers(int page, int pageSize)
    {
        var query = _db.UserAccounts.Where(x => !x.IsDelete);
        var total = query.Count();
        var users = query
            .OrderByDescending(x => x.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new UserAccountModel
            {
                UserID = u.UserID,
                FirstName = u.FirstName,
                MiddleName = u.MiddleName,
                LastName = u.LastName,
                Email = u.Email,
                Phone = u.Phone,
                Address = u.Address,
                IsActive = u.IsActive,
                CreatedDate = u.CreatedDate
            }).ToList();

        return new PaginatedResponse<UserAccountModel>
        {
            Data = users,
            TotalRecords = total,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    public int InactiveUser(int userId)
    {
        var user = _db.UserAccounts.FirstOrDefault(x => x.UserID == userId && !x.IsDelete);
        if (user == null) return 0;
        user.IsActive = false;
        user.UpdateDate = DateTime.UtcNow;
        _db.SaveChanges();
        return 1;
    }

    public List<PermissionModel> GetPermissionsByRole(int roleId)
    {
        return (from p in _db.Permissions
                join rp in _db.RolePermissions.Where(x => x.RoleID == roleId)
                on p.PermissionID equals rp.PermissionID into grp
                from rp in grp.DefaultIfEmpty()
                select new PermissionModel
                {
                    PermissionID = p.PermissionID,
                    Name = p.Name,
                    Code = p.Code,
                    IsAllowed = rp != null && rp.IsAllowed
                }).ToList();
    }

    public int SaveRolePermissions(int roleId, List<PermissionModel> permissions)
    {
        var existing = _db.RolePermissions.Where(x => x.RoleID == roleId).ToList();
        foreach (var perm in permissions)
        {
            var ex = existing.FirstOrDefault(x => x.PermissionID == perm.PermissionID);
            if (ex != null) { ex.IsAllowed = perm.IsAllowed; }
            else { _db.RolePermissions.Add(new RolePermission { RoleID = roleId, PermissionID = perm.PermissionID, IsAllowed = perm.IsAllowed, OrganizationId = TenantDefaults.ScmDemoOrganizationId }); }
        }
        _db.SaveChanges();
        return 1;
    }

    public int SaveUserPermissions(int userId, List<PermissionModel> permissions)
    {
        var existing = _db.UserPermissions.Where(x => x.UserID == userId).ToList();
        foreach (var perm in permissions)
        {
            var ex = existing.FirstOrDefault(x => x.PermissionID == perm.PermissionID);
            if (ex != null) { ex.IsAllowed = perm.IsAllowed; }
            else { _db.UserPermissions.Add(new UserPermission { UserID = userId, PermissionID = perm.PermissionID, IsAllowed = perm.IsAllowed }); }
        }
        _db.SaveChanges();
        return 1;
    }

    public List<PermissionModel> GetUserPermissions(int userId)
    {
        var user = _db.UserAccounts.FirstOrDefault(x => x.UserID == userId);
        if (user == null) return new List<PermissionModel>();

        var allPermissions = _db.Permissions.ToList();
        var rolePerms = _db.RolePermissions.Where(rp => rp.RoleID == user.RoleID).ToList();
        var userPerms = _db.UserPermissions.Where(up => up.UserID == userId).ToList();

        return allPermissions.Select(p =>
        {
            var userOverride = userPerms.FirstOrDefault(up => up.PermissionID == p.PermissionID);
            var rolePerm = rolePerms.FirstOrDefault(rp => rp.PermissionID == p.PermissionID);
            return new PermissionModel
            {
                PermissionID = p.PermissionID,
                Name = p.Name,
                Code = p.Code,
                Description = p.Description,
                IsAllowed = userOverride?.IsAllowed ?? rolePerm?.IsAllowed ?? false
            };
        }).ToList();
    }

    // ── Async methods for login / token / session flows ───────────────────────

    public async Task<UserAccount?> FindUserForLoginAsync(string email)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return await _db.UserAccounts
            .FirstOrDefaultAsync(u => u.Email == normalized && !u.IsDelete);
    }

    public async Task<UserAccount?> FindUserByIdAsync(int userId) =>
        await _db.UserAccounts.FindAsync(userId);

    public async Task<UserAccount?> FindUserByInviteTokenAsync(string token) =>
        await _db.UserAccounts.FirstOrDefaultAsync(u => u.InviteToken == token && !u.IsDelete);

    public async Task SaveAsync(UserAccount user)
    {
        _db.UserAccounts.Update(user);
        await _db.SaveChangesAsync();
    }

    public async Task<string> GetRoleNameAsync(int roleId)
    {
        var role = await _db.Roles.FindAsync(roleId);
        return role?.Name ?? string.Empty;
    }

    public async Task<List<string>> GetAllowedPermissionsAsync(int userId)
    {
        var user = await _db.UserAccounts.FindAsync(userId);
        if (user == null) return [];

        var allPerms = await _db.Permissions.ToListAsync();
        var rolePerms = await _db.RolePermissions.Where(rp => rp.RoleID == user.RoleID).ToListAsync();
        var userPerms = await _db.UserPermissions.Where(up => up.UserID == userId).ToListAsync();

        return allPerms
            .Where(p =>
            {
                var userOverride = userPerms.FirstOrDefault(up => up.PermissionID == p.PermissionID);
                var rolePerm = rolePerms.FirstOrDefault(rp => rp.PermissionID == p.PermissionID);
                return userOverride?.IsAllowed ?? rolePerm?.IsAllowed ?? false;
            })
            .Select(p => p.Code)
            .ToList();
    }

    public async Task<UserSession?> FindActiveSessionByHashAsync(string tokenHash) =>
        await _db.UserSessions
            .FirstOrDefaultAsync(s =>
                s.TokenHash == tokenHash &&
                s.RevokedAt == null &&
                s.ExpiresAt > DateTime.UtcNow);

    public async Task AddSessionAsync(UserSession session)
    {
        _db.UserSessions.Add(session);
        await _db.SaveChangesAsync();
    }

    public async Task RevokeSessionAsync(Guid sessionId)
    {
        var session = await _db.UserSessions.FindAsync(sessionId);
        if (session != null)
        {
            session.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task<int> DeleteExpiredSessionsAsync()
    {
        var expired = await _db.UserSessions
            .Where(s => s.ExpiresAt <= DateTime.UtcNow)
            .ToListAsync();
        _db.UserSessions.RemoveRange(expired);
        await _db.SaveChangesAsync();
        return expired.Count;
    }

    public async Task UpdateProfilePictureAsync(int userId, string? pictureUrl)
    {
        var user = await _db.UserAccounts.FirstOrDefaultAsync(u => u.UserID == userId && !u.IsDelete);
        if (user == null) return;
        user.ProfilePictureUrl = pictureUrl;
        user.UpdateDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ── User management ───────────────────────────────────────────────────────

    public async Task<bool> EmailExistsAsync(string email)
    {
        var normalized = email.Trim().ToLowerInvariant();
        // Global, not scoped to the caller's org: unlike most UserAccount queries, this backs
        // AdminCreateUserAsync (an Org Admin creating a user within their own org, an
        // authenticated, non-bypassed context) — login resolves email with no org selector
        // (FindUserForLoginAsync), so email must stay unique across every org, not just the
        // creating admin's own. Without this, two orgs could each create the same email and
        // whichever user FindUserForLoginAsync happens to return first would silently shadow
        // the other's account.
        //
        // Deliberately does NOT exclude IsDelete=1 rows: IX_UserAccounts_Email is a plain unique
        // index with no filter, so a soft-deleted user's email still physically blocks a new
        // INSERT at the database level regardless of what this check decides. Excluding deleted
        // rows here used to let AdminCreateUserAsync walk straight into that constraint and surface
        // a raw SqlException as a generic 500 instead of the friendly "account already exists"
        // BadRequestException this check exists to produce.
        return await _db.UserAccounts.IgnoreQueryFilters()
            .AnyAsync(u => u.Email == normalized);
    }

    public async Task<UserDetailModel> CreateUserAsync(UserAccount user, List<Guid>? supplierIds, int createdBy)
    {
        _db.UserAccounts.Add(user);
        await _db.SaveChangesAsync();

        if (supplierIds is { Count: > 0 })
            await SaveUserSupplierAccessAsync(user.UserID, supplierIds, createdBy);

        var role = await _db.Roles.FindAsync(user.RoleID);
        return new UserDetailModel
        {
            UserID = user.UserID,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Email = user.Email,
            Phone = user.Phone,
            Address = user.Address,
            Department = user.Department,
            IsActive = user.IsActive,
            CreatedDate = user.CreatedDate,
            Role = new DropDownVM { ID = user.RoleID, Value = role?.Name ?? string.Empty },
            SupplierType = user.SupplierType,
            SupplierIds = supplierIds ?? []
        };
    }

    // ── User↔Supplier access mapping (REQ-2.x) ───────────────────────────────────

    public async Task<List<Guid>> GetUserSupplierIdsAsync(int userId) =>
        await _db.UserSupplierAccess.Where(m => m.UserID == userId).Select(m => m.SupplierId).ToListAsync();

    // True full replace — deletes every existing mapping for this user and inserts the new set,
    // so a supplier dropped from the incoming list actually loses access immediately. Deliberately
    // not the SaveUserPermissions "merge, never delete" pattern (see file header comment there).
    public async Task SaveUserSupplierAccessAsync(int userId, List<Guid> supplierIds, int assignedBy)
    {
        var existing = await _db.UserSupplierAccess.Where(m => m.UserID == userId).ToListAsync();
        _db.UserSupplierAccess.RemoveRange(existing);

        var now = DateTime.UtcNow;
        foreach (var supplierId in supplierIds.Distinct())
        {
            _db.UserSupplierAccess.Add(new UserSupplierAccess
            {
                UserID = userId,
                SupplierId = supplierId,
                AssignedBy = assignedBy,
                AssignedAt = now
            });
        }

        await _db.SaveChangesAsync();
    }

    public async Task<PaginatedResponse<UserListItemModel>> GetUsersFilteredAsync(UserListFilter filter)
    {
        var query = _db.UserAccounts.Where(u => !u.IsDelete).AsQueryable();

        if (filter.RoleId.HasValue)
            query = query.Where(u => u.RoleID == filter.RoleId.Value);

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = filter.Status.ToLowerInvariant() switch
            {
                "active"   => query.Where(u => u.IsActive),
                "inactive" => query.Where(u => !u.IsActive),
                _          => query
            };

        if (!string.IsNullOrWhiteSpace(filter.Department))
            query = query.Where(u => u.Department != null &&
                                     u.Department.Contains(filter.Department));

        if (!string.IsNullOrWhiteSpace(filter.SupplierType))
            query = query.Where(u => u.SupplierType == filter.SupplierType);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.ToLower();
            query = query.Where(u =>
                u.FirstName.ToLower().Contains(s) ||
                (u.LastName != null && u.LastName.ToLower().Contains(s)) ||
                u.Email.ToLower().Contains(s));
        }

        var total = await query.CountAsync();

        var roles = await _db.Roles.ToListAsync();
        var page  = filter.Page < 1 ? 1 : filter.Page;
        var size  = filter.PageSize < 1 ? 20 : filter.PageSize;

        var items = await query
            .OrderByDescending(u => u.CreatedDate)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(u => new
            {
                u.UserID, u.FirstName, u.LastName, u.Email,
                u.Department, u.IsActive, u.RoleID, u.CreatedDate, u.LastLoginAt, u.SupplierType
            })
            .ToListAsync();

        var userIds = items.Select(u => u.UserID).ToList();
        var supplierIdsByUser = await _db.UserSupplierAccess
            .Where(m => userIds.Contains(m.UserID))
            .GroupBy(m => m.UserID)
            .ToDictionaryAsync(g => g.Key, g => g.Select(m => m.SupplierId).ToList());

        var result = items.Select(u => new UserListItemModel
        {
            UserID     = u.UserID,
            FirstName  = u.FirstName,
            LastName   = u.LastName,
            Email      = u.Email,
            Department = u.Department,
            IsActive   = u.IsActive,
            CreatedDate = u.CreatedDate,
            LastLoginAt = u.LastLoginAt,
            SupplierType = u.SupplierType,
            SupplierIds = supplierIdsByUser.GetValueOrDefault(u.UserID, []),
            Role = new DropDownVM
            {
                ID    = u.RoleID,
                Value = roles.FirstOrDefault(r => r.RoleID == u.RoleID)?.Name ?? string.Empty
            }
        }).ToList();

        return new PaginatedResponse<UserListItemModel>
        {
            Data        = result,
            TotalRecords = total,
            Page        = page,
            PageSize    = size,
            TotalPages  = (int)Math.Ceiling((double)total / size)
        };
    }

    public async Task<UserDetailModel?> GetUserDetailAsync(int userId)
    {
        var u = await _db.UserAccounts
            .Where(x => x.UserID == userId && !x.IsDelete)
            .FirstOrDefaultAsync();
        if (u == null) return null;

        var role = await _db.Roles.FindAsync(u.RoleID);
        return new UserDetailModel
        {
            UserID     = u.UserID,
            FirstName  = u.FirstName,
            MiddleName = u.MiddleName,
            LastName   = u.LastName,
            Email      = u.Email,
            Phone      = u.Phone,
            Address    = u.Address,
            Department = u.Department,
            IsActive   = u.IsActive,
            CreatedDate = u.CreatedDate,
            LastLoginAt = u.LastLoginAt,
            Role = new DropDownVM { ID = u.RoleID, Value = role?.Name ?? string.Empty },
            SupplierType = u.SupplierType,
            SupplierIds = await GetUserSupplierIdsAsync(u.UserID)
        };
    }

    public async Task PatchUserAsync(int userId, PatchUserRequest dto, int patchedBy)
    {
        var u = await _db.UserAccounts.FindAsync(userId);
        if (u == null) return;

        if (dto.FirstName is not null) u.FirstName   = dto.FirstName;
        if (dto.LastName  is not null) u.LastName    = dto.LastName;
        if (dto.Department is not null) u.Department = dto.Department;
        if (dto.IsActive.HasValue)     u.IsActive    = dto.IsActive.Value;
        if (!string.IsNullOrWhiteSpace(dto.SupplierType))
        {
            if (dto.SupplierType != "INTERNAL" && dto.SupplierType != "EXTERNAL")
                throw new BadRequestException("Supplier Type must be Internal or External.");
            u.SupplierType = dto.SupplierType;
        }
        u.UpdateDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        if (dto.SupplierIds is not null)
            await SaveUserSupplierAccessAsync(userId, dto.SupplierIds, patchedBy);
    }

    public async Task AssignRoleAsync(int userId, int newRoleId)
    {
        var u = await _db.UserAccounts.FindAsync(userId);
        if (u == null) return;
        u.RoleID     = newRoleId;
        u.UpdateDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task RevokeAllUserSessionsAsync(int userId)
    {
        var sessions = await _db.UserSessions
            .Where(s => s.UserID == userId && s.RevokedAt == null)
            .ToListAsync();

        var now = DateTime.UtcNow;
        foreach (var s in sessions)
            s.RevokedAt = now;

        await _db.SaveChangesAsync();
    }

    public async Task SoftDeleteAsync(int userId)
    {
        var u = await _db.UserAccounts.FindAsync(userId);
        if (u == null) return;
        u.IsDelete   = true;
        u.IsActive   = false;
        u.UpdateDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ── Role CRUD ─────────────────────────────────────────────────────────────

    public async Task<List<RoleListItemModel>> GetRolesAsync()
    {
        var roles = await _db.Roles.OrderBy(r => r.RoleID).ToListAsync();
        var permCounts = await _db.RolePermissions
            .Where(rp => rp.IsAllowed)
            .GroupBy(rp => rp.RoleID)
            .Select(g => new { RoleID = g.Key, Count = g.Count() })
            .ToListAsync();
        var userCounts = await _db.UserAccounts
            .Where(u => !u.IsDelete && u.IsActive)
            .GroupBy(u => u.RoleID)
            .Select(g => new { RoleID = g.Key, Count = g.Count() })
            .ToListAsync();

        return roles.Select(r => new RoleListItemModel
        {
            RoleId           = r.RoleID,
            Name             = r.Name,
            RoleCode         = r.RoleCode,
            Description      = r.Description,
            IsActive         = r.IsActive,
            IsGlobal         = r.IsGlobal,
            ActiveUserCount  = userCounts.FirstOrDefault(u => u.RoleID == r.RoleID)?.Count ?? 0,
            PermissionCount  = permCounts.FirstOrDefault(p => p.RoleID == r.RoleID)?.Count ?? 0
        }).ToList();
    }

    public async Task<RoleDetailModel?> GetRoleDetailAsync(int roleId)
    {
        var role = await _db.Roles.FindAsync(roleId);
        if (role == null) return null;

        var activeUserCount = await _db.UserAccounts
            .CountAsync(u => u.RoleID == roleId && !u.IsDelete && u.IsActive);

        var allPerms = await _db.Permissions.OrderBy(p => p.PermissionID).ToListAsync();
        var rolePerms = await _db.RolePermissions
            .Where(rp => rp.RoleID == roleId)
            .ToListAsync();

        var items = allPerms.Select(p => new PermissionItemModel
        {
            PermissionId = p.PermissionID,
            Name         = p.Name,
            Code         = p.Code,
            IsAllowed    = rolePerms.FirstOrDefault(rp => rp.PermissionID == p.PermissionID)?.IsAllowed ?? false
        }).ToList();

        var moduleOrder = new[] { "System", "Locations", "Suppliers", "RFQ", "Contracts", "Purchase Orders", "Requisitions", "Budget", "Inventory", "Warehouse", "GRN Approvals", "Material Management", "Finance", "Logistics", "Reports", "Workflow" };
        var groups = items
            .GroupBy(i => GetPermissionModule(i.Code))
            .OrderBy(g => Array.IndexOf(moduleOrder, g.Key) is var idx && idx >= 0 ? idx : 99)
            .Select(g => new PermissionGroupModel
            {
                Module      = g.Key,
                Permissions = g.ToList()
            }).ToList();

        return new RoleDetailModel
        {
            RoleId           = role.RoleID,
            Name             = role.Name,
            RoleCode         = role.RoleCode,
            Description      = role.Description,
            IsActive         = role.IsActive,
            IsGlobal         = role.IsGlobal,
            ActiveUserCount  = activeUserCount,
            PermissionGroups = groups
        };
    }

    public async Task<RoleListItemModel> CreateRoleAsync(CreateRoleRequest req)
    {
        // RoleID has no DB identity/auto-increment (see RoleMap.ValueGeneratedNever) — every seeded
        // row is assigned an explicit id, so a new role needs one computed here too. Global across
        // every org regardless of caller (IgnoreQueryFilters) since it's the one physical table's
        // actual primary key, not a per-org sequence.
        var nextId = (await _db.Roles.IgnoreQueryFilters().MaxAsync(r => (int?)r.RoleID) ?? 0) + 1;
        var role = new Role
        {
            RoleID      = nextId,
            Name        = req.Name.Trim(),
            RoleCode    = req.RoleCode.Trim().ToUpperInvariant(),
            Description = req.Description?.Trim(),
            IsActive    = true,
            // Super Admin's new roles join the shared global catalog (usable by every org);
            // anyone else's (an Org Admin) is private to their own org — OrganizationId is left
            // unset here and auto-stamped by StampTenantScopedEntities.
            IsGlobal    = _db.TenantContext.IsSuperAdmin
        };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync();
        return new RoleListItemModel
        {
            RoleId          = role.RoleID,
            Name            = role.Name,
            RoleCode        = role.RoleCode,
            Description     = role.Description,
            IsActive        = role.IsActive,
            IsGlobal        = role.IsGlobal,
            ActiveUserCount = 0,
            PermissionCount = 0
        };
    }

    public async Task<bool> CodeExistsAsync(string code, int? excludeId = null)
    {
        var upper = code.Trim().ToUpperInvariant();
        return await _db.Roles.AnyAsync(r =>
            r.RoleCode == upper && (excludeId == null || r.RoleID != excludeId));
    }

    public async Task<bool> NameExistsAsync(string name, int? excludeId = null)
    {
        var lower = name.Trim().ToLowerInvariant();
        return await _db.Roles.AnyAsync(r =>
            r.Name.ToLower() == lower && (excludeId == null || r.RoleID != excludeId));
    }

    public async Task<bool> UpdateRoleAsync(int roleId, UpdateRoleRequest req)
    {
        var role = await _db.Roles.FindAsync(roleId);
        if (role == null) return false;
        GuardNotGlobalUnlessSuperAdmin(role);
        role.Name        = req.Name.Trim();
        role.Description = req.Description?.Trim();
        role.IsActive    = req.IsActive;
        await _db.SaveChangesAsync();
        return true;
    }

    // Permission codes that grant platform/cross-tenant reach (global reference data behind
    // SYSTEM_CONFIGURE — Countries/Cities/Currencies/Payment Terms/Lookup Types/Values — and the
    // api/system/* surface behind PLATFORM_SUPER_ADMIN) — never grantable by anyone but a Super
    // Admin, even onto a role the caller otherwise fully owns. Without this, an Org Admin could
    // create a private custom role, tick SYSTEM_CONFIGURE on it via this same endpoint (which
    // GuardNotGlobalUnlessSuperAdmin does not block — the ROLE is theirs, only the CODE is
    // dangerous), assign it to one of their org's users, and hand that user edit access to every
    // other organization's shared reference data.
    private static readonly string[] SuperAdminOnlyPermissionCodes =
        [PermissionCodes.SYSTEM_CONFIGURE, PermissionCodes.PLATFORM_SUPER_ADMIN];

    public async Task<bool> ReplaceRolePermissionsAsync(int roleId, List<int> allowedPermissionIds)
    {
        var role = await _db.Roles.FindAsync(roleId);
        if (role == null) return false;
        GuardNotGlobalUnlessSuperAdmin(role);

        if (!_db.TenantContext.IsSuperAdmin)
        {
            var attemptsRestrictedGrant = await _db.Permissions
                .AnyAsync(p => allowedPermissionIds.Contains(p.PermissionID)
                            && SuperAdminOnlyPermissionCodes.Contains(p.Code));
            if (attemptsRestrictedGrant)
                throw new ForbiddenException(
                    "Only a Super Admin can grant platform-configuration permissions (SYSTEM_CONFIGURE, PLATFORM_SUPER_ADMIN).");
        }

        var existing = await _db.RolePermissions.Where(rp => rp.RoleID == roleId).ToListAsync();
        var allPerms = await _db.Permissions.Select(p => p.PermissionID).ToListAsync();

        foreach (var permId in allPerms)
        {
            var ex = existing.FirstOrDefault(rp => rp.PermissionID == permId);
            var shouldAllow = allowedPermissionIds.Contains(permId);
            if (ex != null)
                ex.IsAllowed = shouldAllow;
            else
                _db.RolePermissions.Add(new RolePermission { RoleID = roleId, PermissionID = permId, IsAllowed = shouldAllow, OrganizationId = TenantDefaults.ScmDemoOrganizationId });
        }
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<int> GetActiveUserCountAsync(int roleId) =>
        await _db.UserAccounts.CountAsync(u => u.RoleID == roleId && !u.IsDelete && u.IsActive);

    public async Task<bool> DeactivateRoleAsync(int roleId)
    {
        var role = await _db.Roles.FindAsync(roleId);
        if (role == null) return false;
        GuardNotGlobalUnlessSuperAdmin(role);
        role.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    // The query filter already hides another org's custom roles entirely (FindAsync above returns
    // null for those), but a global/shared-catalog role is visible to everyone by design — this is
    // the remaining guard against a non-Super-Admin (an Org Admin) mutating the catalog every other
    // organization also relies on.
    private void GuardNotGlobalUnlessSuperAdmin(Role role)
    {
        if (role.IsGlobal && !_db.TenantContext.IsSuperAdmin)
            throw new ForbiddenException("This role is shared across every organization and can only be changed by a Super Admin.");
    }

    public async Task<List<RoleUserModel>> GetRoleUsersAsync(int roleId) =>
        await _db.UserAccounts
            .Where(u => u.RoleID == roleId && !u.IsDelete)
            .OrderBy(u => u.FirstName)
            .Select(u => new RoleUserModel
            {
                UserId     = u.UserID,
                FirstName  = u.FirstName,
                LastName   = u.LastName,
                Email      = u.Email,
                Department = u.Department,
                IsActive   = u.IsActive
            }).ToListAsync();

    private static string GetPermissionModule(string code) => code switch
    {
        "SYSTEM_CONFIGURE" or "USER_MANAGE" or "AUDIT_LOG_VIEW" => "System",
        "LOCATION_MANAGE"                       => "Locations",
        var c when c.StartsWith("SUPPLIER_")    => "Suppliers",
        var c when c.StartsWith("RFQ_")         => "RFQ",
        var c when c.StartsWith("CONTRACT_")    => "Contracts",
        var c when c.StartsWith("PO_")          => "Purchase Orders",
        var c when c.StartsWith("REQUISITION_") => "Requisitions",
        var c when c.StartsWith("BUDGET_")      => "Budget",
        "INVENTORY_VIEW" or "STOCK_MANAGE" or "STOCK_ADJUST" or "REORDER_MANAGE" => "Inventory",
        "WAREHOUSE_TRANSFER" or "GOODS_RECEIVE" or "PUTAWAY" or "PICKING" or "DISPATCH" or "STOCK_LOCATION_UPDATE" => "Warehouse",
        var c when c.StartsWith("GRN_")         => "GRN Approvals",
        "INVOICE_VIEW" or "INVOICE_PROCESS" or "PAYMENT_VIEW" or "PAYMENT_PROCESS" or "RECONCILIATION" => "Finance",
        "DELIVERY_TRACK"                        => "Logistics",
        var c when c.StartsWith("MATERIAL_")    => "Material Management",
        var c when c.StartsWith("REPORT_")      => "Reports",
        var c when c.StartsWith("WORKFLOW_")    => "Workflow",
        _                                       => "Other"
    };
}
