using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using Xunit;

namespace SMS.Integration.Tests.MultiTenancy;

// MT-008 — real end-to-end fixture for cross-org isolation tests. Uses the plain, non-Docker
// WebApplicationFactory<Program> pattern already established by AuthIntegrationTests (hits the
// real dev LocalDB via the app's own appsettings.json connection string), since Docker/Testcontainers
// is unavailable in this environment. Creates two throwaway organizations via the real
// POST /api/system/organizations flow and cleans up everything it created in DisposeAsync.
public sealed class MultiTenancyIsolationFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private const string TestUserPassword = "MtEight@12345";
    private const string SuperAdminEmail = "admin@sms.local";
    private const string SuperAdminPassword = "Admin@12345";

    public HttpClient SuperAdminClient { get; private set; } = null!;
    public HttpClient OrgAClient { get; private set; } = null!; // a genuine non-super-admin SCM-DEMO user
    public HttpClient OrgBClient { get; private set; } = null!;

    public Guid OrgBId { get; private set; }
    public string OrgBAdminEmail { get; } = $"mt008-orgb-{Guid.NewGuid():N}@test.local";

    public Guid OrgCId { get; private set; } // dedicated to the deactivation test — never used elsewhere
    public string OrgCAdminEmail { get; } = $"mt008-orgc-{Guid.NewGuid():N}@test.local";
    public string OrgCCode { get; } = $"MT008C{Guid.NewGuid():N}"[..20];

    public string OrgBCode { get; } = $"MT008B{Guid.NewGuid():N}"[..20];

    private readonly string _orgAUserEmail = $"mt008-orga-{Guid.NewGuid():N}@test.local";
    private int _orgAUserId;

    public async Task InitializeAsync()
    {
        SuperAdminClient = CreateClient();
        var superAdminToken = await LoginAsync(SuperAdminClient, SuperAdminEmail, SuperAdminPassword);
        SuperAdminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", superAdminToken);

        // A genuine, brand-new, non-super-admin SCM-DEMO user — NOT the Super Admin (whose token
        // would bypass every tenant query filter, silently making every isolation assertion
        // meaningless). Inserted directly rather than through an API, since there's no "invite a
        // user into an already-existing org" flow to call as a test fixture.
        _orgAUserId = await CreateAndActivateUserAsync(_orgAUserEmail, TenantDefaults.ScmDemoOrganizationId);
        OrgAClient = CreateClient();
        var orgAToken = await LoginAsync(OrgAClient, _orgAUserEmail, TestUserPassword);
        OrgAClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", orgAToken);

        OrgBId = await CreateOrganizationAsync(OrgBCode, "MT-008 Org B", OrgBAdminEmail);
        await ActivateInvitedAdminAsync(OrgBAdminEmail);
        OrgBClient = CreateClient();
        var orgBToken = await LoginAsync(OrgBClient, OrgBAdminEmail, TestUserPassword);
        OrgBClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", orgBToken);

        OrgCId = await CreateOrganizationAsync(OrgCCode, "MT-008 Org C (deactivation-only)", OrgCAdminEmail);
        await ActivateInvitedAdminAsync(OrgCAdminEmail);
    }

    public new async Task DisposeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        // The one dedicated Org A test user — never touch any other SCM-DEMO row.
        await ExecAsync(conn, $"DELETE FROM auth.UserPermissions WHERE UserID = {_orgAUserId}");
        await ExecAsync(conn, $"DELETE FROM auth.UserAccounts WHERE UserID = {_orgAUserId}");

        foreach (var orgId in new[] { OrgBId, OrgCId })
        {
            await ExecAsync(conn, $"DELETE FROM auth.UserPermissions WHERE OrganizationId = '{orgId}'");
            await ExecAsync(conn, $"DELETE FROM auth.UserAccounts WHERE OrganizationId = '{orgId}'");
            await ExecAsync(conn, $"DELETE FROM workflow_schema.workflow_steps WHERE OrganizationId = '{orgId}'");
            await ExecAsync(conn, $"DELETE FROM workflow_schema.workflow_definitions WHERE OrganizationId = '{orgId}'");
            await ExecAsync(conn, $"DELETE FROM demand.purchase_order_lines WHERE OrganizationId = '{orgId}'");
            await ExecAsync(conn, $"DELETE FROM demand.purchase_orders WHERE OrganizationId = '{orgId}'");
            await ExecAsync(conn, $"DELETE FROM inventory.Products WHERE OrganizationId = '{orgId}'");
            await ExecAsync(conn, $"DELETE FROM suppliers.Suppliers WHERE OrganizationId = '{orgId}'");
            await ExecAsync(conn, $"DELETE FROM tenant.OrganizationFeatures WHERE OrganizationId = '{orgId}'");
            await ExecAsync(conn, $"DELETE FROM tenant.Organizations WHERE Id = '{orgId}'");
        }

        Dispose();
    }

    private static async Task ExecAsync(System.Data.Common.DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal static async Task<string> LoginAsync(HttpClient client, string email, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        resp.EnsureSuccessStatusCode();
        var body = await ReadResultAsync<JsonElement>(resp);
        return body.GetProperty("accessToken").GetString()!;
    }

    private async Task<Guid> CreateOrganizationAsync(string orgCode, string orgName, string adminEmail)
    {
        var resp = await SuperAdminClient.PostAsJsonAsync("/api/system/organizations", new
        {
            orgCode,
            orgName,
            plan = "ENTERPRISE",
            adminFirstName = "Test",
            adminLastName = "Admin",
            adminEmail
        });
        resp.EnsureSuccessStatusCode();
        var body = await ReadResultAsync<JsonElement>(resp);
        return body.GetProperty("organizationId").GetGuid();
    }

    // Bypasses the invite-email flow entirely (out of scope here — MT-002 already covers it) by
    // directly activating the auto-created admin with a known password, matching the effect of
    // accepting the invite without needing to intercept/parse an email.
    private async Task ActivateInvitedAdminAsync(string email)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var hasher = new PasswordHasher<UserAccount>();

        var user = await db.UserAccounts.IgnoreQueryFilters().SingleAsync(u => u.Email == email);
        user.Password = hasher.HashPassword(user, TestUserPassword);
        user.IsActive = true;
        user.InviteToken = null;
        user.InviteTokenExpiresAt = null;
        await db.SaveChangesAsync();

        await GrantExtraPermissionsAsync(db, user.UserID, user.OrganizationId);
    }

    private async Task<int> CreateAndActivateUserAsync(string email, Guid organizationId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var hasher = new PasswordHasher<UserAccount>();

        var user = new UserAccount
        {
            FirstName      = "MT008",
            LastName       = "TestUser",
            Email          = email,
            RoleID         = (int)EnumRole.Requester,
            IsActive       = true,
            OrganizationId = organizationId,
            CreatedBy      = 1,
            CreatedDate    = DateTime.UtcNow
        };
        user.Password = hasher.HashPassword(user, TestUserPassword);
        db.UserAccounts.Add(user);
        await db.SaveChangesAsync();

        await GrantExtraPermissionsAsync(db, user.UserID, organizationId);

        return user.UserID;
    }

    // Grants permissions needed by tests that aren't about permission-checking itself: the Roles
    // endpoints require USER_MANAGE (which EnumRole.Requester doesn't carry by default — the OrgA
    // test user is deliberately a Requester, not an Org Admin, so isolation assertions aren't
    // muddied by Org-Admin-specific behavior) and WorkflowDefinitions requires WORKFLOW_ADMIN
    // (which neither EnumRole.OrgAdmin nor EnumRole.Requester carry by default). SYSTEM_CONFIGURE
    // is kept too in case any other test still relies on it.
    private static async Task GrantExtraPermissionsAsync(AuthDbContext db, int userId, Guid organizationId)
    {
        var permissionIds = await db.Permissions
            .Where(p => p.Code == "SYSTEM_CONFIGURE" || p.Code == "WORKFLOW_ADMIN" || p.Code == "USER_MANAGE")
            .Select(p => p.PermissionID)
            .ToListAsync();

        foreach (var permissionId in permissionIds)
        {
            db.UserPermissions.Add(new UserPermission
            {
                UserID         = userId,
                PermissionID   = permissionId,
                IsAllowed      = true,
                OrganizationId = organizationId
            });
        }

        await db.SaveChangesAsync();
    }

    internal static async Task<T> ReadResultAsync<T>(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        var wrapped = JsonSerializer.Deserialize<ApiResponse<T>>(json, Json)!;
        wrapped.Success.Should().BeTrue($"response not successful: {json}");
        return wrapped.Result!;
    }
}
