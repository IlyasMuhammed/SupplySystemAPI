using System.Security.Claims;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// Who is calling: the organization (always explicit in every query — a super admin bypasses the tenant filter), the user
/// and their permissions (D-14). Services take one of these instead of reading the HTTP context.
/// </summary>
public sealed class AccountingCaller
{
    private readonly HashSet<string> _permissions;

    public AccountingCaller(Guid organizationId, int userId, IEnumerable<string> permissions, string? userName = null)
    {
        OrganizationId = organizationId;
        UserId = userId;
        UserName = userName;
        _permissions = new HashSet<string>(permissions, StringComparer.Ordinal);
    }

    public Guid    OrganizationId { get; }
    public int     UserId         { get; }
    public string? UserName       { get; }

    /// <summary>For audit columns (CreatedBy …): null for a system caller (user id 0).</summary>
    public int? UserIdOrNull => UserId > 0 ? UserId : null;

    public bool Has(string permission) => _permissions.Contains(permission);

    /// <summary>GL_VIEW, or either manage code (they imply reading).</summary>
    public bool CanView => Has(PermissionCodes.GL_VIEW) || CanManageAccounts;

    /// <summary>D-14 — chart of accounts create/update/delete: GL_ACCOUNT_MANAGE or GL_SETUP_MANAGE.</summary>
    public bool CanManageAccounts => Has(PermissionCodes.GL_ACCOUNT_MANAGE) || CanManageSetup;

    /// <summary>D-14 — apply/upload, fiscal years, defaults, opening balances, complete/reset, mappings.</summary>
    public bool CanManageSetup => Has(PermissionCodes.GL_SETUP_MANAGE);

    // ── A47 D-15 — the journal engine ──────────────────────────────────────────────────────────────────────────────
    public bool CanCreateJournal    => Has(PermissionCodes.GL_JOURNAL_CREATE);
    public bool CanEditJournal      => Has(PermissionCodes.GL_JOURNAL_EDIT);
    public bool CanDeleteJournal    => Has(PermissionCodes.GL_JOURNAL_DELETE);
    public bool CanPostJournal      => Has(PermissionCodes.GL_JOURNAL_POST);
    public bool CanReverseJournal   => Has(PermissionCodes.GL_JOURNAL_REVERSE);

    /// <summary>Reading journal entries and journal types: GL_VIEW (or a manage code), or any journal code (a clerk sees what they enter).</summary>
    public bool CanReadJournals => CanView || CanCreateJournal || CanEditJournal || CanDeleteJournal || CanPostJournal || CanReverseJournal;

    public bool CanViewReconciliation    => Has(PermissionCodes.GL_RECONCILE_VIEW) || CanReconcile;
    public bool CanReconcile             => Has(PermissionCodes.GL_RECONCILE_EXECUTE);
    public bool CanViewReports           => Has(PermissionCodes.GL_REPORTS_VIEW);
    public bool CanViewPeriodClose       => Has(PermissionCodes.GL_PERIOD_CLOSE_VIEW) || CanExecutePeriodClose || CanAdministerPeriodClose;
    public bool CanExecutePeriodClose    => Has(PermissionCodes.GL_PERIOD_CLOSE_EXECUTE);
    public bool CanAdministerPeriodClose => Has(PermissionCodes.GL_PERIOD_CLOSE_ADMIN);

    /// <summary>SR-FE-2 — the fiscal-year and flat-account lookups the A47 pages need: any GL read, A42 or A47.</summary>
    public bool CanReadGlLookups => CanReadJournals || CanViewReconciliation || CanViewReports || CanViewPeriodClose;

    public void Require(bool allowed, string message = "You do not have permission to perform this action.")
    {
        if (!allowed) throw new ForbiddenException(message);
    }

    /// <summary>
    /// <paramref name="userName"/>: the display name resolved for this request (<see cref="ResolveNameAsync"/>, A47 QA-1);
    /// null = the token's name claim, if any.
    /// </summary>
    public static AccountingCaller From(ClaimsPrincipal user, ITenantContext tenant, string? userName = null) =>
        new(tenant.OrganizationId, user.GetUserId(), user.GetPermissions(), userName ?? ClaimName(user));

    /// <summary>The token's name claim (the SMS token issues none today — QA-1).</summary>
    public static string? ClaimName(ClaimsPrincipal user) => user.FindFirst("name")?.Value ?? user.FindFirst(ClaimTypes.Name)?.Value;

    /// <summary>
    /// A47 QA-1 — the caller's display name for the name snapshots (journals, reconcile groups, period/year close, history):
    /// the organization's user directory by user id (works with every token, old ones too), else the name claim. A missing
    /// or failing directory never fails the request.
    /// </summary>
    public static async Task<string?> ResolveNameAsync(ClaimsPrincipal user, Guid organizationId, IOrgUserDirectory? users, CancellationToken ct = default)
    {
        var id = user.GetUserId();
        if (users is not null && id > 0 && organizationId != Guid.Empty)
        {
            try
            {
                var name = (await users.GetUsersAsync(organizationId, [id], ct)).FirstOrDefault(u => u.UserId == id)?.Name;
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // the claim (or nothing) — a name is display only
            }
        }
        return ClaimName(user);
    }

    /// <summary>Every GL_* code (A42 + A47).</summary>
    public static readonly IReadOnlyList<string> AllGlCodes = PermissionCodes.All.Where(c => c.StartsWith("GL_", StringComparison.Ordinal)).ToList();

    /// <summary>A system caller (startup jobs, tests): every GL permission, no user.</summary>
    public static AccountingCaller System(Guid organizationId) => new(organizationId, 0, AllGlCodes, "System");
}
