using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using SMS.Modules.Logistics.Controllers;
using SMS.Shared.Authorization;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Routes;

/// <summary>A33 PA-05/PA-06 — the routes API is gated exactly as API-CONTRACT.md §2–§3 says (server = frontend guard).</summary>
public class FulfillmentRoutesControllerTests
{
    private static readonly string[] ReadCodes =
    [
        PermissionCodes.FULFILLMENT_ROUTE_VIEW, PermissionCodes.FULFILLMENT_ROUTE_MANAGE, PermissionCodes.FULFILLMENT_ROUTE_ASSIGN,
        PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.INVENTORY_VIEW, PermissionCodes.DELIVERY_VIEW
    ];

    private static IEnumerable<(MethodInfo Action, HttpMethodAttribute Verb)> Actions() =>
        typeof(FulfillmentRoutesController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => (m, m.GetCustomAttribute<HttpMethodAttribute>()!))
            .Where(x => x.Item2 is not null);

    private static IReadOnlyList<string> PermissionsOf(MethodInfo action) =>
        action.GetCustomAttribute<RequirePermissionAttribute>()!.AnyOf;

    [Fact]
    public void It_lives_at_api_fulfillment_routes_behind_the_logistics_feature()
    {
        typeof(FulfillmentRoutesController).GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/fulfillment-routes");
        typeof(FulfillmentRoutesController).GetCustomAttribute<RequiresFeatureAttribute>()!.FeatureCode.Should().Be("MODULE_LOGISTICS");
    }

    [Fact]
    public void Reads_accept_any_of_the_six_read_codes_and_every_change_needs_manage()
    {
        var actions = Actions().ToList();
        actions.Should().HaveCount(9, "list, get, create, update, deactivate, activate, set-default, clear-default, delete");

        foreach (var (action, verb) in actions)
        {
            if (verb is HttpGetAttribute)
                PermissionsOf(action).Should().BeEquivalentTo(ReadCodes, action.Name);
            else
                PermissionsOf(action).Should().Equal([PermissionCodes.FULFILLMENT_ROUTE_MANAGE], action.Name);
        }
    }

    [Theory]
    [InlineData("{uuid:guid}/deactivate")]
    [InlineData("{uuid:guid}/activate")]
    [InlineData("{uuid:guid}/set-default")]
    [InlineData("{uuid:guid}/clear-default")]
    public void State_changes_are_patches(string template) =>
        Actions().Should().Contain(a => a.Verb is HttpPatchAttribute && a.Verb.Template == template);
}
