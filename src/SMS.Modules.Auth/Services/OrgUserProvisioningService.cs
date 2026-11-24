using System.Data.Common;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Shared.Authorization;
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

    // A51 P7 — the checked send path for SendInvitationAsync; optional so the older constructions (tests) still compile.
    private readonly IEmailSender? _sender;
    private readonly AppSettings? _settings;
    private readonly IEmailStatusProvider? _status;

    public OrgUserProvisioningService(AuthDbContext db, IPasswordHasher<UserAccount> hasher, IEmailService emailService,
        IEmailSender? sender = null, IOptions<AppSettings>? settings = null, IEmailStatusProvider? status = null)
    {
        _db = db;
        _hasher = hasher;
        _emailService = emailService;
        _sender = sender;
        _settings = settings?.Value;
        _status = status;
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
        // A51 P7: the create-organization flow sends it itself afterwards (SendInvitationAsync) so the super admin learns whether it went out.
        if (req.SendInviteEmail)
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
            select new OrgUserSummary(u.UserID, u.FirstName, u.LastName, u.Email, r.RoleID, r.Name, u.IsActive,
                u.InviteToken != null, u.InviteTokenExpiresAt)
        ).ToListAsync();

    public async Task<OrgUserSummary> ReinviteUserAsync(Guid organizationId, int userId, string? newEmail, string organizationName)
    {
        var user = await _db.UserAccounts.SingleOrDefaultAsync(u =>
            u.UserID == userId && u.OrganizationId == organizationId && !u.IsDelete)
            ?? throw new BadRequestException("Selected user was not found in this organization.");

        // Set up = a password was chosen (invite accepted) or the account has been used. A new invite to another
        // address would then hand the account to that address; a super admin sets the password instead.
        if (user.InviteToken == null && (user.IsActive || user.LastLoginAt != null))
            throw new BadRequestException(
                $"{user.Email} has already set up their account, so a new invite is not sent. Use Set password instead.");

        if (!string.IsNullOrWhiteSpace(newEmail))
        {
            var normalized = newEmail.Trim().ToLowerInvariant();
            if (normalized.Length > 150 || !System.Net.Mail.MailAddress.TryCreate(normalized, out var parsed) || parsed.Address != normalized)
                throw new BadRequestException($"'{newEmail.Trim()}' is not a valid e-mail address.");
            if (normalized != user.Email
                && await _db.UserAccounts.AnyAsync(u => u.Email == normalized && !u.IsDelete && u.UserID != user.UserID))
                throw new BadRequestException($"An account with email '{normalized}' already exists.");
            user.Email = normalized;
        }

        var token = Guid.NewGuid().ToString("N");
        user.InviteToken          = token;
        user.InviteTokenExpiresAt = DateTime.UtcNow.AddDays(7);
        user.UpdateDate           = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var email = user.Email;
        var firstName = user.FirstName;
        BackgroundJob.Enqueue(() => _emailService.SendOrgAdminInviteEmail(email, firstName, organizationName, token));

        var roleName = await _db.Roles.Where(r => r.RoleID == user.RoleID).Select(r => r.Name).FirstOrDefaultAsync() ?? string.Empty;
        return new OrgUserSummary(user.UserID, user.FirstName, user.LastName, user.Email, user.RoleID, roleName, user.IsActive,
            true, user.InviteTokenExpiresAt);
    }

    /// <summary>A51 P8 — how long a request waits for the invitation mail before answering "still sending" (with the link).</summary>
    public TimeSpan InvitationWaitBudget { get; init; } = TimeSpan.FromSeconds(4);

    public async Task<OrgInviteSendResult?> SendInvitationAsync(Guid organizationId, int userId, string? newEmail, string organizationName,
        bool issueNewToken, CancellationToken ct = default)
    {
        // The create-organization flow joined this scoped context to its shared transaction (CreateOrgAdminUserAsync); once that is
        // committed the context must stop reporting it, or every later query in the scope fails.
        if (_db.Database.CurrentTransaction is not null) _db.Database.UseTransaction(null);

        var user = await _db.UserAccounts.SingleOrDefaultAsync(u => u.UserID == userId && u.OrganizationId == organizationId && !u.IsDelete, ct)
            ?? throw new BadRequestException("Selected user was not found in this organization.");

        if (user.InviteToken == null && (user.IsActive || user.LastLoginAt != null))
            throw new BadRequestException(
                $"{user.Email} has already set up their account, so a new invite is not sent. Use Set password instead.");

        if (!string.IsNullOrWhiteSpace(newEmail))
        {
            var normalized = newEmail.Trim().ToLowerInvariant();
            if (normalized.Length > 150 || !System.Net.Mail.MailAddress.TryCreate(normalized, out var parsed) || parsed.Address != normalized)
                throw new BadRequestException($"'{newEmail.Trim()}' is not a valid e-mail address.");
            if (normalized != user.Email
                && await _db.UserAccounts.AnyAsync(u => u.Email == normalized && !u.IsDelete && u.UserID != user.UserID, ct))
                throw new BadRequestException($"An account with email '{normalized}' already exists.");
            if (normalized != user.Email) issueNewToken = true; // a corrected address gets its own link
            user.Email = normalized;
        }

        var expired = user.InviteTokenExpiresAt is { } ends && ends <= DateTime.UtcNow;
        if (issueNewToken || string.IsNullOrEmpty(user.InviteToken) || expired)
        {
            user.InviteToken          = Guid.NewGuid().ToString("N");
            user.InviteTokenExpiresAt = DateTime.UtcNow.AddDays(7);
        }
        user.UpdateDate = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var token = user.InviteToken!;
        var link = $"{_settings?.BaseUrl}/auth/accept-invite?token={token}";
        var configured = _status?.IsConfigured ?? false;
        var sent = false;
        string? error = null;
        if (_sender is null) error = "E-mail sending is not available in this host.";
        else if (!configured) error = "E-mail is not configured (see the e-mail status); the invitation was not sent.";
        else
        {
            var body = $"<p>Hi {System.Net.WebUtility.HtmlEncode(user.FirstName)},</p>" +
                       $"<p>You've been set up as the administrator for <strong>{System.Net.WebUtility.HtmlEncode(organizationName)}</strong> on the Supply Management System.</p>" +
                       "<p>Click the link below to set your password and activate your account:</p>" +
                       $"<p><a href='{link}'>{link}</a></p>" +
                       "<p>This link expires in 7 days.</p>";
            try
            {
                // The checked path: the result says whether the mail went out (never assumed).
                // A51 P8: the send runs on its own thread (SmtpClient.Send blocks) and the request waits for it only up to
                // InvitationWaitBudget; a slow / dead mail host then reports "still sending" WITH the link, and the send
                // carries on in the background (never tied to the request token).
                var to = user.Email;
                var subject = SingleLine($"You've been invited to administer {organizationName} on SMS");
                var sendTask = Task.Run(() => _sender.SendWithResultAsync(to, subject, body, null, CancellationToken.None));
                var finished = await Task.WhenAny(sendTask, Task.Delay(InvitationWaitBudget, ct));
                if (finished == sendTask)
                {
                    var result = await sendTask;
                    sent = result?.Success == true;
                    error = sent ? null : result?.Error ?? "The e-mail could not be sent.";
                }
                else
                {
                    ct.ThrowIfCancellationRequested();
                    _ = sendTask.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default); // observed; the sender logs its own outcome
                    error = $"The e-mail is still being sent (no answer after {InvitationWaitBudget.TotalSeconds:0} s). If it does not arrive, pass on the link.";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message; // the exception text never contains the link
            }
        }

        var roleName = await _db.Roles.Where(r => r.RoleID == user.RoleID).Select(r => r.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        var summary = new OrgUserSummary(user.UserID, user.FirstName, user.LastName, user.Email, user.RoleID, roleName, user.IsActive, true, user.InviteTokenExpiresAt);
        // The link is handed back ONLY when the mail did not go out (the Super Admin passes it on); it is never logged.
        return new OrgInviteSendResult(user.UserID, user.Email, sent, error, configured, sent ? null : link, user.InviteTokenExpiresAt, summary);
    }

    private static string SingleLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

    // A50 D-22 — the setup wizard's invites. Same account shape and e-mail as CreateOrgAdminUserAsync (inactive, 7-day
    // token, accept-invite sets the password), but with the wizard's role and no shared transaction. Reports instead of
    // throwing, so one bad row never stops the others.
    public async Task<OrgUserInviteResult> InviteUserAsync(OrgUserInviteRequest request, CancellationToken ct = default)
    {
        var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
        if (email.Length == 0 || email.Length > 150
            || !System.Net.Mail.MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
            return new OrgUserInviteResult(request.Email ?? string.Empty, OrgUserInviteOutcomes.Invalid, null,
                $"'{request.Email?.Trim()}' is not a valid e-mail address.");

        // Any organization's account: an e-mail signs in to exactly one account (the login looks it up globally).
        var existing = await _db.UserAccounts.IgnoreQueryFilters()
            .Where(u => u.Email == email && !u.IsDelete).Select(u => new { u.UserID, u.OrganizationId }).FirstOrDefaultAsync(ct);
        if (existing is not null)
            return new OrgUserInviteResult(email, OrgUserInviteOutcomes.Exists,
                existing.OrganizationId == request.OrganizationId ? existing.UserID : null,
                existing.OrganizationId == request.OrganizationId
                    ? "This person already has an account in this organization; it was left unchanged."
                    : "An account with this e-mail already exists; it was left unchanged.");

        // The role must be one this organization may hand out: a global role (never the super admin's platform reach)
        // or one of its own custom roles.
        var role = await _db.Roles.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(r => r.RoleID == request.RoleId, ct);
        if (role is null || !role.IsActive || !(role.IsGlobal || role.OrganizationId == request.OrganizationId))
            return new OrgUserInviteResult(email, OrgUserInviteOutcomes.Invalid, null, $"Role {request.RoleId} cannot be assigned in this organization.");
        var grantsPlatform = await (
            from rp in _db.RolePermissions.IgnoreQueryFilters()
            join p in _db.Permissions on rp.PermissionID equals p.PermissionID
            where rp.RoleID == role.RoleID && rp.IsAllowed && p.Code == PermissionCodes.PLATFORM_SUPER_ADMIN
            select rp.RolePermissionID).AnyAsync(ct);
        if (grantsPlatform)
            return new OrgUserInviteResult(email, OrgUserInviteOutcomes.Invalid, null, $"Role '{role.Name}' is a platform role and cannot be assigned by an invite.");

        var firstName = string.IsNullOrWhiteSpace(request.FirstName) ? email.Split('@')[0] : request.FirstName.Trim();
        var token = Guid.NewGuid().ToString("N");
        var user = new UserAccount
        {
            FirstName            = firstName.Length > 100 ? firstName[..100] : firstName,
            LastName             = string.IsNullOrWhiteSpace(request.LastName) ? null : request.LastName.Trim(),
            Email                = email,
            RoleID               = role.RoleID,
            OrganizationId       = request.OrganizationId,
            IsActive             = false, // activated once the invite is accepted
            IsDelete             = false,
            CreatedBy            = request.InvitedBy,
            CreatedDate          = DateTime.UtcNow,
            InviteToken          = token,
            InviteTokenExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        user.Password = _hasher.HashPassword(user, Guid.NewGuid().ToString());
        _db.UserAccounts.Add(user);
        await _db.SaveChangesAsync(ct);

        var to = user.Email;
        var name = user.FirstName;
        var orgName = request.OrganizationName;
        BackgroundJob.Enqueue(() => _emailService.SendOrgAdminInviteEmail(to, name, orgName, token));

        return new OrgUserInviteResult(email, OrgUserInviteOutcomes.Invited, user.UserID, null);
    }

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
