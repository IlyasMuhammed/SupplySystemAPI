using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Repositories;
using SMS.Modules.Auth.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Auth.Tests;

/// <summary>
/// A38 D-4 — <see cref="IPosPinService"/>: PINs hashed with the password hasher (Identity v3 format, API-CONTRACT §7),
/// explicit organization (the ambient tenant here is ANOTHER org and not a super admin), PIN failures independent of
/// the password lockout, and PIN sign-in tokens issued like a password login.
/// </summary>
public class PosPinServiceTests
{
    private const string Secret = "test-secret-key-must-be-at-least-32-bytes!";
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private sealed record Bed(PosPinService Pins, AuthDbContext Db, string DbName, Mock<IOrganizationStatusService> OrgStatus,
        Mock<ISuperAdminService> SuperAdmins);

    private static Bed New()
    {
        var dbName = Guid.NewGuid().ToString();
        var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options,
            new StaticTenantContext { OrganizationId = OrgB, IsSuperAdmin = false });

        db.Roles.AddRange(
            new Role { RoleID = 1, Name = "POS Cashier", RoleCode = "POS_CASHIER", IsGlobal = true },
            new Role { RoleID = 2, Name = "Store Manager", RoleCode = "STORE_MANAGER", IsGlobal = true });
        db.Permissions.AddRange(
            new Permission { PermissionID = 1, Code = "POS_ORDER_CREATE", Name = "Sell" },
            new Permission { PermissionID = 2, Code = "POS_ORDER_VOID", Name = "Void" },
            new Permission { PermissionID = 3, Code = "POS_REPORTS_VIEW", Name = "Reports" });
        db.RolePermissions.AddRange(
            new RolePermission { RoleID = 1, PermissionID = 1, IsAllowed = true, OrganizationId = OrgA },
            new RolePermission { RoleID = 2, PermissionID = 1, IsAllowed = true, OrganizationId = OrgA },
            new RolePermission { RoleID = 2, PermissionID = 3, IsAllowed = true, OrganizationId = OrgA });

        db.UserAccounts.AddRange(
            User(10, "Ali", "Khan", "ali@a.test", roleId: 1),
            User(11, "Sara", null, "sara@a.test", roleId: 2),
            User(12, "Idle", "User", "idle@a.test", roleId: 1, active: false),
            User(13, "Gone", "User", "gone@a.test", roleId: 1, deleted: true),
            User(20, "Bob", "Other", "bob@b.test", roleId: 1, org: OrgB));
        // Ali: a user-level grant of VOID; Sara: a user-level DENY of the reports her role gives.
        db.UserPermissions.AddRange(
            new UserPermission { UserID = 10, PermissionID = 2, IsAllowed = true, OrganizationId = OrgA },
            new UserPermission { UserID = 11, PermissionID = 3, IsAllowed = false, OrganizationId = OrgA });
        db.SaveChanges();

        var hasher = new PasswordHasher<UserAccount>();
        var orgStatus = new Mock<IOrganizationStatusService>();
        orgStatus.Setup(o => o.IsOrganizationActiveAsync(It.IsAny<Guid>())).ReturnsAsync(true);
        var superAdmins = new Mock<ISuperAdminService>();
        superAdmins.Setup(s => s.IsSuperAdminAsync(It.IsAny<int>())).ReturnsAsync(false);
        var tokens = new TokenService(Options.Create(new AppSettings { Secret = Secret }));
        return new Bed(new PosPinService(db, hasher, tokens, superAdmins.Object, orgStatus.Object), db, dbName, orgStatus, superAdmins);
    }

    private static UserAccount User(int id, string first, string? last, string email, int roleId,
        bool active = true, bool deleted = false, Guid? org = null) =>
        new()
        {
            UserID = id, FirstName = first, LastName = last, Email = email, Password = "x", RoleID = roleId,
            IsActive = active, IsDelete = deleted, OrganizationId = org ?? OrgA, CreatedDate = DateTime.UtcNow
        };

    private static UserAccount Row(Bed b, int id) => b.Db.UserAccounts.IgnoreQueryFilters().AsNoTracking().Single(u => u.UserID == id);

    // ── Set / verify / format ────────────────────────────────────────────────

    [Fact]
    public async Task A_pin_is_stored_as_an_identity_v3_pbkdf2_hash_and_verifies()
    {
        var b = New();
        await b.Pins.SetPinAsync(OrgA, 10, "4821");

        var hash = Row(b, 10).PosPinHash!;
        hash.Should().NotContain("4821");
        var bytes = Convert.FromBase64String(hash);
        bytes[0].Should().Be(0x01, "Identity v3 marker");
        var prf = (uint)(bytes[1] << 24 | bytes[2] << 16 | bytes[3] << 8 | bytes[4]);
        var iterations = (uint)(bytes[5] << 24 | bytes[6] << 16 | bytes[7] << 8 | bytes[8]);
        var saltLength = (uint)(bytes[9] << 24 | bytes[10] << 16 | bytes[11] << 8 | bytes[12]);
        prf.Should().BeOneOf(1u, 2u);   // HMACSHA256 | HMACSHA512 — what the PWA's WebCrypto PBKDF2 accepts
        iterations.Should().BeGreaterThanOrEqualTo(10_000u);
        saltLength.Should().Be(16u);
        bytes.Length.Should().Be(13 + (int)saltLength + 32);

        (await b.Pins.VerifyAsync(OrgA, 10, "4821")).Should().BeTrue();
        (await b.Pins.VerifyAsync(OrgA, 10, "4822")).Should().BeFalse();
        (await b.Pins.VerifyAsync(OrgA, 10, "48211")).Should().BeFalse();
    }

    [Theory]
    [InlineData("123")]
    [InlineData("1234567")]
    [InlineData("12a4")]
    [InlineData(" 1234")]
    [InlineData("１２３４")]   // full-width digits are not ASCII digits
    public async Task A_pin_must_be_4_to_6_ascii_digits(string pin)
    {
        var b = New();
        await b.Pins.Invoking(p => p.SetPinAsync(OrgA, 10, pin)).Should()
            .ThrowAsync<BadRequestException>().WithMessage("PIN must be 4 to 6 digits.");
        Row(b, 10).PosPinHash.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Null_or_empty_clears_the_pin(string? clear)
    {
        var b = New();
        await b.Pins.SetPinAsync(OrgA, 10, "123456");
        await b.Pins.SetPinAsync(OrgA, 10, clear);

        Row(b, 10).PosPinHash.Should().BeNull();
        (await b.Pins.VerifyAsync(OrgA, 10, "123456")).Should().BeFalse();
    }

    [Fact]
    public async Task Another_organizations_user_is_absent()
    {
        var b = New();
        await b.Pins.SetPinAsync(OrgA, 10, "4821");

        await b.Pins.Invoking(p => p.SetPinAsync(OrgB, 10, "1111")).Should().ThrowAsync<NotFoundException>();
        await b.Pins.Invoking(p => p.SetPinAsync(OrgA, 20, "1111")).Should().ThrowAsync<NotFoundException>();
        await b.Pins.Invoking(p => p.SetPinAsync(OrgA, 13, "1111")).Should().ThrowAsync<NotFoundException>("deleted");
        (await b.Pins.VerifyAsync(OrgB, 10, "4821")).Should().BeFalse();
        (await b.Pins.FindByPinAsync(OrgB, [10], "4821")).Should().BeNull();
        (await b.Pins.GetPinHashesAsync(OrgB, [10])).Should().BeEmpty();
        (await b.Pins.GetUsersAsync(OrgB, [10, 11])).Should().BeEmpty();
    }

    // ── Find / hashes ────────────────────────────────────────────────────────

    [Fact]
    public async Task Find_by_pin_picks_the_one_candidate_holding_it()
    {
        var b = New();
        await b.Pins.SetPinAsync(OrgA, 10, "1111");
        await b.Pins.SetPinAsync(OrgA, 11, "2222");

        (await b.Pins.FindByPinAsync(OrgA, [10, 11], "2222")).Should().Be(11);
        (await b.Pins.FindByPinAsync(OrgA, [10, 11], "1111")).Should().Be(10);
        (await b.Pins.FindByPinAsync(OrgA, [10], "2222")).Should().BeNull("11 is not a candidate");
        (await b.Pins.FindByPinAsync(OrgA, [10, 11], "3333")).Should().BeNull();
        (await b.Pins.FindByPinAsync(OrgA, [], "1111")).Should().BeNull();
        (await b.Pins.FindByPinAsync(OrgA, [10, 11], "abc")).Should().BeNull();
    }

    [Fact]
    public async Task Find_by_pin_never_returns_an_inactive_user_or_an_ambiguous_match()
    {
        var b = New();
        await b.Pins.SetPinAsync(OrgA, 12, "5555");   // inactive user may hold a PIN, but never signs in with it
        (await b.Pins.FindByPinAsync(OrgA, [12], "5555")).Should().BeNull();
        (await b.Pins.VerifyAsync(OrgA, 12, "5555")).Should().BeFalse();

        await b.Pins.SetPinAsync(OrgA, 10, "7777");
        await b.Pins.SetPinAsync(OrgA, 11, "7777");
        (await b.Pins.FindByPinAsync(OrgA, [10, 11], "7777")).Should().BeNull("two staff share it — signing in either could be the wrong person");
    }

    [Fact]
    public async Task Pin_hashes_are_returned_for_active_users_with_a_pin_only()
    {
        var b = New();
        await b.Pins.SetPinAsync(OrgA, 10, "1111");
        await b.Pins.SetPinAsync(OrgA, 12, "2222");

        var hashes = await b.Pins.GetPinHashesAsync(OrgA, [10, 11, 12, 13]);

        hashes.Should().ContainSingle().Which.Should().Be(new PosPinHash(10, Row(b, 10).PosPinHash!));
    }

    // ── Users ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Users_carry_name_role_pin_flag_and_the_tokens_effective_permissions()
    {
        var b = New();
        await b.Pins.SetPinAsync(OrgA, 10, "1111");

        var users = await b.Pins.GetUsersAsync(OrgA, [10, 11, 12, 13, 20]);

        users.Select(u => u.UserId).Should().Equal(10, 11, 12);
        var ali = users[0];
        (ali.Name, ali.Email, ali.RoleName, ali.IsActive, ali.HasPin).Should().Be(("Ali Khan", "ali@a.test", "POS Cashier", true, true));
        ali.Permissions.Should().BeEquivalentTo(["POS_ORDER_CREATE", "POS_ORDER_VOID"]);
        users[1].Name.Should().Be("Sara");
        users[1].HasPin.Should().BeFalse();
        users[1].Permissions.Should().BeEquivalentTo(["POS_ORDER_CREATE"], "a user-level deny beats the role's grant");
        users[2].IsActive.Should().BeFalse();

        // Exactly what the access token carries: the login path's own computation, run as a caller of org A.
        var repoAsOrgA = new AuthRepository(
            new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(b.DbName).Options,
                new StaticTenantContext { OrganizationId = OrgA }),
            new PasswordHasher<UserAccount>());
        foreach (var u in users)
            u.Permissions.Should().BeEquivalentTo(await repoAsOrgA.GetAllowedPermissionsAsync(u.UserId), $"user {u.UserId}");
    }

    [Fact]
    public async Task Search_finds_active_users_of_the_org_by_name_or_email_within_the_limit()
    {
        var b = New();

        (await b.Pins.SearchUsersAsync(OrgA, null, 50)).Select(u => u.UserId).Should().Equal(10, 11);
        (await b.Pins.SearchUsersAsync(OrgA, "sara", 50)).Select(u => u.UserId).Should().Equal(11);
        (await b.Pins.SearchUsersAsync(OrgA, "khan", 50)).Select(u => u.UserId).Should().Equal(10);
        (await b.Pins.SearchUsersAsync(OrgA, "@a.test", 1)).Should().HaveCount(1);
        (await b.Pins.SearchUsersAsync(OrgA, "idle", 50)).Should().BeEmpty("inactive");
        (await b.Pins.SearchUsersAsync(OrgA, "bob", 50)).Should().BeEmpty("another organization");
    }

    // ── Lockout independence ─────────────────────────────────────────────────

    [Fact]
    public async Task Wrong_pins_never_touch_the_password_lockout()
    {
        var b = New();
        await b.Pins.SetPinAsync(OrgA, 10, "1111");

        for (var i = 0; i < 10; i++)
        {
            (await b.Pins.VerifyAsync(OrgA, 10, "9999")).Should().BeFalse();
            (await b.Pins.FindByPinAsync(OrgA, [10], "9999")).Should().BeNull();
        }

        var row = Row(b, 10);
        (row.FailedLoginAttempts, row.LastFailedAt, row.LockedUntil).Should().Be((0, (DateTime?)null, (DateTime?)null));
    }

    [Fact]
    public async Task A_password_lockout_does_not_lock_the_pin_and_a_pin_sign_in_does_not_clear_it()
    {
        var b = New();
        await b.Pins.SetPinAsync(OrgA, 10, "1111");
        var lockedUntil = DateTime.UtcNow.AddMinutes(20);
        var tracked = b.Db.UserAccounts.IgnoreQueryFilters().Single(u => u.UserID == 10);
        tracked.FailedLoginAttempts = 3;
        tracked.LockedUntil = lockedUntil;
        await b.Db.SaveChangesAsync();

        (await b.Pins.VerifyAsync(OrgA, 10, "1111")).Should().BeTrue();
        await b.Pins.IssueTokensAsync(OrgA, 10);

        var row = Row(b, 10);
        row.LockedUntil.Should().Be(lockedUntil);
        row.FailedLoginAttempts.Should().Be(3);
    }

    // ── Tokens ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Issue_tokens_gives_a_normal_jwt_and_records_the_refresh_session()
    {
        var b = New();

        var pair = await b.Pins.IssueTokensAsync(OrgA, 10);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(pair.AccessToken);
        jwt.Subject.Should().Be("10");
        jwt.Claims.Single(c => c.Type == "organizationId").Value.Should().Be(OrgA.ToString());
        jwt.Claims.Single(c => c.Type == "roleName").Value.Should().Be("POS Cashier");
        jwt.Claims.Single(c => c.Type == "is_super_admin").Value.Should().Be("false");
        jwt.Claims.Where(c => c.Type == "permission").Select(c => c.Value).Should().BeEquivalentTo(["POS_ORDER_CREATE", "POS_ORDER_VOID"]);

        var session = b.Db.UserSessions.IgnoreQueryFilters().AsNoTracking().Single();
        session.UserID.Should().Be(10);
        session.OrganizationId.Should().Be(OrgA, "stamped from the user, not the ambient tenant");
        session.TokenHash.Should().Be(TokenService.ComputeSha256(pair.RefreshToken));
        session.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromMinutes(1));
        Row(b, 10).LastLoginAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData(12, "This user account is not active.")]
    [InlineData(13, "This user cannot sign in.")]   // deleted
    [InlineData(20, "This user cannot sign in.")]   // another organization
    [InlineData(999, "This user cannot sign in.")]
    public async Task Issue_tokens_refuses_inactive_deleted_and_foreign_users(int userId, string message)
    {
        var b = New();
        await b.Pins.Invoking(p => p.IssueTokensAsync(OrgA, userId)).Should().ThrowAsync<UnauthorizedException>().WithMessage(message);
        b.Db.UserSessions.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task Issue_tokens_refuses_an_inactive_organization_and_a_super_admin()
    {
        var b = New();
        b.OrgStatus.Setup(o => o.IsOrganizationActiveAsync(OrgA)).ReturnsAsync(false);
        await b.Pins.Invoking(p => p.IssueTokensAsync(OrgA, 10)).Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("This organization has been deactivated.");

        b.OrgStatus.Setup(o => o.IsOrganizationActiveAsync(OrgA)).ReturnsAsync(true);
        b.SuperAdmins.Setup(s => s.IsSuperAdminAsync(10)).ReturnsAsync(true);
        await b.Pins.Invoking(p => p.IssueTokensAsync(OrgA, 10)).Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Platform super admins cannot sign in with a PIN.");
        b.Db.UserSessions.IgnoreQueryFilters().Should().BeEmpty();
    }
}
