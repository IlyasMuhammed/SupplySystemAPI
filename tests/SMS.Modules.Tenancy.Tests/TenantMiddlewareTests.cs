using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using SMS.Shared.Common;
using SMS.Shared.Middleware;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

// MT-004 acceptance criteria for the request pipeline: an active org's request resolves the tenant
// and proceeds; a deactivated org's request is rejected with 401; unauthenticated and Super Admin
// requests bypass tenant resolution entirely.
public class TenantMiddlewareTests
{
    private static DefaultHttpContext NewHttpContext(bool authenticated)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(authenticated ? "TestAuth" : null));
        return context;
    }

    [Fact]
    public async Task InvokeAsync_ActiveOrg_CallsNext_AndStoresSnapshotOnHttpContextItems()
    {
        var orgId = Guid.NewGuid();
        var snapshot = new TenantSnapshot(true, new HashSet<string> { "MODULE_MIR" });

        var tenantContext = new Mock<ITenantContext>();
        tenantContext.SetupGet(t => t.OrganizationId).Returns(orgId);
        tenantContext.SetupGet(t => t.IsSuperAdmin).Returns(false);

        var snapshots = new Mock<ITenantSnapshotProvider>();
        snapshots.Setup(s => s.GetSnapshotAsync(orgId)).ReturnsAsync(snapshot);

        var nextCalled = false;
        var middleware = new TenantMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        var context = NewHttpContext(authenticated: true);
        await middleware.InvokeAsync(context, tenantContext.Object, snapshots.Object);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Items[TenantMiddleware.SnapshotItemKey].Should().Be(snapshot);
    }

    [Fact]
    public async Task InvokeAsync_DeactivatedOrg_Returns401_AndDoesNotCallNext()
    {
        var orgId = Guid.NewGuid();
        var snapshot = new TenantSnapshot(false, new HashSet<string>());

        var tenantContext = new Mock<ITenantContext>();
        tenantContext.SetupGet(t => t.OrganizationId).Returns(orgId);
        tenantContext.SetupGet(t => t.IsSuperAdmin).Returns(false);

        var snapshots = new Mock<ITenantSnapshotProvider>();
        snapshots.Setup(s => s.GetSnapshotAsync(orgId)).ReturnsAsync(snapshot);

        var nextCalled = false;
        var middleware = new TenantMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        var context = NewHttpContext(authenticated: true);
        await middleware.InvokeAsync(context, tenantContext.Object, snapshots.Object);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task InvokeAsync_OrganizationNotFound_Returns401()
    {
        var orgId = Guid.NewGuid();

        var tenantContext = new Mock<ITenantContext>();
        tenantContext.SetupGet(t => t.OrganizationId).Returns(orgId);
        tenantContext.SetupGet(t => t.IsSuperAdmin).Returns(false);

        var snapshots = new Mock<ITenantSnapshotProvider>();
        snapshots.Setup(s => s.GetSnapshotAsync(orgId)).ReturnsAsync((TenantSnapshot?)null);

        var nextCalled = false;
        var middleware = new TenantMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        var context = NewHttpContext(authenticated: true);
        await middleware.InvokeAsync(context, tenantContext.Object, snapshots.Object);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task InvokeAsync_SuperAdmin_BypassesResolution_EvenIfOrgWouldBeInactive()
    {
        var tenantContext = new Mock<ITenantContext>();
        tenantContext.SetupGet(t => t.OrganizationId).Returns(Guid.NewGuid());
        tenantContext.SetupGet(t => t.IsSuperAdmin).Returns(true);

        var snapshots = new Mock<ITenantSnapshotProvider>();

        var nextCalled = false;
        var middleware = new TenantMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        var context = NewHttpContext(authenticated: true);
        await middleware.InvokeAsync(context, tenantContext.Object, snapshots.Object);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        snapshots.Verify(s => s.GetSnapshotAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_UnauthenticatedRequest_BypassesResolution()
    {
        var tenantContext = new Mock<ITenantContext>();
        var snapshots = new Mock<ITenantSnapshotProvider>();

        var nextCalled = false;
        var middleware = new TenantMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        var context = NewHttpContext(authenticated: false);
        await middleware.InvokeAsync(context, tenantContext.Object, snapshots.Object);

        nextCalled.Should().BeTrue();
        snapshots.Verify(s => s.GetSnapshotAsync(It.IsAny<Guid>()), Times.Never);
    }
}
