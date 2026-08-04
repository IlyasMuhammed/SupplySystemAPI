using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Middleware;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

// MT-004 acceptance criteria: [RequiresFeature] returns 403 (as a ForbiddenException, matching this
// app's centralized exception-to-status-code convention) when the feature is disabled for the
// org, 200 (falls through to the action) when enabled, and Super Admin bypasses it entirely.
public class FeatureAuthorizationFilterTests
{
    private static (ActionExecutingContext Context, ActionExecutionDelegate Next, Func<bool> NextWasCalled)
        NewContext(RequiresFeatureAttribute? attribute, TenantSnapshot? preloadedSnapshot = null)
    {
        var httpContext = new DefaultHttpContext();
        if (preloadedSnapshot is not null)
            httpContext.Items[TenantMiddleware.SnapshotItemKey] = preloadedSnapshot;

        var actionDescriptor = new ActionDescriptor
        {
            EndpointMetadata = attribute is null ? Array.Empty<object>() : new object[] { attribute }
        };
        var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(httpContext, new RouteData(), actionDescriptor);
        var context = new ActionExecutingContext(
            actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());

        var nextCalled = false;
        Task<ActionExecutedContext> Next()
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object()));
        }

        return (context, new ActionExecutionDelegate(Next), () => nextCalled);
    }

    [Fact]
    public async Task OnActionExecutionAsync_NoRequiresFeatureAttribute_CallsNext_WithoutTouchingTenantContext()
    {
        var tenantContext = new Mock<ITenantContext>(MockBehavior.Strict);
        var snapshots = new Mock<ITenantSnapshotProvider>(MockBehavior.Strict);
        var filter = new FeatureAuthorizationFilter(tenantContext.Object, snapshots.Object);

        var (context, next, nextWasCalled) = NewContext(attribute: null);
        await filter.OnActionExecutionAsync(context, next);

        nextWasCalled().Should().BeTrue();
    }

    [Fact]
    public async Task OnActionExecutionAsync_SuperAdmin_BypassesFeatureCheck()
    {
        var tenantContext = new Mock<ITenantContext>();
        tenantContext.SetupGet(t => t.IsSuperAdmin).Returns(true);
        var snapshots = new Mock<ITenantSnapshotProvider>();
        var filter = new FeatureAuthorizationFilter(tenantContext.Object, snapshots.Object);

        var (context, next, nextWasCalled) = NewContext(new RequiresFeatureAttribute("MODULE_MIR"));
        await filter.OnActionExecutionAsync(context, next);

        nextWasCalled().Should().BeTrue();
        snapshots.Verify(s => s.GetSnapshotAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task OnActionExecutionAsync_FeatureEnabled_CallsNext()
    {
        var orgId = Guid.NewGuid();
        var tenantContext = new Mock<ITenantContext>();
        tenantContext.SetupGet(t => t.IsSuperAdmin).Returns(false);
        tenantContext.SetupGet(t => t.OrganizationId).Returns(orgId);

        var snapshots = new Mock<ITenantSnapshotProvider>();
        snapshots.Setup(s => s.GetSnapshotAsync(orgId))
            .ReturnsAsync(new TenantSnapshot(true, new HashSet<string> { "MODULE_MIR" }));

        var filter = new FeatureAuthorizationFilter(tenantContext.Object, snapshots.Object);

        var (context, next, nextWasCalled) = NewContext(new RequiresFeatureAttribute("MODULE_MIR"));
        await filter.OnActionExecutionAsync(context, next);

        nextWasCalled().Should().BeTrue();
    }

    [Fact]
    public async Task OnActionExecutionAsync_FeatureDisabled_ThrowsForbiddenException_AndDoesNotCallNext()
    {
        var orgId = Guid.NewGuid();
        var tenantContext = new Mock<ITenantContext>();
        tenantContext.SetupGet(t => t.IsSuperAdmin).Returns(false);
        tenantContext.SetupGet(t => t.OrganizationId).Returns(orgId);

        var snapshots = new Mock<ITenantSnapshotProvider>();
        snapshots.Setup(s => s.GetSnapshotAsync(orgId))
            .ReturnsAsync(new TenantSnapshot(true, new HashSet<string>())); // MODULE_MIR not enabled

        var filter = new FeatureAuthorizationFilter(tenantContext.Object, snapshots.Object);

        var (context, next, nextWasCalled) = NewContext(new RequiresFeatureAttribute("MODULE_MIR"));
        await Assert.ThrowsAsync<ForbiddenException>(() => filter.OnActionExecutionAsync(context, next));

        nextWasCalled().Should().BeFalse();
    }

    [Fact]
    public async Task OnActionExecutionAsync_ReusesSnapshotFromHttpContextItems_SetByTenantMiddleware()
    {
        var tenantContext = new Mock<ITenantContext>();
        tenantContext.SetupGet(t => t.IsSuperAdmin).Returns(false);
        tenantContext.SetupGet(t => t.OrganizationId).Returns(Guid.NewGuid());

        var snapshots = new Mock<ITenantSnapshotProvider>(MockBehavior.Strict); // must not be called
        var filter = new FeatureAuthorizationFilter(tenantContext.Object, snapshots.Object);

        var preloaded = new TenantSnapshot(true, new HashSet<string> { "MODULE_MIR" });
        var (context, next, nextWasCalled) = NewContext(new RequiresFeatureAttribute("MODULE_MIR"), preloaded);

        await filter.OnActionExecutionAsync(context, next);

        nextWasCalled().Should().BeTrue();
        snapshots.VerifyNoOtherCalls();
    }
}
