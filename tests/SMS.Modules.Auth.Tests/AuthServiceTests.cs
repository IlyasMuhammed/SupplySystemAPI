using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Jobs;
using SMS.Modules.Auth.Models;
using SMS.Modules.Auth.Repositories;
using SMS.Modules.Auth.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using System.IdentityModel.Tokens.Jwt;
using Xunit;

namespace SMS.Modules.Auth.Tests;

// ── Test helpers ──────────────────────────────────────────────────────────────

file static class Helpers
{
    internal const string TestSecret = "test-secret-key-must-be-at-least-32-bytes!";

    internal static (AuthService svc, AuthDbContext db) Build(
        Action<AuthDbContext>? seed = null, bool organizationActive = true, bool isSuperAdmin = false)
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        // AuthService's methods under test here (login, refresh, accept-invite, ...) all
        // correspond to anonymous endpoints — in production TenantContext bypasses the query
        // filter whenever there's no authenticated HttpContext.User (see TenantContext.cs), so
        // IsSuperAdmin=true here replicates that same "no tenant known yet" reality for these tests.
        var db = new AuthDbContext(options, new StaticTenantContext { IsSuperAdmin = true });
        seed?.Invoke(db);
        db.SaveChanges();

        var hasher = new PasswordHasher<UserAccount>();
        var repo = new AuthRepository(db, hasher);
        var settings = Options.Create(new AppSettings { Secret = TestSecret });
        var tokenSvc = new TokenService(settings);
        var emailMock = new Mock<IEmailService>();
        var orgStatusMock = new Mock<IOrganizationStatusService>();
        orgStatusMock.Setup(x => x.IsOrganizationActiveAsync(It.IsAny<Guid>())).ReturnsAsync(organizationActive);
        // MT-007: SuperAdminUsers table membership, mocked directly rather than via a real Tenancy
        // DbContext — AuthService only ever calls through the ISuperAdminService seam.
        var superAdminMock = new Mock<ISuperAdminService>();
        superAdminMock.Setup(x => x.IsSuperAdminAsync(It.IsAny<int>())).ReturnsAsync(isSuperAdmin);
        var svc = new AuthService(repo, emailMock.Object, settings, tokenSvc, hasher, orgStatusMock.Object, superAdminMock.Object);
        return (svc, db);
    }

    internal static UserAccount ActiveUser(string email = "alice@example.com", string password = "P@ssword1")
    {
        var u = new UserAccount
        {
            UserID = 1,
            Email = email,
            FirstName = "Alice",
            RoleID = (int)EnumRole.Requester,
            IsActive = true,
            IsDelete = false,
            CreatedDate = DateTime.UtcNow
        };
        u.Password = new PasswordHasher<UserAccount>().HashPassword(u, password);
        return u;
    }
}

// ── AuthService.LoginAsync ────────────────────────────────────────────────────

public class LoginAsync_Should
{
    [Fact]
    public async Task Return_access_and_refresh_tokens_on_valid_credentials()
    {
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));

        var result = await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });

        result.AccessToken.Should().NotBeNullOrWhiteSpace();
        result.RefreshToken.Should().NotBeNullOrWhiteSpace();
        result.ExpiresIn.Should().Be(900);
    }

    [Fact]
    public async Task Return_user_profile_in_response()
    {
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));

        var result = await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });

        result.User.Email.Should().Be("alice@example.com");
        result.User.FirstName.Should().Be("Alice");
        result.User.UserId.Should().Be(1);
    }

    [Fact]
    public async Task JWT_contains_sub_email_roleId_claims()
    {
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));
        var result = await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });

        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(result.AccessToken);

        token.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Sub && c.Value == "1");
        token.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Email && c.Value == "alice@example.com");
        token.Claims.Should().Contain(c => c.Type == "roleId" && c.Value == ((int)EnumRole.Requester).ToString());
    }

    [Fact]
    public async Task JWT_expires_in_15_minutes()
    {
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));
        var before = DateTime.UtcNow;
        var result = await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });

        var token = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);
        token.ValidTo.Should().BeCloseTo(before.AddMinutes(15), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Throw_UnauthorizedException_for_wrong_password()
    {
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));

        var act = () => svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "Wrong!" });
        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Throw_UnauthorizedException_for_unknown_email()
    {
        var (svc, _) = Helpers.Build();

        var act = () => svc.LoginAsync(new LoginRequestModel { Email = "nobody@example.com", Password = "P@ssword1" });
        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Throw_UnauthorizedException_for_inactive_account()
    {
        var user = Helpers.ActiveUser();
        user.IsActive = false;
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(user));

        var act = () => svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });
        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Increment_FailedLoginAttempts_on_wrong_password()
    {
        var (svc, db) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));

        try { await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "Wrong!" }); } catch { }

        db.UserAccounts.First().FailedLoginAttempts.Should().Be(1);
    }

    [Fact]
    public async Task Lock_account_after_5_consecutive_failures()
    {
        var (svc, db) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));

        for (var i = 0; i < 5; i++)
            try { await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "Wrong!" }); } catch { }

        var user = db.UserAccounts.First();
        user.LockedUntil.Should().NotBeNull();
        user.LockedUntil!.Value.Should().BeAfter(DateTime.UtcNow.AddMinutes(25));
    }

    [Fact]
    public async Task Throw_AccountLockedException_when_account_is_locked()
    {
        var user = Helpers.ActiveUser();
        user.LockedUntil = DateTime.UtcNow.AddMinutes(20);
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(user));

        var act = () => svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });
        await act.Should().ThrowAsync<AccountLockedException>();
    }

    [Fact]
    public async Task Reset_counter_when_previous_failure_is_outside_the_15_min_window()
    {
        var user = Helpers.ActiveUser();
        user.FailedLoginAttempts = 4;
        user.LastFailedAt = DateTime.UtcNow.AddMinutes(-20); // > 15 min ago
        var (svc, db) = Helpers.Build(db => db.UserAccounts.Add(user));

        try { await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "Wrong!" }); } catch { }

        db.UserAccounts.First().FailedLoginAttempts.Should().Be(1);
    }

    [Fact]
    public async Task Reset_lockout_fields_on_successful_login()
    {
        var user = Helpers.ActiveUser();
        user.FailedLoginAttempts = 3;
        user.LastFailedAt = DateTime.UtcNow.AddMinutes(-5);
        var (svc, db) = Helpers.Build(db => db.UserAccounts.Add(user));

        await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });

        var saved = db.UserAccounts.First();
        saved.FailedLoginAttempts.Should().Be(0);
        saved.LastFailedAt.Should().BeNull();
        saved.LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task Store_refresh_token_as_SHA256_hash_not_raw_value()
    {
        var (svc, db) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));

        var result = await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });

        var session = db.UserSessions.First();
        session.TokenHash.Should().NotBe(result.RefreshToken);
        session.TokenHash.Should().HaveLength(64); // SHA-256 hex = 64 chars
    }

    [Fact]
    public async Task Session_expires_in_7_days()
    {
        var (svc, db) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));
        var before = DateTime.UtcNow;

        await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });

        db.UserSessions.First().ExpiresAt.Should().BeCloseTo(before.AddDays(7), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Throw_UnauthorizedException_when_users_organization_is_deactivated()
    {
        var user = Helpers.ActiveUser();
        user.OrganizationId = Guid.NewGuid();
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(user), organizationActive: false);

        var act = () => svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });
        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Succeed_when_users_organization_is_active()
    {
        var user = Helpers.ActiveUser();
        user.OrganizationId = Guid.NewGuid();
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(user), organizationActive: true);

        var result = await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });

        result.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

}

// ── AuthService.RefreshAsync ──────────────────────────────────────────────────

public class RefreshAsync_Should
{
    [Fact]
    public async Task Return_new_access_and_refresh_tokens()
    {
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));
        var login = await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });

        var result = await svc.RefreshAsync(login.RefreshToken);

        result.AccessToken.Should().NotBeNullOrWhiteSpace();
        result.RefreshToken.Should().NotBeNullOrWhiteSpace();
        result.ExpiresIn.Should().Be(900);
    }

    [Fact]
    public async Task New_refresh_token_differs_from_old()
    {
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));
        var login = await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });

        var result = await svc.RefreshAsync(login.RefreshToken);

        result.RefreshToken.Should().NotBe(login.RefreshToken);
    }

    [Fact]
    public async Task Old_session_is_revoked_after_refresh()
    {
        var (svc, db) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));
        var login = await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });
        var oldHash = db.UserSessions.First().TokenHash;

        await svc.RefreshAsync(login.RefreshToken);

        db.UserSessions.First(s => s.TokenHash == oldHash).RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Two_sessions_exist_after_rotation()
    {
        var (svc, db) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));
        var login = await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });

        await svc.RefreshAsync(login.RefreshToken);

        db.UserSessions.Count().Should().Be(2);
    }

    [Fact]
    public async Task Throw_UnauthorizedException_for_invalid_token()
    {
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));

        var act = () => svc.RefreshAsync("not-a-real-token");
        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Throw_UnauthorizedException_when_token_is_reused_after_rotation()
    {
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));
        var login = await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });
        await svc.RefreshAsync(login.RefreshToken); // rotate once

        var act = () => svc.RefreshAsync(login.RefreshToken); // attempt reuse
        await act.Should().ThrowAsync<UnauthorizedException>();
    }
}

// ── AuthService.LogoutAsync ───────────────────────────────────────────────────

public class LogoutAsync_Should
{
    [Fact]
    public async Task Revoke_session_on_valid_refresh_token()
    {
        var (svc, db) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));
        var login = await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });

        await svc.LogoutAsync(login.RefreshToken);

        db.UserSessions.First().RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Not_throw_for_unknown_token()
    {
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));

        var act = () => svc.LogoutAsync("unknown-token");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Make_refresh_token_invalid_after_logout()
    {
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(Helpers.ActiveUser()));
        var login = await svc.LoginAsync(new LoginRequestModel { Email = "alice@example.com", Password = "P@ssword1" });
        await svc.LogoutAsync(login.RefreshToken);

        var act = () => svc.RefreshAsync(login.RefreshToken);
        await act.Should().ThrowAsync<UnauthorizedException>();
    }
}

// ── AuthService.AcceptInviteAsync ─────────────────────────────────────────────

public class AcceptInviteAsync_Should
{
    private static UserAccount InvitedUser(string token, DateTime? expiresAt = null) => new()
    {
        UserID = 1,
        Email = "orgadmin@example.com",
        FirstName = "Org",
        RoleID = (int)EnumRole.OrgAdmin,
        OrganizationId = Guid.NewGuid(),
        IsActive = false,
        IsDelete = false,
        Password = "unusable",
        InviteToken = token,
        InviteTokenExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(7),
        CreatedDate = DateTime.UtcNow
    };

    [Fact]
    public async Task Activates_user_and_sets_password_for_valid_token()
    {
        var (svc, db) = Helpers.Build(db => db.UserAccounts.Add(InvitedUser("good-token")));

        await svc.AcceptInviteAsync("good-token", "NewP@ssword1");

        var user = db.UserAccounts.First();
        user.IsActive.Should().BeTrue();
        user.InviteToken.Should().BeNull();
        user.InviteTokenExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task New_password_can_be_used_to_log_in_afterward()
    {
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(InvitedUser("good-token")));
        await svc.AcceptInviteAsync("good-token", "NewP@ssword1");

        var result = await svc.LoginAsync(new LoginRequestModel { Email = "orgadmin@example.com", Password = "NewP@ssword1" });

        result.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Throw_BadRequestException_for_unknown_token()
    {
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(InvitedUser("good-token")));

        var act = () => svc.AcceptInviteAsync("wrong-token", "NewP@ssword1");
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Throw_BadRequestException_for_expired_token()
    {
        var (svc, _) = Helpers.Build(db => db.UserAccounts.Add(InvitedUser("expired-token", DateTime.UtcNow.AddDays(-1))));

        var act = () => svc.AcceptInviteAsync("expired-token", "NewP@ssword1");
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Expired_token_does_not_activate_the_account()
    {
        var (svc, db) = Helpers.Build(db => db.UserAccounts.Add(InvitedUser("expired-token", DateTime.UtcNow.AddDays(-1))));

        try { await svc.AcceptInviteAsync("expired-token", "NewP@ssword1"); } catch { }

        db.UserAccounts.First().IsActive.Should().BeFalse();
    }
}

// ── TokenService ──────────────────────────────────────────────────────────────

public class TokenService_Should
{
    private static TokenService CreateSut() =>
        new(Options.Create(new AppSettings { Secret = Helpers.TestSecret }));

    [Fact]
    public void GenerateRefreshToken_raw_is_32_chars()
    {
        var (raw, _) = CreateSut().GenerateRefreshToken();
        raw.Should().HaveLength(32);
    }

    [Fact]
    public void GenerateRefreshToken_hash_is_64_chars()
    {
        var (_, hash) = CreateSut().GenerateRefreshToken();
        hash.Should().HaveLength(64);
    }

    [Fact]
    public void GenerateRefreshToken_hash_differs_from_raw()
    {
        var (raw, hash) = CreateSut().GenerateRefreshToken();
        hash.Should().NotBe(raw);
    }

    [Fact]
    public void GenerateRefreshToken_produces_unique_values_each_call()
    {
        var svc = CreateSut();
        var (raw1, _) = svc.GenerateRefreshToken();
        var (raw2, _) = svc.GenerateRefreshToken();
        raw1.Should().NotBe(raw2);
    }

    [Fact]
    public void ComputeSha256_is_deterministic()
    {
        TokenService.ComputeSha256("input").Should().Be(TokenService.ComputeSha256("input"));
    }

    [Fact]
    public void GenerateAccessToken_contains_sub_and_email()
    {
        var svc = CreateSut();
        var user = new UserAccount { UserID = 42, Email = "u@test.com", RoleID = 1 };
        var token = svc.GenerateAccessToken(user, "Admin", new[] { "po.read" }, isSuperAdmin: false);

        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);
        parsed.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Sub && c.Value == "42");
        parsed.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Email && c.Value == "u@test.com");
    }

    [Fact]
    public void GenerateAccessToken_includes_permission_claims()
    {
        var svc = CreateSut();
        var user = new UserAccount { UserID = 1, Email = "u@test.com", RoleID = 1 };
        var token = svc.GenerateAccessToken(user, "Manager", new[] { "po.read", "po.create" }, isSuperAdmin: false);

        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);
        parsed.Claims.Count(c => c.Type == "permission").Should().Be(2);
    }

    [Fact]
    public void GenerateAccessToken_expires_in_15_minutes()
    {
        var svc = CreateSut();
        var user = new UserAccount { UserID = 1, Email = "u@test.com", RoleID = 1 };
        var before = DateTime.UtcNow;

        var token = svc.GenerateAccessToken(user, "User", Array.Empty<string>(), isSuperAdmin: false);

        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);
        parsed.ValidTo.Should().BeCloseTo(before.AddMinutes(15), TimeSpan.FromSeconds(5));
    }

    // MT-007 — is_super_admin is now a plain passed-in bool (SuperAdminUsers table membership,
    // resolved by the caller), not derived from the permission list.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GenerateAccessToken_is_super_admin_claim_reflects_the_passed_flag(bool isSuperAdmin)
    {
        var svc = CreateSut();
        var user = new UserAccount { UserID = 1, Email = "u@test.com", RoleID = 1 };

        var token = svc.GenerateAccessToken(user, "User", Array.Empty<string>(), isSuperAdmin);

        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);
        parsed.Claims.Should().Contain(c => c.Type == "is_super_admin" && c.Value == (isSuperAdmin ? "true" : "false"));
    }
}

// ── SessionCleanupJob ─────────────────────────────────────────────────────────

public class SessionCleanupJob_Should
{
    private static AuthDbContext BuildDb()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(options, new StaticTenantContext());
    }

    private static UserAccount SeedUser(AuthDbContext db)
    {
        var u = new UserAccount
        {
            UserID = 1, Email = "a@b.com", FirstName = "A",
            Password = "x", CreatedDate = DateTime.UtcNow
        };
        db.UserAccounts.Add(u);
        return u;
    }

    [Fact]
    public async Task Delete_expired_sessions()
    {
        var db = BuildDb();
        SeedUser(db);
        db.UserSessions.Add(new UserSession
        {
            Id = Guid.NewGuid(), UserID = 1, TokenHash = "expired",
            ExpiresAt = DateTime.UtcNow.AddHours(-1), CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        await new SessionCleanupJob(db).RunAsync();

        db.UserSessions.Count().Should().Be(0);
    }

    [Fact]
    public async Task Keep_valid_sessions()
    {
        var db = BuildDb();
        SeedUser(db);
        db.UserSessions.Add(new UserSession
        {
            Id = Guid.NewGuid(), UserID = 1, TokenHash = "valid",
            ExpiresAt = DateTime.UtcNow.AddDays(7), CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        await new SessionCleanupJob(db).RunAsync();

        db.UserSessions.Count().Should().Be(1);
    }

    [Fact]
    public async Task Delete_only_expired_when_both_exist()
    {
        var db = BuildDb();
        SeedUser(db);
        db.UserSessions.AddRange(
            new UserSession { Id = Guid.NewGuid(), UserID = 1, TokenHash = "old", ExpiresAt = DateTime.UtcNow.AddHours(-1), CreatedAt = DateTime.UtcNow },
            new UserSession { Id = Guid.NewGuid(), UserID = 1, TokenHash = "new", ExpiresAt = DateTime.UtcNow.AddDays(7), CreatedAt = DateTime.UtcNow }
        );
        db.SaveChanges();

        await new SessionCleanupJob(db).RunAsync();

        db.UserSessions.Count().Should().Be(1);
        db.UserSessions.First().TokenHash.Should().Be("new");
    }

    [Fact]
    public async Task Not_throw_when_no_expired_sessions()
    {
        var db = BuildDb();
        var act = () => new SessionCleanupJob(db).RunAsync();
        await act.Should().NotThrowAsync();
    }
}
