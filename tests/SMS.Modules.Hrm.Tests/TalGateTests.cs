using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using SMS.Modules.Hrm.Controllers;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Tests;

/// <summary>Module gate + sub-feature gate on every TAL controller, a permission on every action, and the convert route's AND of two codes.</summary>
public sealed class TalGateTests
{
    public static TheoryData<Type, string> Controllers => new()
    {
        { typeof(VacanciesController), HrmFeatureCodes.Recruitment },
        { typeof(CandidatesController), HrmFeatureCodes.Recruitment },
        { typeof(JobApplicationsController), HrmFeatureCodes.Recruitment },
        { typeof(InterviewsController), HrmFeatureCodes.Recruitment },
        { typeof(OffersController), HrmFeatureCodes.Recruitment },
        { typeof(ReviewCyclesController), HrmFeatureCodes.Performance },
        { typeof(ReviewsController), HrmFeatureCodes.Performance },
        { typeof(GoalsController), HrmFeatureCodes.Performance },
        { typeof(PerformanceDashboardController), HrmFeatureCodes.Performance },
        { typeof(CoursesController), HrmFeatureCodes.Training },
        { typeof(EnrollmentsController), HrmFeatureCodes.Training },
    };

    private static IEnumerable<MethodInfo> Actions(Type controller) => controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

    [Theory]
    [MemberData(nameof(Controllers))]
    public void Every_controller_gates_module_and_feature_and_every_action_a_permission(Type controller, string feature)
    {
        typeof(HrmControllerBase).IsAssignableFrom(controller).Should().BeTrue();
        var gates = controller.GetCustomAttributes<RequiresFeatureAttribute>().Select(a => a.FeatureCode).ToList();
        gates.Should().Contain(ModuleCodes.Hrm).And.Contain(feature);
        var actions = Actions(controller).ToList();
        actions.Should().NotBeEmpty();
        foreach (var action in actions)
            action.GetCustomAttributes<RequirePermissionAttribute>().Should().NotBeEmpty($"{controller.Name}.{action.Name} needs a permission");
    }

    /// <summary>FE-3: the route half of the review matrix in API-CONTRACT Changes. A VIEW-only caller is stopped here on every write.</summary>
    [Theory]
    [InlineData(typeof(ReviewsController), nameof(ReviewsController.Get), new[] { P.HRM_PERFORMANCE_VIEW, P.HRM_PERFORMANCE_MANAGE, P.HRM_SELF_SERVICE })]
    [InlineData(typeof(ReviewsController), nameof(ReviewsController.List), new[] { P.HRM_PERFORMANCE_VIEW, P.HRM_PERFORMANCE_MANAGE })]
    [InlineData(typeof(ReviewsController), nameof(ReviewsController.Mine), new[] { P.HRM_SELF_SERVICE })]
    [InlineData(typeof(ReviewsController), nameof(ReviewsController.Assigned), new[] { P.HRM_SELF_SERVICE })]
    [InlineData(typeof(ReviewsController), nameof(ReviewsController.Update), new[] { P.HRM_PERFORMANCE_MANAGE, P.HRM_SELF_SERVICE })]
    [InlineData(typeof(ReviewsController), nameof(ReviewsController.Submit), new[] { P.HRM_PERFORMANCE_MANAGE, P.HRM_SELF_SERVICE })]
    [InlineData(typeof(ReviewsController), nameof(ReviewsController.Acknowledge), new[] { P.HRM_PERFORMANCE_MANAGE, P.HRM_SELF_SERVICE })]
    [InlineData(typeof(GoalsController), nameof(GoalsController.Create), new[] { P.HRM_PERFORMANCE_MANAGE, P.HRM_SELF_SERVICE })]
    [InlineData(typeof(GoalsController), nameof(GoalsController.Update), new[] { P.HRM_PERFORMANCE_MANAGE, P.HRM_SELF_SERVICE })]
    [InlineData(typeof(GoalsController), nameof(GoalsController.Delete), new[] { P.HRM_PERFORMANCE_MANAGE, P.HRM_SELF_SERVICE })]
    public void Review_routes_accept_exactly_the_codes_in_the_contract(Type controller, string action, string[] codes) =>
        controller.GetMethod(action)!.GetCustomAttributes<RequirePermissionAttribute>().Should().ContainSingle().Which.AnyOf.Should().BeEquivalentTo(codes);

    [Fact]
    public void Convert_requires_both_codes_and_self_service_never_writes_recruitment_or_training()
    {
        var convert = typeof(JobApplicationsController).GetMethod(nameof(JobApplicationsController.Convert))!.GetCustomAttributes<RequirePermissionAttribute>().ToList();
        convert.Should().HaveCount(2);
        convert.Should().Contain(a => a.AnyOf.Count == 1 && a.AnyOf[0] == P.HRM_RECRUITMENT_MANAGE);
        convert.Should().Contain(a => a.AnyOf.Count == 1 && a.AnyOf[0] == P.HRM_EMPLOYEE_EDIT);

        typeof(InterviewsController).GetMethod(nameof(InterviewsController.Mine))!.GetCustomAttributes<RequirePermissionAttribute>()
            .Should().ContainSingle().Which.AnyOf.Should().Equal(P.HRM_RECRUITMENT_VIEW);

        // The one recruitment write open to VIEW: the assigned interviewer's outcome, narrowed further in the service.
        typeof(InterviewsController).GetMethod(nameof(InterviewsController.Update))!.GetCustomAttributes<RequirePermissionAttribute>()
            .Should().ContainSingle().Which.AnyOf.Should().BeEquivalentTo([P.HRM_RECRUITMENT_MANAGE, P.HRM_RECRUITMENT_VIEW]);
        foreach (var write in new[] { typeof(VacanciesController), typeof(CandidatesController), typeof(JobApplicationsController), typeof(OffersController) }
            .SelectMany(Actions).Where(a => !a.GetCustomAttributes<HttpGetAttribute>().Any()))
            write.GetCustomAttributes<RequirePermissionAttribute>().SelectMany(a => a.AnyOf).Should().NotContain(P.HRM_RECRUITMENT_VIEW, $"{write.Name} is a write");

        foreach (var (controller, _) in new[] { (typeof(VacanciesController), 0), (typeof(CandidatesController), 0), (typeof(JobApplicationsController), 0), (typeof(OffersController), 0), (typeof(InterviewsController), 0) })
            foreach (var action in Actions(controller))
                action.GetCustomAttributes<RequirePermissionAttribute>().SelectMany(a => a.AnyOf).Should().NotContain(P.HRM_SELF_SERVICE);
    }
}
