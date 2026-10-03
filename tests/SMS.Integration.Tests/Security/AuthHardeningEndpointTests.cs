using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Integration.Tests.Security;

/// <summary>
/// The 2026-10-02 auth hardening, through the real host on LocalDB: the permission gates on the legacy
/// api/auth administration, the api/users role-assignment guard, profile changes bound to the token's user,
/// the uniform forgot-password answer, and the reset code's attempt limit. Also proves AddAuthModule registers
/// the rate-limit policies the anonymous actions name — an unregistered one is a 500 on every login.
/// </summary>
public sealed class AuthHardeningEndpointTests : IClassFixture<SapWebApplicationFactory>
{
    private const string Password = "Hardening@12345!";
    private readonly SapWebApplicationFactory _f;

    public AuthHardeningEndpointTests(SapWebApplicationFactory factory) => _f = factory;

    private async Task<(int Id, string Email, HttpClient Client)> NewUserAsync(EnumRole role, string tag)
    {
        var email = $"{tag}-{Guid.NewGuid():N}@hardening.test";
        using (var admin = _f.CreateAdminClient())
        {
            var created = await admin.PostAsJsonAsync("/api/users", new { FirstName = "Hard", LastName = "Ening", Email = email, RoleID = (int)role, SupplierType = "INTERNAL" });
            created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        }
        await _f.SetPasswordAsync(email, Password);
        return (await UserIdAsync(email), email, _f.CreateBearerClient(await _f.LoginAsync(email, Password)));
    }

    private async Task<int> UserIdAsync(string email) =>
        Convert.ToInt32((await _f.QueryAsync("SELECT UserID FROM auth.UserAccounts WHERE Email = @e", ("@e", email))).Single()["UserID"]);

    private async Task<object?> ColumnAsync(int userId, string column) =>
        (await _f.QueryAsync($"SELECT {column} FROM auth.UserAccounts WHERE UserID = @id", ("@id", userId))).Single()[column];

    [Fact]
    public async Task A_user_without_user_management_is_refused_the_legacy_administration()
    {
        var (id, _, requester) = await NewUserAsync(EnumRole.Requester, "req");
        using var _ = requester;

        (await requester.PutAsJsonAsync($"api/auth/users/{id}/permissions", new[] { new { permissionID = 1, isAllowed = true } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await requester.GetAsync("api/auth/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await requester.PatchAsync($"api/auth/users/{id}/deactivate", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await requester.PutAsJsonAsync("api/auth/roles/7/permissions", new[] { new { permissionID = 1, isAllowed = true } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_org_admin_cannot_make_anyone_a_system_admin_or_change_their_own_role_or_a_shared_roles_permissions()
    {
        var (adminId, _, orgAdmin) = await NewUserAsync(EnumRole.OrgAdmin, "orgadm");
        var (requesterId, _, requester) = await NewUserAsync(EnumRole.Requester, "victim");
        using var _1 = orgAdmin; using var _2 = requester;

        (await orgAdmin.PutAsJsonAsync($"api/users/{requesterId}/role", new { roleID = (int)EnumRole.SystemAdmin }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await orgAdmin.PutAsJsonAsync($"api/users/{adminId}/role", new { roleID = (int)EnumRole.FinanceManager }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await orgAdmin.PutAsJsonAsync("api/auth/roles/7/permissions", new[] { new { permissionID = 1, isAllowed = true } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "Org Admin does not hold PLATFORM_SUPER_ADMIN");

        // Ordinary roles are still theirs to hand out.
        (await orgAdmin.PutAsJsonAsync($"api/users/{requesterId}/role", new { roleID = (int)EnumRole.SupplyDeptAdmin }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        Convert.ToInt32(await ColumnAsync(adminId, "RoleID")).Should().Be((int)EnumRole.OrgAdmin);
    }

    [Fact]
    public async Task A_profile_change_always_lands_on_the_token_user_whatever_id_the_body_names()
    {
        var (id, _, requester) = await NewUserAsync(EnumRole.Requester, "mallory");
        using var _ = requester;
        var superAdminId = await UserIdAsync("admin@sms.local");
        var newEmail = $"mallory-{Guid.NewGuid():N}@hardening.test";

        var resp = await requester.PutAsJsonAsync("api/auth/profile", new { userID = superAdminId, firstName = "Mallory", email = newEmail });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await ColumnAsync(superAdminId, "Email")).Should().Be("admin@sms.local");
        (await ColumnAsync(id, "Email")).Should().Be(newEmail);
    }

    [Fact]
    public async Task Forgot_password_answers_the_same_for_an_unknown_address_and_the_code_is_spent_after_five_wrong_guesses()
    {
        var (id, email, client) = await NewUserAsync(EnumRole.Requester, "forgetful");
        client.Dispose();
        using var anon = _f.CreateAnonymousClient();

        var real = await anon.PostAsync($"api/auth/forgot-password?email={Uri.EscapeDataString(email)}", null);
        var unknown = await anon.PostAsync($"api/auth/forgot-password?email=nobody-{Guid.NewGuid():N}@hardening.test", null);

        real.StatusCode.Should().Be(HttpStatusCode.OK, await real.Content.ReadAsStringAsync());
        unknown.StatusCode.Should().Be(HttpStatusCode.OK);
        (await unknown.Content.ReadAsStringAsync()).Should().Be(await real.Content.ReadAsStringAsync());

        var code = (string)(await ColumnAsync(id, "PasswordResetToken"))!;
        (await ColumnAsync(id, "FirstName")).Should().Be("Hard", "issuing the code must not touch any other column");
        var wrong = ((int.Parse(code) + 1) % 1_000_000).ToString("D6");

        for (var i = 0; i < 5; i++)
        {
            var guess = await anon.PostAsJsonAsync("api/auth/reset-password", new { email, password = "Taken#Over2026", verificationCode = wrong });
            guess.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        (await ColumnAsync(id, "PasswordResetToken")).Should().BeNull("the fifth wrong guess spends the code");

        var right = await anon.PostAsJsonAsync("api/auth/reset-password", new { email, password = "Taken#Over2026", verificationCode = code });
        right.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _f.LoginAsync(email, Password)).Should().NotBeNullOrEmpty("the password is unchanged");
    }
}
