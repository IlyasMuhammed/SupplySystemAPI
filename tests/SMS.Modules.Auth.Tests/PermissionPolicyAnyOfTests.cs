using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Auth.Authorization;
using SMS.Shared.Authorization;
using Xunit;

namespace SMS.Modules.Auth.Tests;

/// <summary>
/// The real permission policy provider and handler, as Program.cs wires them. "Permission:A|B" — what
/// [RequirePermission(A, B)] produces — must admit a holder of either code, the way the frontend's
/// permissionGuard(A, B) does: an endpoint that several pages call (a PO read by the PO screens, the GRN form
/// and the invoice form) has to admit everyone those pages admit, or gating it locks real users out.
/// </summary>
public class PermissionPolicyAnyOfTests
{
    private static IAuthorizationService Authorization()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal SignedIn(params string[] permissions) =>
        new(new ClaimsIdentity(
            permissions.Select(p => new Claim("permission", p)).Prepend(new Claim("sub", "42")), "Test"));

    [Theory]
    [InlineData("PO_VIEW",       true)]
    [InlineData("GOODS_RECEIVE", true)]
    [InlineData("INVOICE_VIEW",  false)]
    public async Task An_any_of_policy_admits_a_holder_of_any_one_of_its_codes_and_nobody_else(string held, bool admitted)
    {
        var result = await Authorization().AuthorizeAsync(SignedIn(held), "Permission:PO_VIEW|GOODS_RECEIVE");

        result.Succeeded.Should().Be(admitted);
    }

    [Fact]
    public async Task A_single_code_policy_is_unchanged()
    {
        (await Authorization().AuthorizeAsync(SignedIn("PO_VIEW"), "Permission:PO_VIEW")).Succeeded.Should().BeTrue();
        (await Authorization().AuthorizeAsync(SignedIn("PO_CREATE"), "Permission:PO_VIEW")).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Holding_the_joined_name_as_one_claim_admits_nobody()
    {
        // The old provider read "PO_VIEW|GOODS_RECEIVE" as one code; no real user holds that, but a forged-looking
        // claim must not be what opens an any-of endpoint.
        (await Authorization().AuthorizeAsync(SignedIn("PO_VIEW|GOODS_RECEIVE"), "Permission:PO_VIEW|GOODS_RECEIVE"))
            .Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_whatever_the_codes()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        (await Authorization().AuthorizeAsync(anonymous, "Permission:PO_VIEW|GOODS_RECEIVE")).Succeeded.Should().BeFalse();
    }

    [Fact]
    public void The_attribute_names_an_any_of_policy_and_leaves_a_single_code_as_it_was()
    {
        // A single code keeps its old policy name, so every existing [RequirePermission(X)] and the tests that
        // read "Permission:X" off one are untouched.
        new RequirePermissionAttribute("PO_VIEW").Policy.Should().Be("Permission:PO_VIEW");

        var anyOf = new RequirePermissionAttribute("PO_VIEW", "GOODS_RECEIVE");
        anyOf.Policy.Should().Be("Permission:PO_VIEW|GOODS_RECEIVE");
        anyOf.AnyOf.Should().Equal("PO_VIEW", "GOODS_RECEIVE");
        RequirePermissionAttribute.CodesOf(anyOf.Policy!).Should().Equal("PO_VIEW", "GOODS_RECEIVE");
        RequirePermissionAttribute.CodesOf("SomeOtherPolicy").Should().BeNull();
    }
}
