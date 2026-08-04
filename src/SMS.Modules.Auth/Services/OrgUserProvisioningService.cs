using System.Data.Common;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Auth.Services;

// Implements the SMS.Shared.Common cross-module interface so SMS.Modules.Tenancy can provision an
// organization's initial Admin user (and later bulk-invalidate that org's sessions) without a
// project reference to Auth — AuthDbContext/AuthRepository are internal, so this is the only way
// in. Uses AuthDbContext directly (not IAuthRepository) since CreateOrgAdminUserAsync must join
// the caller's already-open DbTransaction, which needs direct Database access.
internal sealed class OrgUserProvisioningService : IOrgUserProvisioningService
{
    private readonly AuthDbContext _db;
    private readonly IPasswordHasher<UserAccount> _hasher;
    private readonly IEmailService _emailService;

    public OrgUserProvisioningService(AuthDbContext db, IPasswordHasher<UserAccount> hasher, IEmailService emailService)
    {
        _db = db;
        _hasher = hasher;
        _emailService = emailService;
    }

    public async Task<int> CreateOrgAdminUserAsync(CreateOrgAdminUserRequest req, DbTransaction sharedTransaction)
    {
        var normalized = req.Email.Trim().ToLowerInvariant();
        if (await _db.UserAccounts.AnyAsync(u => u.Email == normalized && !u.IsDelete))
            throw new BadRequestException($"An account with email '{req.Email}' already exists.");

        // Join the caller's already-open transaction (opened on TenancyDbContext) — same physical
        // connection/database, different schema, no MSDTC escalation. Mirrors the pattern already
        // established in SMS.Modules.Material/Services/MivService.cs for cross-DbContext writes.
        _db.Database.SetDbConnection(sharedTransaction.Connection!, contextOwnsConnection: false);
        await _db.Database.UseTransactionAsync(sharedTransaction);

        var token = Guid.NewGuid().ToString("N");
        var user = new UserAccount
        {
            FirstName            = req.FirstName,
            LastName             = req.LastName,
            Email                = normalized,
            RoleID               = req.RoleId,
            OrganizationId       = req.OrganizationId,
            IsActive             = false, // activated once the invite is accepted
            IsDelete             = false,
            CreatedBy            = 0, // system-initiated, via org creation
            CreatedDate          = DateTime.UtcNow,
            InviteToken          = token,
            InviteTokenExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        // Unusable placeholder — the account has no valid password until accept-invite is completed.
        user.Password = _hasher.HashPassword(user, Guid.NewGuid().ToString());

        _db.UserAccounts.Add(user);
        await _db.SaveChangesAsync();

        // Enqueued after SaveChangesAsync, outside the shared transaction — matches the existing
        // AdminCreateUserAsync precedent (fire-and-forget email job, not part of the DB atomicity).
        BackgroundJob.Enqueue(() =>
            _emailService.SendOrgAdminInviteEmail(user.Email, user.FirstName, req.OrganizationName, token));

        return user.UserID;
    }

    public async Task<int> DeleteActiveSessionsForOrganizationAsync(Guid organizationId)
    {
        var sessions = await (
            from s in _db.UserSessions
            join u in _db.UserAccounts on s.UserID equals u.UserID
            where u.OrganizationId == organizationId && s.RevokedAt == null
            select s
        ).ToListAsync();

        _db.UserSessions.RemoveRange(sessions);
        await _db.SaveChangesAsync();
        return sessions.Count;
    }

    public async Task<List<OrgUserSummary>> GetOrgUsersAsync(Guid organizationId) =>
        await (
            from u in _db.UserAccounts
            join r in _db.Roles on u.RoleID equals r.RoleID
            where u.OrganizationId == organizationId && !u.IsDelete
            orderby u.FirstName
            select new OrgUserSummary(u.UserID, u.FirstName, u.LastName, u.Email, r.RoleID, r.Name, u.IsActive)
        ).ToListAsync();

    public async Task ReassignOrgAdminAsync(Guid organizationId, int newAdminUserId)
    {
        var newAdmin = await _db.UserAccounts.SingleOrDefaultAsync(u =>
            u.UserID == newAdminUserId && u.OrganizationId == organizationId && !u.IsDelete);
        if (newAdmin is null)
            throw new BadRequestException("Selected user was not found in this organization.");

        var previousAdmins = await _db.UserAccounts
            .Where(u => u.OrganizationId == organizationId
                     && u.RoleID == (int)EnumRole.OrgAdmin
                     && u.UserID != newAdminUserId
                     && !u.IsDelete)
            .ToListAsync();
        foreach (var previous in previousAdmins)
            previous.RoleID = (int)EnumRole.Requester;

        newAdmin.RoleID = (int)EnumRole.OrgAdmin;
        await _db.SaveChangesAsync();
    }
}
