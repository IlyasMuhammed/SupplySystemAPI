using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SMS.Modules.Auth.Controllers;
using SMS.Modules.Auth.Infrastructure;
using SMS.Shared.Authorization;
using Xunit;

namespace SMS.Modules.Auth.Tests.Security;

/// <summary>
/// Every action on <c>api/auth</c> falls into exactly one reviewed category. The global filter only demands a
/// sign-in, so an action that is in none of them is open to every signed-in user of every organization — which
/// is how <c>PUT api/auth/users/{id}/permissions</c> let anyone grant themselves anything until 2026-10-02.
/// A new action fails here until someone decides which category it belongs to.
/// </summary>
public class AuthControllerEndpointClassificationTests
{
    private const string Anonymous = "anonymous";
    private const string SelfService = "self-service";

    /// <summary>
    /// <b>Anonymous</b>: sign-in and account recovery, by credentials or a one-time secret — the same list as the
    /// reviewed one in SMS.Integration.Tests/Security/AnonymousEndpointsTests. <b>Self-service</b>: signed in, and
    /// the target is always the token's own user; any id in the body is ignored. <b>Otherwise</b>: the one
    /// permission the action must require.
    /// </summary>
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["Login"]                = Anonymous,
        ["Refresh"]              = Anonymous,
        ["Logout"]               = Anonymous,
        ["CreateAccount"]        = Anonymous,
        ["ActivateAccount"]      = Anonymous,
        ["AcceptInvite"]         = Anonymous,
        ["ForgotPassword"]       = Anonymous,
        ["ResetPassword"]        = Anonymous,

        ["Me"]                   = SelfService,
        ["UpdateProfile"]        = SelfService,
        ["UpdatePassword"]       = SelfService,
        ["UploadProfilePicture"] = SelfService,
        ["DeleteProfilePicture"] = SelfService,

        ["GetAllUsers"]          = PermissionCodes.USER_MANAGE,
        ["DeactivateUser"]       = PermissionCodes.USER_MANAGE,
        ["GetRolePermissions"]   = PermissionCodes.USER_MANAGE,
        ["GetUserPermissions"]   = PermissionCodes.USER_MANAGE,
        ["SaveUserPermissions"]  = PermissionCodes.USER_MANAGE,
        // Roles in the global catalog are shared by every organization: platform administration only.
        ["SaveRolePermissions"]  = PermissionCodes.PLATFORM_SUPER_ADMIN,
    };

    /// <summary>Anonymous actions that take a guessable or sprayable input are rate limited per client IP.</summary>
    private static readonly Dictionary<string, string> RateLimited = new(StringComparer.Ordinal)
    {
        ["Login"]           = AuthRateLimits.SignIn,
        ["CreateAccount"]   = AuthRateLimits.AccountRecovery,
        ["ActivateAccount"] = AuthRateLimits.AccountRecovery,
        ["AcceptInvite"]    = AuthRateLimits.AccountRecovery,
        ["ForgotPassword"]  = AuthRateLimits.AccountRecovery,
        ["ResetPassword"]   = AuthRateLimits.AccountRecovery,
        // Refresh and Logout take a 128-bit refresh token: nothing to grind, and every open tab refreshes.
    };

    private static IEnumerable<MethodInfo> Actions() =>
        typeof(AuthController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() is null);

    private static MethodInfo Action(string name) => Actions().Single(m => m.Name == name);

    public static TheoryData<string> AllActions()
    {
        var data = new TheoryData<string>();
        foreach (var name in Expected.Keys) data.Add(name);
        return data;
    }

    [Fact]
    public void Every_action_on_the_controller_has_been_classified_and_nothing_classified_has_gone()
    {
        var actual = Actions().Select(m => m.Name).ToHashSet();

        actual.Except(Expected.Keys).Should().BeEmpty("a new api/auth action must be reviewed: anonymous, self-service, or which permission");
        Expected.Keys.Except(actual).Should().BeEmpty("a classified action no longer exists; update the list");
    }

    [Fact]
    public void The_controller_itself_neither_opens_nor_gates_everything()
    {
        typeof(AuthController).GetCustomAttributes<AllowAnonymousAttribute>(true).Should().BeEmpty();
        typeof(AuthController).GetCustomAttributes<RequirePermissionAttribute>(true).Should().BeEmpty(
            "the category is decided per action; a class-level gate would lock out sign-in");
    }

    [Theory]
    [MemberData(nameof(AllActions))]
    public void Each_action_carries_exactly_its_categorys_attributes(string name)
    {
        var method = Action(name);
        var anonymous = method.GetCustomAttributes<AllowAnonymousAttribute>().Any();
        var policies = method.GetCustomAttributes<RequirePermissionAttribute>().Select(a => a.Policy).ToList();

        switch (Expected[name])
        {
            case Anonymous:
                anonymous.Should().BeTrue($"{name} is a reviewed anonymous action");
                policies.Should().BeEmpty();
                break;

            case SelfService:
                anonymous.Should().BeFalse($"{name} acts on the signed-in user");
                policies.Should().BeEmpty($"{name} is every signed-in user's own account; no permission needed");
                // Nothing in the route or query may pick a different user: the target is the token's.
                method.GetParameters()
                    .Where(p => p.GetCustomAttribute<FromRouteAttribute>() is not null || p.GetCustomAttribute<FromQueryAttribute>() is not null
                             || p.ParameterType == typeof(int))
                    .Should().BeEmpty($"{name} must not take a user id from the URL");
                break;

            default:
                anonymous.Should().BeFalse($"{name} administers users or roles");
                policies.Should().Equal(["Permission:" + Expected[name]],
                    $"{name} must require exactly {Expected[name]}");
                break;
        }
    }

    [Theory]
    [MemberData(nameof(AllActions))]
    public void Anonymous_actions_that_can_be_ground_through_are_rate_limited_per_ip(string name)
    {
        var limits = Action(name).GetCustomAttributes<EnableRateLimitingAttribute>().Select(a => a.PolicyName).ToList();

        if (RateLimited.TryGetValue(name, out var policy))
            limits.Should().Equal([policy], $"{name} is anonymous and takes a guessable or sprayable input");
        else
            limits.Should().BeEmpty($"{name} is not meant to carry a rate limit");
    }
}
