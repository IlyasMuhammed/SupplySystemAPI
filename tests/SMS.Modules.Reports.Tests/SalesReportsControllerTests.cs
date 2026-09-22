using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Controllers;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using Xunit;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-01 §15 R1 — the register's endpoints: that each lives at the route the spec names, is gated by
/// both the reports permission and the sale order permission, and hands the service what the request said.
/// </summary>
public class SalesReportsControllerTests
{
    private static IEnumerable<MethodInfo> Actions() =>
        typeof(SalesReportsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetCustomAttributes<HttpMethodAttribute>().Any());

    private static string[] Policies(MethodInfo action) =>
        [.. action.GetCustomAttributes<RequirePermissionAttribute>().Select(a => a.Policy!).Order()];

    // ── Attributes ───────────────────────────────────────────────────────────

    [Fact]
    public void The_three_actions_are_found()
    {
        Actions().Should().HaveCount(3, "guards the reflection: a query that matches nothing passes everything below");
    }

    [Fact]
    public void The_controller_is_an_api_controller_behind_the_reports_feature_and_not_anonymous()
    {
        var c = typeof(SalesReportsController);

        c.GetCustomAttribute<ApiControllerAttribute>().Should().NotBeNull();
        c.GetCustomAttribute<RequiresFeatureAttribute>()!.FeatureCode.Should().Be("MODULE_REPORTS");
        c.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
        Actions().Should().OnlyContain(a => a.GetCustomAttribute<AllowAnonymousAttribute>() == null);
    }

    [Fact]
    public void The_routes_are_the_ones_section_15_names_with_the_register_as_order_register()
    {
        var prefix = typeof(SalesReportsController).GetCustomAttribute<RouteAttribute>()!.Template;

        Actions().SelectMany(a => a.GetCustomAttributes<HttpMethodAttribute>().Select(h => $"{h.HttpMethods.Single()} {prefix}/{h.Template}"))
                 .Should().BeEquivalentTo(
                 [
                     "GET api/reports/sales/order-register",
                     "GET api/reports/sales/order-register/pdf",
                     "GET api/reports/sales/order-register/excel",
                 ]);
    }

    [Fact]
    public void Viewing_needs_the_reports_permission_and_the_sale_order_permission_together()
    {
        Policies(typeof(SalesReportsController).GetMethod(nameof(SalesReportsController.GetOrderRegister))!)
            .Should().Equal("Permission:REPORT_VIEW", "Permission:SALE_ORDER_VIEW");
    }

    [Theory]
    [InlineData(nameof(SalesReportsController.ExportOrderRegisterPdf))]
    [InlineData(nameof(SalesReportsController.ExportOrderRegisterExcel))]
    public void Downloading_needs_the_export_permission_and_the_sale_order_permission_together(string action)
    {
        Policies(typeof(SalesReportsController).GetMethod(action)!).Should().Equal("Permission:REPORT_EXPORT", "Permission:SALE_ORDER_VIEW");
    }

    // ── What each action does ────────────────────────────────────────────────

    private static SalesOrderRegisterReport Sample => RegisterWorld.Report([RegisterWorld.Item()]);

    private static T Body<T>(IActionResult result) where T : class =>
        ((ApiResponse<T>)((OkObjectResult)result).Value!).Result!;

    [Fact]
    public async Task The_page_endpoint_hands_the_filter_to_the_paged_query_and_wraps_the_report()
    {
        var svc    = new Mock<ISalesReportService>();
        var filter = new SalesOrderRegisterFilter { Status = "DRAFT", Page = 2, PageSize = 5 };
        var report = Sample;
        svc.Setup(s => s.GetOrderRegisterAsync(filter)).ReturnsAsync(report);

        var result = await new SalesReportsController(svc.Object).GetOrderRegister(filter);

        Body<SalesOrderRegisterReport>(result).Should().BeSameAs(report);
        svc.Verify(s => s.GetOrderRegisterForExportAsync(It.IsAny<SalesOrderRegisterFilter>()), Times.Never);
    }

    [Fact]
    public async Task The_pdf_endpoint_prints_the_whole_register_not_the_page_and_names_the_file_by_its_date()
    {
        var svc    = new Mock<ISalesReportService>();
        var filter = new SalesOrderRegisterFilter { Page = 4, PageSize = 2 };
        svc.Setup(s => s.GetOrderRegisterForExportAsync(filter)).ReturnsAsync(Sample);

        var file = (FileContentResult)await new SalesReportsController(svc.Object).ExportOrderRegisterPdf(filter);

        file.ContentType.Should().Be("application/pdf");
        file.FileDownloadName.Should().Be("sales-order-register-20260920.pdf");
        Encoding.ASCII.GetString(file.FileContents, 0, 5).Should().Be("%PDF-");
        svc.Verify(s => s.GetOrderRegisterAsync(It.IsAny<SalesOrderRegisterFilter>()), Times.Never);
    }

    [Fact]
    public async Task The_excel_endpoint_lists_the_whole_register_not_the_page_and_names_the_file_by_its_date()
    {
        var svc    = new Mock<ISalesReportService>();
        var filter = new SalesOrderRegisterFilter { Page = 4, PageSize = 2 };
        svc.Setup(s => s.GetOrderRegisterForExportAsync(filter)).ReturnsAsync(Sample);

        var file = (FileContentResult)await new SalesReportsController(svc.Object).ExportOrderRegisterExcel(filter);

        file.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        file.FileDownloadName.Should().Be("sales-order-register-20260920.xlsx");
        (file.FileContents[0], file.FileContents[1]).Should().Be(((byte)'P', (byte)'K'));
        svc.Verify(s => s.GetOrderRegisterAsync(It.IsAny<SalesOrderRegisterFilter>()), Times.Never);
    }
}

/// <summary>
/// That the module registers the service its controller takes, so the endpoints do not answer 500 at
/// runtime for want of one line in <c>AddReportsModule</c>. The three collaborators the service needs are
/// other modules' and are stood in for; what is under test is that the Reports registration is enough.
/// </summary>
public class SalesReportsRegistrationTests
{
    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddReportsModule(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=unused;Database=unused" }).Build());

        services.AddSingleton<ITenantContext>(new StaticTenantContext());
        services.AddDbContext<DemandDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton(new Mock<ISupplierNameLookupService>().Object);
        services.AddSingleton(new Mock<ILookupsService>().Object);
        services.AddSingleton(new Mock<IPoDocumentTemplateService>().Object);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
    }

    [Fact]
    public void The_service_is_registered_per_request_and_the_controller_can_be_built_from_it()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<ISalesReportService>();

        service.Should().BeOfType<SalesReportService>();
        scope.ServiceProvider.GetRequiredService<ISalesReportService>().Should().BeSameAs(service, "one instance per request");
        using var other = provider.CreateScope();
        other.ServiceProvider.GetRequiredService<ISalesReportService>().Should().NotBeSameAs(service);
        ActivatorUtilities.CreateInstance<SalesReportsController>(scope.ServiceProvider).Should().NotBeNull();
    }
}

/// <summary>
/// The same endpoints through a real ASP.NET Core pipeline: the route, the query-string binding, the two
/// permission gates each refusing a caller who holds only the other, and the wire shape a browser would parse.
/// The service behind them is a mock; the feature and exception middleware live in SMS.API and are not here.
/// </summary>
public class SalesReportsHttpTests : IAsyncLifetime
{
    private readonly Mock<ISalesReportService> _svc = new();
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var items = new[]
        {
            RegisterWorld.Item("SO-2026-00002", "Acme Ltd", "CONFIRMED", "SHIP", "PKR", 1000m, 50m, 152m, 1102m, 2, new DateTime(2026, 9, 12)),
            RegisterWorld.Item("SO-2026-00001", "Globex Corp", "DRAFT", "SELF_PICKUP", "USD", 40m, 0m, 0m, 40m, 1, new DateTime(2026, 9, 10)),
        };
        var report = RegisterWorld.Report(items);
        _svc.Setup(s => s.GetOrderRegisterAsync(It.IsAny<SalesOrderRegisterFilter>())).ReturnsAsync(report);
        _svc.Setup(s => s.GetOrderRegisterForExportAsync(It.IsAny<SalesOrderRegisterFilter>())).ReturnsAsync(report);

        _host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddRouting();
                services.AddControllers(options =>
                {
                    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
                    options.Filters.Add(new AuthorizeFilter(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()));
                }).ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyTheSalesReportsController()));

                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Test", _ => { });
                services.AddAuthorization();
                services.AddSingleton<IAuthorizationPolicyProvider, PermissionClaimPolicyProvider>();
                services.AddSingleton(_svc.Object);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(e => e.MapControllers());
            });
        }).StartAsync();

        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    private sealed class OnlyTheSalesReportsController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            feature.Controllers.Add(typeof(SalesReportsController).GetTypeInfo());
        }
    }

    private sealed class HeaderAuthentication : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public HeaderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-User", out var user))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new("sub", user.ToString()) };
            if (Request.Headers.TryGetValue("X-Permissions", out var permissions))
                claims.AddRange(permissions.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => new Claim("permission", p)));

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
        }
    }

    private sealed class PermissionClaimPolicyProvider : DefaultAuthorizationPolicyProvider
    {
        public PermissionClaimPolicyProvider(IOptions<AuthorizationOptions> options) : base(options) { }

        public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
            policyName.StartsWith("Permission:", StringComparison.Ordinal)
                ? Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser().RequireClaim("permission", policyName["Permission:".Length..]).Build())
                : base.GetPolicyAsync(policyName);
    }

    private Task<HttpResponseMessage> Get(string url, string? user, params string[] permissions)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (user is not null) request.Headers.Add("X-User", user);
        request.Headers.Add("X-Permissions", string.Join(',', permissions));
        return _client.SendAsync(request);
    }

    private const string View = "/api/reports/sales/order-register";
    private const string Pdf  = "/api/reports/sales/order-register/pdf";
    private const string Xlsx = "/api/reports/sales/order-register/excel";

    private static readonly string[] ViewPermissions   = [PermissionCodes.REPORT_VIEW, PermissionCodes.SALE_ORDER_VIEW];
    private static readonly string[] ExportPermissions = [PermissionCodes.REPORT_EXPORT, PermissionCodes.SALE_ORDER_VIEW];

    public static IEnumerable<object[]> Endpoints() =>
    [
        [View, string.Join(',', ViewPermissions)],
        [Pdf,  string.Join(',', ExportPermissions)],
        [Xlsx, string.Join(',', ExportPermissions)],
    ];

    // ── Who may ──────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Nobody_who_has_not_logged_in_gets_anything(string url, string permissions) =>
        (await Get(url, null, permissions.Split(','))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Both_permissions_together_reach_the_endpoint(string url, string permissions) =>
        (await Get(url, "42", permissions.Split(','))).StatusCode.Should().Be(HttpStatusCode.OK);

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Either_permission_without_the_other_is_refused(string url, string permissions)
    {
        foreach (var only in permissions.Split(','))
            (await Get(url, "42", only)).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{url} with only {only}");
    }

    [Theory]
    [InlineData(Pdf)]
    [InlineData(Xlsx)]
    public async Task Being_allowed_to_view_reports_is_not_being_allowed_to_download_them(string url) =>
        (await Get(url, "42", PermissionCodes.REPORT_VIEW, PermissionCodes.SALE_ORDER_VIEW)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Being_allowed_to_download_reports_is_not_being_allowed_to_page_through_them_online() =>
        (await Get(View, "42", PermissionCodes.REPORT_EXPORT, PermissionCodes.SALE_ORDER_VIEW)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Every_other_permission_a_neighbouring_role_holds_is_not_enough(string url, string permissions)
    {
        var response = await Get(url, "42", PermissionCodes.SALES_INVOICE_VIEW, PermissionCodes.CUSTOMER_LEDGER_VIEW, PermissionCodes.PRODUCT_LEDGER_VIEW,
            PermissionCodes.INVENTORY_VIEW, PermissionCodes.SALE_ORDER_CREATE, PermissionCodes.SALE_ORDER_EDIT);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Routing and binding ──────────────────────────────────────────────────

    [Fact]
    public async Task Only_get_is_answered()
    {
        var post = new HttpRequestMessage(HttpMethod.Post, View);
        post.Headers.Add("X-User", "42");
        post.Headers.Add("X-Permissions", string.Join(',', ViewPermissions));

        (await _client.SendAsync(post)).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task The_filter_is_bound_from_the_query_string()
    {
        var partner = Guid.NewGuid();
        SalesOrderRegisterFilter? seen = null;
        _svc.Setup(s => s.GetOrderRegisterAsync(It.IsAny<SalesOrderRegisterFilter>()))
            .Callback<SalesOrderRegisterFilter>(f => seen = f).ReturnsAsync(RegisterWorld.Report([]));

        var response = await Get($"{View}?dateFrom=2026-09-01&dateTo=2026-09-20&status=confirmed&partnerId={partner}&deliveryMode=self_pickup&page=3&pageSize=10",
            "42", ViewPermissions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (seen!.DateFrom, seen.DateTo, seen.Status, seen.PartnerId, seen.DeliveryMode, seen.Page, seen.PageSize)
            .Should().Be(((DateTime?)new DateTime(2026, 9, 1), (DateTime?)new DateTime(2026, 9, 20), "confirmed", (Guid?)partner, "self_pickup", 3, 10));
    }

    [Theory]
    [InlineData(Pdf)]
    [InlineData(Xlsx)]
    public async Task The_downloads_bind_the_same_filter_and_ask_for_the_whole_register(string url)
    {
        var partner = Guid.NewGuid();
        SalesOrderRegisterFilter? seen = null;
        _svc.Setup(s => s.GetOrderRegisterForExportAsync(It.IsAny<SalesOrderRegisterFilter>()))
            .Callback<SalesOrderRegisterFilter>(f => seen = f).ReturnsAsync(RegisterWorld.Report([]));

        await Get($"{url}?dateFrom=2026-09-01&status=DRAFT&partnerId={partner}&deliveryMode=SHIP", "42", ExportPermissions);

        (seen!.DateFrom, seen.Status, seen.PartnerId, seen.DeliveryMode).Should().Be(((DateTime?)new DateTime(2026, 9, 1), "DRAFT", (Guid?)partner, "SHIP"));
        _svc.Verify(s => s.GetOrderRegisterAsync(It.IsAny<SalesOrderRegisterFilter>()), Times.Never);
    }

    [Fact]
    public async Task With_no_query_string_it_is_the_first_twenty_of_everything()
    {
        SalesOrderRegisterFilter? seen = null;
        _svc.Setup(s => s.GetOrderRegisterAsync(It.IsAny<SalesOrderRegisterFilter>()))
            .Callback<SalesOrderRegisterFilter>(f => seen = f).ReturnsAsync(RegisterWorld.Report([]));

        await Get(View, "42", ViewPermissions);

        (seen!.Page, seen.PageSize, seen.Status, seen.PartnerId, seen.DeliveryMode, seen.DateFrom, seen.DateTo)
            .Should().Be((1, 20, (string?)null, (Guid?)null, (string?)null, (DateTime?)null, (DateTime?)null));
    }

    [Fact]
    public async Task A_customer_id_that_is_not_a_guid_is_a_bad_request_not_a_register_of_everything()
    {
        var response = await Get($"{View}?partnerId=not-a-guid", "42", ViewPermissions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _svc.Verify(s => s.GetOrderRegisterAsync(It.IsAny<SalesOrderRegisterFilter>()), Times.Never);
    }

    [Fact]
    public async Task A_date_that_is_not_a_date_is_a_bad_request_too()
    {
        (await Get($"{View}?dateFrom=yesterday", "42", ViewPermissions)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── The wire ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_register_comes_back_in_the_usual_envelope_with_rows_totals_and_paging()
    {
        var response = await Get(View, "42", ViewPermissions);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        var result = json.RootElement.GetProperty("result");

        result.GetProperty("companyName").GetString().Should().Be("Northwind Trading");
        result.GetProperty("totalRecords").GetInt32().Should().Be(2);
        result.GetProperty("totalPages").GetInt32().Should().Be(1);

        var first = result.GetProperty("items")[0];
        first.GetProperty("soNumber").GetString().Should().Be("SO-2026-00002");
        first.GetProperty("customerName").GetString().Should().Be("Acme Ltd");
        first.GetProperty("status").GetString().Should().Be("CONFIRMED");
        first.GetProperty("deliveryMode").GetString().Should().Be("SHIP");
        first.GetProperty("currencyCode").GetString().Should().Be("PKR");
        first.GetProperty("lineCount").GetInt32().Should().Be(2);
        (first.GetProperty("subtotal").GetDecimal(), first.GetProperty("discountAmount").GetDecimal(),
         first.GetProperty("taxAmount").GetDecimal(), first.GetProperty("grandTotal").GetDecimal()).Should().Be((1000m, 50m, 152m, 1102m));

        var totals = result.GetProperty("totals");
        totals.GetArrayLength().Should().Be(2);
        totals[0].GetProperty("currencyCode").GetString().Should().Be("PKR");
        totals[1].GetProperty("currencyCode").GetString().Should().Be("USD");
    }

    [Fact]
    public async Task The_pdf_download_is_a_pdf_attachment_named_by_the_date()
    {
        var response = await Get(Pdf, "42", ExportPermissions);

        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        response.Content.Headers.ContentDisposition!.FileName.Should().Be("sales-order-register-20260920.pdf");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public async Task The_excel_download_is_an_xlsx_attachment_named_by_the_date()
    {
        var response = await Get(Xlsx, "42", ExportPermissions);

        response.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        response.Content.Headers.ContentDisposition!.FileName.Should().Be("sales-order-register-20260920.xlsx");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        (bytes[0], bytes[1]).Should().Be(((byte)'P', (byte)'K'));
    }
}
