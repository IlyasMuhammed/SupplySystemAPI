using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Data;
using SMS.Shared.Authorization;
using SMS.Shared.Common;

namespace SMS.Modules.Auth.Services;

// REQ-2.x — see IUserSupplierAccessService for the fail-open design rationale. Resolves the
// caller's identity from the same JWT claims TenantContext already reads, then hits AuthDbContext
// directly (this service lives in the Auth module, so it can — SuppliersDbContext/other modules'
// DbContexts are never touched here, only via IUserSupplierAccessService's public contract).
internal sealed class UserSupplierAccessService : IUserSupplierAccessService
{
    private readonly AuthDbContext _db;
    private readonly IHttpContextAccessor _accessor;

    public UserSupplierAccessService(AuthDbContext db, IHttpContextAccessor accessor)
    {
        _db = db;
        _accessor = accessor;
    }

    public async Task<bool> IsRestrictedAsync()
    {
        var user = _accessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return false;
        if (user.FindFirst("is_super_admin")?.Value == "true") return false;

        var userId = user.GetUserId();
        if (userId == 0) return false;

        var account = await _db.UserAccounts.FindAsync(userId);
        if (account is null || account.SupplierType != "EXTERNAL") return false;

        return await _db.UserSupplierAccess.AnyAsync(m => m.UserID == userId);
    }

    public async Task<IReadOnlySet<Guid>> GetAllowedSupplierIdsAsync()
    {
        var userId = _accessor.HttpContext?.User?.GetUserId() ?? 0;
        if (userId == 0) return new HashSet<Guid>();

        var ids = await _db.UserSupplierAccess
            .Where(m => m.UserID == userId)
            .Select(m => m.SupplierId)
            .ToListAsync();

        return ids.ToHashSet();
    }

    public async Task<bool> CanAccessSupplierAsync(Guid supplierUuid)
    {
        if (!await IsRestrictedAsync()) return true;
        var allowed = await GetAllowedSupplierIdsAsync();
        return allowed.Contains(supplierUuid);
    }
}
