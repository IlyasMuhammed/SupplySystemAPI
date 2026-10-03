using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using SMS.Modules.Demand.Controllers;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A32 PB-06/PB-07/PB-12 — api/sale-inquiries: every action gated exactly as API-CONTRACT §4 says (the server
/// requires what the frontend guard requires), the not-found mapping, and the create-quotation endpoint
/// handing off to the quotation service (T-C1-11's endpoint side).
/// </summary>
public class SaleInquiriesControllerTests
{
    private const int UserId = 31;

    public static TheoryData<string, string, string, string[]> Gates => new()
    {
        { nameof(SaleInquiriesController.GetList),         "GET",    "",                                     [PermissionCodes.SALE_INQUIRY_VIEW] },
        { nameof(SaleInquiriesController.Create),          "POST",   "",                                     [PermissionCodes.SALE_INQUIRY_CREATE] },
        { nameof(SaleInquiriesController.GetById),         "GET",    "{uuid:guid}",                          [PermissionCodes.SALE_INQUIRY_VIEW] },
        { nameof(SaleInquiriesController.Update),          "PUT",    "{uuid:guid}",                          [PermissionCodes.SALE_INQUIRY_EDIT] },
        { nameof(SaleInquiriesController.AddLine),         "POST",   "{uuid:guid}/lines",                    [PermissionCodes.SALE_INQUIRY_EDIT] },
        { nameof(SaleInquiriesController.UpdateLine),      "PUT",    "{uuid:guid}/lines/{lineUuid:guid}",    [PermissionCodes.SALE_INQUIRY_EDIT] },
        { nameof(SaleInquiriesController.DeleteLine),      "DELETE", "{uuid:guid}/lines/{lineUuid:guid}",    [PermissionCodes.SALE_INQUIRY_EDIT] },
        { nameof(SaleInquiriesController.ChangeStatus),    "PATCH",  "{uuid:guid}/status",                   [PermissionCodes.SALE_INQUIRY_EDIT] },
        { nameof(SaleInquiriesController.CreateQuotation), "POST",   "{uuid:guid}/create-quotation",         [PermissionCodes.SALE_QUOTATION_CREATE] },
    };

    private static IEnumerable<MethodInfo> Actions() =>
        typeof(SaleInquiriesController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetCustomAttributes<HttpMethodAttribute>().Any());

    [Theory]
    [MemberData(nameof(Gates))]
    public void Every_action_has_the_contracts_route_and_permission(string action, string verb, string template, string[] codes)
    {
        var method = typeof(SaleInquiriesController).GetMethod(action)!;

        var http = method.GetCustomAttributes<HttpMethodAttribute>().Single();
        http.HttpMethods.Should().Equal(verb);
        (http.Template ?? "").Should().Be(template);
        method.GetCustomAttributes<RequirePermissionAttribute>().Should().ContainSingle()
            .Which.AnyOf.Should().BeEquivalentTo(codes);
        method.GetCustomAttributes<AllowAnonymousAttribute>().Should().BeEmpty();
    }

    [Fact]
    public void The_controller_is_feature_gated_and_every_action_is_covered_above()
    {
        var type = typeof(SaleInquiriesController);
        type.GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/sale-inquiries");
        type.GetCustomAttribute<RequiresFeatureAttribute>()!.FeatureCode.Should().Be("MODULE_DEMAND");
        type.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
        Actions().Select(m => m.Name).Should().BeEquivalentTo(Gates.Select(g => (string)g[0]));
    }

    private static (SaleInquiriesController Controller, Mock<ISaleInquiryService> Inquiries, Mock<ISaleQuotationService> Quotations) NewController()
    {
        var inquiries  = new Mock<ISaleInquiryService>();
        var quotations = new Mock<ISaleQuotationService>();
        var controller = new SaleInquiriesController(inquiries.Object, quotations.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", UserId.ToString())], "test"))
                }
            }
        };
        return (controller, inquiries, quotations);
    }

    [Fact]
    public async Task T_C1_11_Create_quotation_hands_the_inquiry_to_the_quotation_service_and_returns_its_uuid()
    {
        var (controller, _, quotations) = NewController();
        var inquiry = Guid.NewGuid();
        var quotation = Guid.NewGuid();
        var req = new CreateSaleQuotationFromInquiryRequest { ValidTo = new DateTime(2026, 5, 1) };
        quotations.Setup(q => q.CreateFromInquiryAsync(inquiry, req, UserId)).ReturnsAsync(quotation);

        var result = await controller.CreateQuotation(inquiry, req);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<ApiResponse<Guid>>()
            .Which.Result.Should().Be(quotation);
        quotations.Verify(q => q.CreateFromInquiryAsync(inquiry, req, UserId), Times.Once);
    }

    [Fact]
    public async Task Create_passes_the_caller_and_returns_the_new_uuid()
    {
        var (controller, inquiries, _) = NewController();
        var uuid = Guid.NewGuid();
        var req = new CreateSaleInquiryRequest { PartnerId = Guid.NewGuid() };
        inquiries.Setup(s => s.CreateAsync(req, UserId)).ReturnsAsync(uuid);

        var result = await controller.Create(req);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<ApiResponse<Guid>>()
            .Which.Result.Should().Be(uuid);
    }

    [Fact]
    public async Task Not_found_in_the_callers_organization_is_a_404_everywhere()
    {
        var (controller, inquiries, _) = NewController();
        var uuid = Guid.NewGuid();
        var line = Guid.NewGuid();
        inquiries.Setup(s => s.GetByIdAsync(uuid)).ReturnsAsync((SaleInquiryModel?)null);
        inquiries.Setup(s => s.UpdateAsync(uuid, It.IsAny<UpdateSaleInquiryRequest>(), UserId)).ReturnsAsync(false);
        inquiries.Setup(s => s.AddLineAsync(uuid, It.IsAny<SaleInquiryLineRequest>(), UserId)).ReturnsAsync((Guid?)null);
        inquiries.Setup(s => s.UpdateLineAsync(uuid, line, It.IsAny<UpdateSaleInquiryLineRequest>(), UserId)).ReturnsAsync(false);
        inquiries.Setup(s => s.DeleteLineAsync(uuid, line, UserId)).ReturnsAsync(false);
        inquiries.Setup(s => s.ChangeStatusAsync(uuid, It.IsAny<ChangeSaleInquiryStatusRequest>(), UserId)).ReturnsAsync((SaleInquiryModel?)null);

        (await controller.GetById(uuid)).Should().BeOfType<NotFoundObjectResult>();
        (await controller.Update(uuid, new UpdateSaleInquiryRequest())).Should().BeOfType<NotFoundObjectResult>();
        (await controller.AddLine(uuid, new SaleInquiryLineRequest())).Should().BeOfType<NotFoundObjectResult>();
        (await controller.UpdateLine(uuid, line, new UpdateSaleInquiryLineRequest())).Should().BeOfType<NotFoundObjectResult>();
        (await controller.DeleteLine(uuid, line)).Should().BeOfType<NotFoundObjectResult>();
        (await controller.ChangeStatus(uuid, new ChangeSaleInquiryStatusRequest { Status = "UNDER_REVIEW" })).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Status_change_returns_the_refreshed_inquiry()
    {
        var (controller, inquiries, _) = NewController();
        var uuid = Guid.NewGuid();
        var model = new SaleInquiryModel { Uuid = uuid, Status = "UNDER_REVIEW" };
        inquiries.Setup(s => s.ChangeStatusAsync(uuid, It.IsAny<ChangeSaleInquiryStatusRequest>(), UserId)).ReturnsAsync(model);

        var result = await controller.ChangeStatus(uuid, new ChangeSaleInquiryStatusRequest { Status = "UNDER_REVIEW" });

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<ApiResponse<SaleInquiryModel>>()
            .Which.Result.Should().BeSameAs(model);
    }
}
