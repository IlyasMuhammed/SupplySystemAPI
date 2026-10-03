using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Models;
using SMS.Modules.Auth.Repositories;
using SMS.Modules.Auth.Services;
using Xunit;
using static SMS.Modules.Auth.Tests.Security.SecurityTestBed;

namespace SMS.Modules.Auth.Tests.Security;

/// <summary>
/// The anonymous "forgot password" flow. The reset code is six digits and valid for an hour; until 2026-10-02
/// it came from <c>new Random()</c>, could be guessed any number of times, and issuing one did
/// <c>_db.UserAccounts.Update(new UserAccount { UserID, PasswordResetToken, ... })</c> — which marks every
/// other column modified with its default value.
/// </summary>
public class PasswordResetHardeningTests
{
    private const string Rita = "rita.requester@a.test";
    private const string NewPassword = "Brand#New2026";

    private static string CodeOf(SecurityTestBed bed, int userId) => bed.User(userId).PasswordResetToken!;

    /// <summary>Puts a live code on the account directly, so a test of guessing does not depend on issuing.</summary>
    private static string GiveCode(SecurityTestBed bed, int userId, string code = "481516")
    {
        bed.Write(db =>
        {
            var user = db.UserAccounts.Single(u => u.UserID == userId);
            user.PasswordResetToken = code;
            user.PasswordResetTokenTime = DateTime.UtcNow.AddMinutes(60);
        });
        return code;
    }

    private static string WrongCode(string code) => ((int.Parse(code) + 1) % 1_000_000).ToString("D6");

    private static int Reset(SecurityTestBed bed, string email, string? code) =>
        bed.Anonymous(s => s.SetPasswordVerification(new ForgotPasswordModel { Email = email, Password = NewPassword, verificationCode = code }));

    private static bool PasswordIs(SecurityTestBed bed, int userId, string password)
    {
        var user = bed.User(userId);
        return new PasswordHasher<UserAccount>().VerifyHashedPassword(user, user.Password, password) != PasswordVerificationResult.Failed;
    }

    // ── the row wipe ───────────────────────────────────────────────────────────

    [Fact]
    public void Issuing_a_reset_code_changes_the_code_and_its_expiry_and_nothing_else()
    {
        var bed = new SecurityTestBed();
        var before = bed.User(RequesterAId);

        bed.Anonymous(s => s.SendPasswordResetToken(Rita)).Should().Be(1);

        var after = bed.User(RequesterAId);
        after.Should().BeEquivalentTo(before, o => o
            .Excluding(u => u.PasswordResetToken)
            .Excluding(u => u.PasswordResetTokenTime));
        after.PasswordResetToken.Should().MatchRegex("^[0-9]{6}$");
        after.PasswordResetTokenTime.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(60), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Storing_a_reset_code_on_a_context_that_has_not_loaded_the_user_still_leaves_every_other_column_alone()
    {
        // The old Update(new UserAccount{...}) threw on the forgot-password path only because FindByEmail had
        // already tracked the row. On a context that has not, the same call silently blanked the account:
        // name, e-mail, password hash, role, organization.
        var bed = new SecurityTestBed();
        var before = bed.User(RequesterAId);

        using (var fresh = bed.AnonymousDb())
            new AuthRepository(fresh, new PasswordHasher<UserAccount>()).SetPasswordResetCode(RequesterAId, "123456", DateTime.UtcNow.AddMinutes(60));

        var after = bed.User(RequesterAId);
        after.Should().BeEquivalentTo(before, o => o
            .Excluding(u => u.PasswordResetToken)
            .Excluding(u => u.PasswordResetTokenTime));
        after.PasswordResetToken.Should().Be("123456");
    }

    // ── the attempt limit ──────────────────────────────────────────────────────

    [Fact]
    public void The_fifth_wrong_guess_spends_the_code_so_the_right_one_no_longer_works()
    {
        var bed = new SecurityTestBed();
        var code = GiveCode(bed, RequesterAId);

        var answers = Enumerable.Range(1, PasswordResetThrottle.MaxAttemptsPerCode).Select(_ => Reset(bed, Rita, WrongCode(code))).ToList();

        answers.Should().Equal(0, 0, 0, 0, -2);
        bed.User(RequesterAId).PasswordResetToken.Should().BeNull("the code is cleared in the database, not just in memory");
        Reset(bed, Rita, code).Should().Be(-2, "a spent code stays spent, right or wrong");
        PasswordIs(bed, RequesterAId, Password).Should().BeTrue("the password must not have changed");
    }

    [Fact]
    public void Guesses_at_an_address_with_no_account_are_answered_exactly_like_guesses_at_a_real_one()
    {
        var bed = new SecurityTestBed();
        var code = GiveCode(bed, RequesterAId);

        var real = Enumerable.Range(1, 6).Select(_ => Reset(bed, Rita, WrongCode(code))).ToList();
        var none = Enumerable.Range(1, 6).Select(_ => Reset(bed, "nobody@a.test", "123456")).ToList();

        none.Should().Equal(real, "the answers must not reveal which addresses have accounts");
    }

    [Fact]
    public void A_new_code_after_a_spent_one_works()
    {
        var bed = new SecurityTestBed();
        bed.Anonymous(s => s.SendPasswordResetToken(Rita));
        var first = CodeOf(bed, RequesterAId);
        for (var i = 0; i < PasswordResetThrottle.MaxAttemptsPerCode; i++) Reset(bed, Rita, WrongCode(first));

        bed.Anonymous(s => s.SendPasswordResetToken(Rita)).Should().Be(1);
        Reset(bed, Rita, CodeOf(bed, RequesterAId)).Should().Be(1);

        PasswordIs(bed, RequesterAId, NewPassword).Should().BeTrue();
    }

    [Fact]
    public void An_address_gets_at_most_three_codes_an_hour_and_a_fourth_request_sends_nothing()
    {
        var bed = new SecurityTestBed();

        var sent = Enumerable.Range(1, PasswordResetThrottle.MaxCodesPerWindow).Select(_ => bed.Anonymous(s => s.SendPasswordResetToken(Rita))).ToList();
        var third = CodeOf(bed, RequesterAId);
        var fourth = bed.Anonymous(s => s.SendPasswordResetToken(Rita));

        sent.Should().AllBeEquivalentTo(1);
        fourth.Should().Be(0);
        CodeOf(bed, RequesterAId).Should().Be(third, "the fourth request must neither replace the code nor send another");
    }

    // ── success and expiry ─────────────────────────────────────────────────────

    [Fact]
    public void A_successful_reset_clears_the_code_ends_every_session_and_lifts_a_login_lockout()
    {
        var bed = new SecurityTestBed();
        bed.Write(db =>
        {
            var user = db.UserAccounts.Single(u => u.UserID == RequesterAId);
            user.FailedLoginAttempts = 3;
            user.LockedUntil = DateTime.UtcNow.AddMinutes(20);
            db.UserSessions.Add(new UserSession { Id = Guid.NewGuid(), UserID = RequesterAId, TokenHash = "h", ExpiresAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow, OrganizationId = OrgA });
        });
        var code = GiveCode(bed, RequesterAId);

        Reset(bed, Rita, code).Should().Be(1);

        var user = bed.User(RequesterAId);
        user.PasswordResetToken.Should().BeNull();
        user.LockedUntil.Should().BeNull();
        user.FailedLoginAttempts.Should().Be(0);
        bed.Read(db => db.UserSessions.Where(s => s.UserID == RequesterAId).All(s => s.RevokedAt != null)).Should().BeTrue(
            "whoever held a session before the reset may be the reason for it");
        PasswordIs(bed, RequesterAId, NewPassword).Should().BeTrue();
    }

    [Fact]
    public void An_expired_code_is_reported_as_expired_only_to_someone_who_has_the_right_code()
    {
        var bed = new SecurityTestBed();
        var code = GiveCode(bed, RequesterAId);
        bed.Write(db => db.UserAccounts.Single(u => u.UserID == RequesterAId).PasswordResetTokenTime = DateTime.UtcNow.AddMinutes(-1));

        Reset(bed, Rita, WrongCode(code)).Should().Be(0);
        Reset(bed, Rita, code).Should().Be(-1);
        PasswordIs(bed, RequesterAId, Password).Should().BeTrue();
    }

    [Fact]
    public async Task An_admin_triggered_reset_also_starts_the_code_with_a_full_set_of_attempts()
    {
        var bed = new SecurityTestBed();
        bed.Anonymous(s => s.SendPasswordResetToken(Rita));
        var first = CodeOf(bed, RequesterAId);
        for (var i = 0; i < PasswordResetThrottle.MaxAttemptsPerCode; i++) Reset(bed, Rita, WrongCode(first));

        await bed.As(OrgAdminA, s => s.AdminResetPasswordAsync(RequesterAId, OrgAdminA));

        Reset(bed, Rita, CodeOf(bed, RequesterAId)).Should().Be(1);
    }

    // ── profile: e-mail stays unique ───────────────────────────────────────────

    [Fact]
    public void Changing_your_email_to_one_another_account_uses_is_refused_and_changes_nothing()
    {
        var bed = new SecurityTestBed();
        var before = bed.User(RequesterAId);

        var result = bed.Anonymous(s => s.UpdatePersonalInformation(new UpdatePersonalInfoModel
        {
            UserID = RequesterAId, FirstName = "Rita", Email = "ADMIN@sms.local "
        }));

        result.Should().Be(-2);
        bed.User(RequesterAId).Should().BeEquivalentTo(before);
    }
}
