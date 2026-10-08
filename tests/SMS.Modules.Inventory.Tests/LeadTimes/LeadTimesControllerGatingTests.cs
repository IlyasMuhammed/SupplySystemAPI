using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Routing;
using SMS.Modules.Inventory.Controllers;
using SMS.Shared.Authorization;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>A34 D-24 / API-CONTRACT §2: every lead-time action is gated with exactly the contract's codes, under MODULE_INVENTORY.</summary>
public class LeadTimesControllerGatingTests
{
    private static readonly string[] ReadAnyOf =
    [
        PermissionCodes.LEAD_TIME_DEFAULTS_MANAGE, PermissionCodes.INVENTORY_VIEW, PermissionCodes.STOCK_MANAGE,
        PermissionCodes.SALE_ORDER_VIEW
    ];

    private static readonly string[] CalculateAnyOf =
    [
        PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.SALE_ORDER_CREATE, PermissionCodes.SALE_ORDER_EDIT,
        PermissionCodes.SALE_INQUIRY_VIEW, PermissionCodes.SALE_INQUIRY_EDIT, PermissionCodes.SALE_QUOTATION_VIEW,
        PermissionCodes.SALE_QUOTATION_EDIT, PermissionCodes.INVENTORY_VIEW, PermissionCodes.STOCK_MANAGE
    ];

    public static TheoryData<string, string, string, string[]> Actions => new()
    {
        { "Calculate", "POST", "api/lead-time/calculate", CalculateAnyOf },
        { "CalculateManufacturing", "POST", "api/lead-time/calculate-manufacturing", CalculateAnyOf },
        { nameof(LeadTimesController.GetDefaults), "GET", "api/lead-time/defaults", ReadAnyOf },
        { nameof(LeadTimesController.UpdateDefaults), "PUT", "api/lead-time/defaults", [PermissionCodes.LEAD_TIME_DEFAULTS_MANAGE] },
        { nameof(LeadTimesController.GetVariantLeadTimes), "GET", "api/variants/{uuid:guid}/lead-times", ReadAnyOf },
        { nameof(LeadTimesController.UpdateVariantLeadTimes), "PUT", "api/variants/{uuid:guid}/lead-times", [PermissionCodes.STOCK_MANAGE] },
    };

    [Theory]
    [MemberData(nameof(Actions))]
    public void Each_action_has_its_route_verb_and_exact_permissions(string action, string verb, string template, string[] anyOf)
    {
        var method = typeof(LeadTimesController).GetMethod(action)!;

        method.GetCustomAttributes<RequirePermissionAttribute>().SelectMany(a => a.AnyOf).Should().BeEquivalentTo(anyOf);
        var route = method.GetCustomAttributes<HttpMethodAttribute>().Single();
        route.HttpMethods.Should().Equal(verb);
        route.Template.Should().Be(template);
        method.GetCustomAttributes().OfType<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>().Should().BeEmpty();
    }

    [Fact]
    public void The_controller_is_under_MODULE_INVENTORY_and_every_public_action_is_gated()
    {
        typeof(LeadTimesController).GetCustomAttribute<RequiresFeatureAttribute>()!.FeatureCode.Should().Be("MODULE_INVENTORY");
        typeof(LeadTimesController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Should().OnlyContain(m => m.GetCustomAttributes<RequirePermissionAttribute>().Any(), "D-24: every new action is gated");
    }
}
