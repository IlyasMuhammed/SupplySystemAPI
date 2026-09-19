using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc.Routing;
using SMS.Modules.Logistics.Controllers;
using SMS.Shared.Authorization;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

/// <summary>
/// T-17 — every Logistics endpoint must declare both gates.
/// <para>
/// This module went its whole life with no <c>[RequirePermission]</c> anywhere while the rest of
/// the system used it 179 times: the endpoints sat behind the feature flag and the global
/// authenticated-user filter and nothing else. Adding attributes once fixes that for today; this
/// test is what stops the next endpoint shipping without them, because nothing else in the build
/// would notice.
/// </para>
/// </summary>
public class ControllerAuthorizationTests
{
    /// <summary>
    /// The deliberate exceptions: endpoints that cannot have a user. Each is guarded another way,
    /// and the list is pinned below so a new anonymous endpoint cannot slip in unnoticed.
    /// </summary>
    private static readonly Type[] DeliberatelyAnonymous =
    [
        // A carrier cannot log in (T-39).
        typeof(CarrierWebhooksController),
        // Neither can a consignee (T-62, decision G11). Guarded instead by a 256-bit token, a
        // per-IP rate limit, an identical 404 for every kind of miss, and a whitelisted payload —
        // each pinned by its own test below.
        typeof(PublicTrackingController)
    ];

    private static IEnumerable<Type> AllControllers() =>
        typeof(DeliveriesController).Assembly
            .GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

    private static IEnumerable<Type> LogisticsControllers() =>
        AllControllers().Where(t => !DeliberatelyAnonymous.Contains(t));

    private static IEnumerable<MethodInfo> ActionsOf(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                  .Where(m => !m.IsSpecialName
                           && m.GetCustomAttributes<HttpMethodAttribute>().Any());

    [Fact]
    public void The_module_actually_has_controllers_to_check()
    {
        // Guards the guard: a reflection query that silently matches nothing would pass every
        // assertion below.
        LogisticsControllers().Should().HaveCountGreaterThanOrEqualTo(4);
    }

    [Fact]
    public void Every_controller_is_behind_the_module_feature_flag()
    {
        foreach (var controller in LogisticsControllers())
            controller.GetCustomAttribute<RequiresFeatureAttribute>()
                      .Should().NotBeNull($"{controller.Name} must declare [RequiresFeature]");
    }

    [Fact]
    public void Every_action_requires_a_permission()
    {
        var unguarded = new List<string>();

        foreach (var controller in LogisticsControllers())
        {
            var onController = controller.GetCustomAttributes<RequirePermissionAttribute>().Any();

            foreach (var action in ActionsOf(controller))
            {
                if (onController || action.GetCustomAttributes<RequirePermissionAttribute>().Any())
                    continue;

                unguarded.Add($"{controller.Name}.{action.Name}");
            }
        }

        unguarded.Should().BeEmpty(
            "an endpoint with no permission is reachable by any authenticated user in an "
          + "organization that has the module switched on");
    }

    [Fact]
    public void Reading_and_writing_are_not_gated_by_the_same_permission()
    {
        // DELIVERY_VIEW must not be enough to create, amend or cancel a delivery. The policy
        // string is "Permission:<CODE>", set by RequirePermissionAttribute.
        var controller = typeof(DeliveriesController);

        PolicyFor(controller, nameof(DeliveriesController.GetList))
            .Should().Be("Permission:DELIVERY_VIEW");
        PolicyFor(controller, nameof(DeliveriesController.GetById))
            .Should().Be("Permission:DELIVERY_VIEW");

        PolicyFor(controller, nameof(DeliveriesController.Create))
            .Should().Be("Permission:DELIVERY_CREATE");
        PolicyFor(controller, nameof(DeliveriesController.CreateFromSource))
            .Should().Be("Permission:DELIVERY_CREATE");

        foreach (var write in new[]
                 {
                     nameof(DeliveriesController.Patch),
                     nameof(DeliveriesController.Delete),
                     nameof(DeliveriesController.Hold),
                     nameof(DeliveriesController.Resume),
                     nameof(DeliveriesController.Cancel),
                     nameof(DeliveriesController.ShortClose)
                 })
            PolicyFor(controller, write).Should().Be("Permission:DELIVERY_EDIT");
    }

    [Fact]
    public void Committing_money_to_a_carrier_needs_its_own_permission()
    {
        // Booking is not an edit. Someone who may amend a delivery does not automatically get to
        // spend money with a carrier.
        PolicyFor(typeof(ConsignmentsController), nameof(ConsignmentsController.BookManually))
            .Should().Be("Permission:SHIPMENT_BOOK");

        PolicyFor(typeof(ConsignmentsController), nameof(ConsignmentsController.Create))
            .Should().NotBe("Permission:SHIPMENT_BOOK");
    }

    [Fact]
    public void The_deprecated_endpoints_are_guarded_by_the_code_their_screens_already_use()
    {
        // DELIVERY_TRACK is what the Angular routes guard the carrier and shipment screens with,
        // so gating the API on it closes the hole without taking access from anyone who has it.
        foreach (var controller in new[] { typeof(CarriersController), typeof(ShipmentsController) })
            controller.GetCustomAttributes<RequirePermissionAttribute>()
                      .Should().NotBeEmpty($"{controller.Name} must declare a permission");
    }

    [Fact]
    public void The_only_anonymous_endpoints_are_the_ones_meant_to_be()
    {
        // [AllowAnonymous] anywhere else in the module — on a controller or a single action — is a
        // hole in the global authenticated-user filter.
        var anonymous = AllControllers()
            .Where(c => c.GetCustomAttribute<AllowAnonymousAttribute>() is not null
                     || ActionsOf(c).Any(a => a.GetCustomAttribute<AllowAnonymousAttribute>() is not null))
            .ToList();

        anonymous.Should().BeEquivalentTo(DeliberatelyAnonymous);
    }

    [Fact]
    public void The_carrier_webhook_endpoint_is_rate_limited_size_limited_and_only_accepts_posts()
    {
        var controller = typeof(CarrierWebhooksController);

        controller.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName
            .Should().Be(CarrierWebhooksController.RateLimitPolicy);

        var actions = ActionsOf(controller).ToList();
        actions.Should().ContainSingle();
        actions[0].GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods).Should().Equal("POST");
        actions[0].GetCustomAttribute<RequestSizeLimitAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void The_public_tracking_endpoint_is_rate_limited_and_only_reads()
    {
        var controller = typeof(PublicTrackingController);

        controller.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName
            .Should().Be(PublicTrackingController.RateLimitPolicy);

        var actions = ActionsOf(controller).ToList();

        actions.Should().ContainSingle("one way in is one thing to get right");
        // Nothing anonymous may change anything.
        actions[0].GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods)
            .Should().Equal(["GET"]);
    }

    [Fact]
    public void Issuing_a_tracking_link_is_an_edit_not_a_read()
    {
        // Creating an unauthenticated way into a consignment's progress, and silently breaking the
        // link the consignee already has, are not reading acts.
        PolicyFor(typeof(TrackingLinksController), nameof(TrackingLinksController.Issue))
            .Should().Contain(PermissionCodes.DELIVERY_EDIT);

        PolicyFor(typeof(TrackingLinksController), nameof(TrackingLinksController.Revoke))
            .Should().Contain(PermissionCodes.DELIVERY_EDIT);

        PolicyFor(typeof(TrackingLinksController), nameof(TrackingLinksController.Get))
            .Should().Contain(PermissionCodes.DELIVERY_VIEW);
    }

    private static string PolicyFor(Type controller, string actionName)
    {
        var action = controller.GetMethod(actionName);
        action.Should().NotBeNull($"{controller.Name}.{actionName} should exist");

        var attribute = action!.GetCustomAttributes<RequirePermissionAttribute>().SingleOrDefault()
            ?? controller.GetCustomAttributes<RequirePermissionAttribute>().SingleOrDefault();

        attribute.Should().NotBeNull($"{controller.Name}.{actionName} must declare a permission");
        return attribute!.Policy!;
    }
}
