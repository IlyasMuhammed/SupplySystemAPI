using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Models;
using SMS.Modules.Auth.Repositories;
using SMS.Modules.Auth.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Auth.Tests;

// AdminCreateUserAsync fires a fire-and-forget Hangfire job (temp-password email) — this is the
// first test in this project to exercise that real code path, so JobStorage needs an in-memory
// backing store or Hangfire.BackgroundJob.Enqueue throws before the assertions even run.
file static class HangfireSetup
{
    private static bool _configured;
    internal static void EnsureConfigured()
    {
        if (_configured) return;
        _configured = true;
        GlobalConfiguration.Configuration.UseInMemoryStorage();
    }
}

// Regression coverage for a real bug found while wiring up Org Admin user management: unlike most
// AuthDbContext queries (correctly org-scoped by the MT-003 tenant filter), email uniqueness must
// stay GLOBAL — login resolves a user by email alone with no org selector
// (AuthRepository.FindUserForLoginAsync), so two different orgs creating the same email would
// leave one of the two accounts unable to log in (silently shadowed by whichever row
// FirstOrDefaultAsync happens to return). These tests use a real, non-bypassed, org-scoped
// StaticTenantContext for each org — unlike AuthServiceTests' shared Helpers.Build(), which always
// constructs an IsSuperAdmin=true (bypassed) context to match the anonymous-endpoint flows it
// tests, and would make this specific regression untestable.
public class AdminCreateUserCrossOrgTests
{
    private const string TestSecret = "test-secret-key-must-be-at-least-32-bytes!";

    private static (AuthService Svc, AuthDbContext Db) Build(string dbName, Guid organizationId)
    {
        HangfireSetup.EnsureConfigured();

        var options = new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options;
        var db = new AuthDbContext(options, new StaticTenantContext { OrganizationId = organizationId });

        var hasher = new PasswordHasher<UserAccount>();
        var repo = new AuthRepository(db, hasher);
        var settings = Options.Create(new AppSettings { Secret = TestSecret });
        var tokenSvc = new TokenService(settings);
        var emailMock = new Mock<IEmailService>();
        var orgStatusMock = new Mock<IOrganizationStatusService>();
        orgStatusMock.Setup(x => x.IsOrganizationActiveAsync(It.IsAny<Guid>())).ReturnsAsync(true);
        var superAdminMock = new Mock<ISuperAdminService>();
        superAdminMock.Setup(x => x.IsSuperAdminAsync(It.IsAny<int>())).ReturnsAsync(false);

        var svc = new AuthService(repo, emailMock.Object, settings, tokenSvc, hasher, orgStatusMock.Object, superAdminMock.Object);
        return (svc, db);
    }

    [Fact]
    public async Task AdminCreateUserAsync_SameEmailInAnotherOrg_IsRejected()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var (svcA, _) = Build(dbName, orgA);
        await svcA.AdminCreateUserAsync(new CreateUserRequest
        {
            FirstName = "Alice", Email = "shared@test.com", RoleID = 1
        }, createdByUserId: 1);

        var (svcB, _) = Build(dbName, orgB);
        var act = () => svcB.AdminCreateUserAsync(new CreateUserRequest
        {
            FirstName = "Bob", Email = "shared@test.com", RoleID = 1
        }, createdByUserId: 2);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task AdminCreateUserAsync_NewUser_IsStampedIntoTheCreatingAdminsOwnOrg()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();

        var (svc, db) = Build(dbName, orgA);
        await svc.AdminCreateUserAsync(new CreateUserRequest
        {
            FirstName = "Alice", Email = "alice@test.com", RoleID = 1
        }, createdByUserId: 1);

        var created = await db.UserAccounts.IgnoreQueryFilters().SingleAsync(u => u.Email == "alice@test.com");
        created.OrganizationId.Should().Be(orgA);
    }

    // Regression for a second, real production bug found via this same code path: IX_UserAccounts_Email
    // is a plain (non-filtered) unique index — it still blocks a new INSERT even when the existing
    // row is soft-deleted (IsDelete=1). EmailExistsAsync used to exclude soft-deleted rows from its
    // check, so AdminCreateUserAsync would sail past this check and crash on the raw DB constraint
    // (a generic 500) instead of surfacing the friendly "account already exists" BadRequestException.
    [Fact]
    public async Task AdminCreateUserAsync_EmailBelongsToASoftDeletedUser_IsRejectedCleanly()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var (_, dbSetup) = Build(dbName, orgB);
        dbSetup.UserAccounts.Add(new UserAccount
        {
            FirstName = "Departed", Email = "departed@test.com", Password = "x",
            RoleID = 1, OrganizationId = orgB, IsActive = false, IsDelete = true,
            CreatedDate = DateTime.UtcNow
        });
        await dbSetup.SaveChangesAsync();

        var (svcA, _) = Build(dbName, orgA);
        var act = () => svcA.AdminCreateUserAsync(new CreateUserRequest
        {
            FirstName = "NewHire", Email = "departed@test.com", RoleID = 1
        }, createdByUserId: 1);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task AdminCreateUserAsync_DifferentEmailsInDifferentOrgs_BothSucceed()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var (svcA, _) = Build(dbName, orgA);
        await svcA.AdminCreateUserAsync(new CreateUserRequest
        {
            FirstName = "Alice", Email = "alice@test.com", RoleID = 1
        }, createdByUserId: 1);

        var (svcB, _) = Build(dbName, orgB);
        var act = () => svcB.AdminCreateUserAsync(new CreateUserRequest
        {
            FirstName = "Bob", Email = "bob@test.com", RoleID = 1
        }, createdByUserId: 2);

        await act.Should().NotThrowAsync();
    }
}
