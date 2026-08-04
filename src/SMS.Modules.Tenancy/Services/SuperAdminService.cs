using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Services;

// Implements the SMS.Shared.Common cross-module interface so SMS.Modules.Auth can check
// SuperAdminUsers membership without a project reference to Tenancy (TenancyDbContext is
// internal) — same reasoning as OrganizationStatusService (MT-007).
internal sealed class SuperAdminService : ISuperAdminService
{
    private readonly TenancyDbContext _db;

    public SuperAdminService(TenancyDbContext db) => _db = db;

    public Task<bool> IsSuperAdminAsync(int userId) =>
        _db.SuperAdminUsers.AnyAsync(s => s.UserId == userId);
}
