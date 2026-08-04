using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SMS.Modules.Tenancy.Controllers;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

// MT-004: GET /api/tenant/current — org info + enabled feature codes (matching OrganizationFeatures)
// + the caller's role/permissions, read straight from JWT claims rather than a second DB query.
public class TenantControllerTests
{
    private static TenantController NewController(
        OrganizationDetailModel? org, List<OrganizationFeatureModel> features, Guid orgId, bool isSuperAdmin,
        string roleName, IEnumerable<string> permissions)
    {
        var svc = new Mock<ITenancyService>();
        svc.Setup(s => s.GetOrganizationByIdAsync(orgId)).ReturnsAsync(org);
        svc.Setup(s => s.GetOrganizationFeaturesAsync(orgId)).ReturnsAsync(features);

        var tenantContext = new Mock<ITenantContext>();
        tenantContext.SetupGet(t => t.OrganizationId).Returns(orgId);
        tenantContext.SetupGet(t => t.IsSuperAdmin).Returns(isSuperAdmin);

        var claims = new List<Claim> { new("roleName", roleName) };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        return new TenantController(svc.Object, tenantContext.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };
    }

    [Fact]
    public async Task GetCurrent_ReturnsOrgInfo_EnabledFeaturesOnly_AndRolePermissionsFromClaims()
    {
        var orgId = Guid.NewGuid();
        var org = new OrganizationDetailModel { Id = orgId, OrgCode = "SCM-DEMO", OrgName = "SCM Demo", Plan = "ENTERPRISE", IsActive = true };
        var features = new List<OrganizationFeatureModel>
        {
            new() { FeatureCode = "MODULE_MIR", IsEnabled = true },
            new() { FeatureCode = "MODULE_LOGISTICS", IsEnabled = false },
        };

        var controller = NewController(org, features, orgId, isSuperAdmin: false, roleName: "Procurement Officer",
            permissions: new[] { "PO_CREATE", "PO_VIEW" });

        var result = await controller.GetCurrent();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<ApiResponse<CurrentTenantModel>>().Subject;
        var model = body.Result!;

        model.OrgCode.Should().Be("SCM-DEMO");
        model.Plan.Should().Be("ENTERPRISE");
        model.EnabledFeatureCodes.Should().BeEquivalentTo(new[] { "MODULE_MIR" });
        model.RoleName.Should().Be("Procurement Officer");
        model.Permissions.Should().BeEquivalentTo(new[] { "PO_CREATE", "PO_VIEW" });
        model.IsSuperAdmin.Should().BeFalse();
    }

    [Fact]
    public async Task GetCurrent_OrganizationNotFound_ReturnsNotFound()
    {
        var orgId = Guid.NewGuid();
        var controller = NewController(org: null, features: new List<OrganizationFeatureModel>(), orgId,
            isSuperAdmin: false, roleName: "User", permissions: Array.Empty<string>());

        var result = await controller.GetCurrent();

        result.Should().BeOfType<NotFoundObjectResult>();
    }
}
