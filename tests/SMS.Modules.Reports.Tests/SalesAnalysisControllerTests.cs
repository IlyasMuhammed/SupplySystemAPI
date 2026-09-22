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
using SMS.Modules.Finance.Data;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Logistics.Data;
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
/// A29-P9-04 §15 R4, R5 and R6 — the endpoints: that each lives at the route the row names, is gated by both
/// the reports permission and the one that opens the same data elsewhere, and hands the service what the
/// request said.
/// </summary>
public class SalesAnalysisControllerTests
{
    private static readonly Type Analysis    = typeof(SalesAnalysisReportsController);
    private static readonly Type Fulfilment  = typeof(FulfilmentReportsController);

    private static IEnumerable<MethodInfo> Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetCustomAttributes<HttpMethodAttribute>().Any());

    private static string[] Policies(Type controller, string action) =>
        [.. controller.GetMethod(action)!.GetCustomAttributes<RequirePermissionAttribute>().Select(a => a.Policy!).Order()];

    private static IEnumerable<string> Routes(Type controller)
    {
        var prefix = controller.GetCustomAttribute<RouteAttribute>()!.Template;
        return Actions(controller).SelectMany(a => a.GetCustomAttributes<HttpMethodAttribute>().Select(h => $"{h.HttpMethods.Single()} {prefix}/{h.Template}"));
    }

    // ── Attributes ───────────────────────────────────────────────────────────

    [Fact]
    public void The_actions_are_found_nine_for_sales_analysis_and_three_for_fulfilment()
    {
        Actions(Analysis).Should().HaveCount(9, "guards the reflection: a query that matches nothing passes everything below");
        Actions(Fulfilment).Should().HaveCount(3);
    }

    [Theory]
    [InlineData(typeof(SalesAnalysisReportsController))]
    [InlineData(typeof(FulfilmentReportsController))]
    public void Each_controller_is_an_api_controller_behind_the_reports_feature_and_not_anonymous(Type controller)
    {
        controller.GetCustomAttribute<ApiControllerAttribute>().Should().NotBeNull();
        controller.GetCustomAttribute<RequiresFeatureAttribute>()!.FeatureCode.Should().Be("MODULE_REPORTS");
        controller.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
        Actions(controller).Should().OnlyContain(a => a.GetCustomAttribute<AllowAnonymousAttribute>() == null);
    }

    [Fact]
    public void The_routes_are_the_ones_the_task_names_under_the_sales_reports_prefix()
    {
        Routes(Analysis).Should().BeEquivalentTo(
        [
            "GET api/reports/sales/sales-by-product", "GET api/reports/sales/sales-by-product/pdf", "GET api/reports/sales/sales-by-product/excel",
            "GET api/reports/sales/sales-by-customer", "GET api/reports/sales/sales-by-customer/pdf", "GET api/reports/sales/sales-by-customer/excel",
            "GET api/reports/sales/sales-vs-purchase", "GET api/reports/sales/sales-vs-purchase/pdf", "GET api/reports/sales/sales-vs-purchase/excel",
        ]);
        Routes(Fulfilment).Should().BeEquivalentTo(
        [
            "GET api/reports/sales/fulfillment-status", "GET api/reports/sales/fulfillment-status/pdf", "GET api/reports/sales/fulfillment-status/excel",
        ]);
    }

    [Fact]
    public void No_two_sales_report_actions_share_a_route_or_ASP_NET_could_not_tell_them_apart()
    {
        var all = new[] { typeof(SalesReportsController), typeof(ReceivablesReportsController), Analysis, Fulfilment, typeof(MarginAnalysisReportsController), typeof(ProductLedgerReportsController) }.SelectMany(Routes).ToList();

        all.Should().HaveCount(30, "three for the register, six for the receivables, nine for sales analysis, three for fulfilment, three for margin analysis, six for the product reports");
        all.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(nameof(SalesAnalysisReportsController.GetSalesByProduct), "REPORT_VIEW")]
    [InlineData(nameof(SalesAnalysisReportsController.GetSalesByCustomer), "REPORT_VIEW")]
    [InlineData(nameof(SalesAnalysisReportsController.ExportSalesByProductPdf), "REPORT_EXPORT")]
    [InlineData(nameof(SalesAnalysisReportsController.ExportSalesByProductExcel), "REPORT_EXPORT")]
    [InlineData(nameof(SalesAnalysisReportsController.ExportSalesByCustomerPdf), "REPORT_EXPORT")]
    [InlineData(nameof(SalesAnalysisReportsController.ExportSalesByCustomerExcel), "REPORT_EXPORT")]
    public void Sales_reports_need_the_reports_permission_together_with_the_sales_invoice_permission(string action, string reports)
    {
        Policies(Analysis, action).Should().Equal(new[] { $"Permission:{reports}", "Permission:SALES_INVOICE_VIEW" }.Order());
    }

    [Theory]
    [InlineData(nameof(FulfilmentReportsController.GetFulfilmentStatus), "REPORT_VIEW")]
    [InlineData(nameof(FulfilmentReportsController.ExportFulfilmentStatusPdf), "REPORT_EXPORT")]
    [InlineData(nameof(FulfilmentReportsController.ExportFulfilmentStatusExcel), "REPORT_EXPORT")]
    public void The_fulfilment_report_needs_the_reports_permission_together_with_the_delivery_permission(string action, string reports)
    {
        Policies(Fulfilment, action).Should().Equal(new[] { $"Permission:{reports}", "Permission:DELIVERY_VIEW" }.Order());
    }

    // ── What each action does ────────────────────────────────────────────────

    private static T Body<T>(IActionResult result) where T : class =>
        ((ApiResponse<T>)((OkObjectResult)result).Value!).Result!;

    private static SalesByProductReport ProductSample() => new() { GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0), TotalRecords = 0, Page = 1, PageSize = 1 };
    private static SalesByCustomerReport CustomerSample() => new() { GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0), TotalRecords = 0, Page = 1, PageSize = 1 };
    private static FulfilmentStatusReport FulfilmentSample() => new() { GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0), TotalRecords = 0, Page = 1, PageSize = 1 };

    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [Fact]
    public async Task Each_page_endpoint_hands_its_filter_to_the_paged_query_and_wraps_the_report()
    {
        var sales = new Mock<ISalesAnalysisReportService>();
        var byProduct = new SalesByProductFilter { PartnerId = Guid.NewGuid(), Page = 2 };
        var byCustomer = new SalesByCustomerFilter { Page = 3 };
        var productReport = ProductSample();
        var customerReport = CustomerSample();
        sales.Setup(s => s.GetSalesByProductAsync(byProduct)).ReturnsAsync(productReport);
        sales.Setup(s => s.GetSalesByCustomerAsync(byCustomer)).ReturnsAsync(customerReport);
        var controller = new SalesAnalysisReportsController(sales.Object);

        Body<SalesByProductReport>(await controller.GetSalesByProduct(byProduct)).Should().BeSameAs(productReport);
        Body<SalesByCustomerReport>(await controller.GetSalesByCustomer(byCustomer)).Should().BeSameAs(customerReport);
        sales.Verify(s => s.GetSalesByProductForExportAsync(It.IsAny<SalesByProductFilter>()), Times.Never);
        sales.Verify(s => s.GetSalesByCustomerForExportAsync(It.IsAny<SalesByCustomerFilter>()), Times.Never);

        var fulfilment = new Mock<IFulfilmentReportService>();
        var filter = new FulfilmentStatusFilter { Status = "PICKING", Page = 2 };
        var report = FulfilmentSample();
        fulfilment.Setup(s => s.GetFulfilmentStatusAsync(filter)).ReturnsAsync(report);

        Body<FulfilmentStatusReport>(await new FulfilmentReportsController(fulfilment.Object).GetFulfilmentStatus(filter)).Should().BeSameAs(report);
        fulfilment.Verify(s => s.GetFulfilmentStatusForExportAsync(It.IsAny<FulfilmentStatusFilter>()), Times.Never);
    }

    [Fact]
    public async Task The_sales_downloads_print_the_whole_report_not_the_page_and_are_named_by_the_day_they_were_made()
    {
        var sales = new Mock<ISalesAnalysisReportService>();
        sales.Setup(s => s.GetSalesByProductForExportAsync(It.IsAny<SalesByProductFilter>())).ReturnsAsync(ProductSample());
        sales.Setup(s => s.GetSalesByCustomerForExportAsync(It.IsAny<SalesByCustomerFilter>())).ReturnsAsync(CustomerSample());
        var controller = new SalesAnalysisReportsController(sales.Object);

        var files = new[]
        {
            (FileContentResult)await controller.ExportSalesByProductPdf(new SalesByProductFilter { Page = 4 }),
            (FileContentResult)await controller.ExportSalesByProductExcel(new SalesByProductFilter { Page = 4 }),
            (FileContentResult)await controller.ExportSalesByCustomerPdf(new SalesByCustomerFilter { Page = 4 }),
            (FileContentResult)await controller.ExportSalesByCustomerExcel(new SalesByCustomerFilter { Page = 4 }),
        };

        files.Select(f => (f.ContentType, f.FileDownloadName)).Should().Equal(
            ("application/pdf", "sales-by-product-20260920.pdf"), (Xlsx, "sales-by-product-20260920.xlsx"),
            ("application/pdf", "sales-by-customer-20260920.pdf"), (Xlsx, "sales-by-customer-20260920.xlsx"));
        Encoding.ASCII.GetString(files[0].FileContents, 0, 5).Should().Be("%PDF-");
        Encoding.ASCII.GetString(files[2].FileContents, 0, 5).Should().Be("%PDF-");
        (files[1].FileContents[0], files[1].FileContents[1]).Should().Be(((byte)'P', (byte)'K'));
        (files[3].FileContents[0], files[3].FileContents[1]).Should().Be(((byte)'P', (byte)'K'));
        sales.Verify(s => s.GetSalesByProductAsync(It.IsAny<SalesByProductFilter>()), Times.Never);
        sales.Verify(s => s.GetSalesByCustomerAsync(It.IsAny<SalesByCustomerFilter>()), Times.Never);
    }

    [Fact]
    public async Task The_fulfilment_downloads_print_every_open_delivery_not_the_page_and_are_named_by_the_day_they_were_made()
    {
        var svc = new Mock<IFulfilmentReportService>();
        svc.Setup(s => s.GetFulfilmentStatusForExportAsync(It.IsAny<FulfilmentStatusFilter>())).ReturnsAsync(FulfilmentSample());
        var controller = new FulfilmentReportsController(svc.Object);

        var pdf   = (FileContentResult)await controller.ExportFulfilmentStatusPdf(new FulfilmentStatusFilter { Page = 4 });
        var excel = (FileContentResult)await controller.ExportFulfilmentStatusExcel(new FulfilmentStatusFilter { Page = 4 });

        (pdf.ContentType, pdf.FileDownloadName).Should().Be(("application/pdf", "fulfillment-status-20260920.pdf"));
        (excel.ContentType, excel.FileDownloadName).Should().Be((Xlsx, "fulfillment-status-20260920.xlsx"));
        Encoding.ASCII.GetString(pdf.FileContents, 0, 5).Should().Be("%PDF-");
        (excel.FileContents[0], excel.FileContents[1]).Should().Be(((byte)'P', (byte)'K'));
        svc.Verify(s => s.GetFulfilmentStatusAsync(It.IsAny<FulfilmentStatusFilter>()), Times.Never);
    }
}

/// <summary>
/// That the module registers the services its controllers take, so the endpoints do not answer 500 at
/// runtime for want of a line in <c>AddReportsModule</c>. What the services need from other modules is stood
/// in for; what is under test is that the Reports registration is enough.
/// </summary>
public class SalesAnalysisRegistrationTests
{
    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddReportsModule(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=unused;Database=unused" }).Build());

        services.AddSingleton<ITenantContext>(new StaticTenantContext());
        services.AddDbContext<FinanceDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddDbContext<LogisticsDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddDbContext<DemandDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddDbContext<InventoryDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton(new Mock<ISupplierNameLookupService>().Object);
        services.AddSingleton(new Mock<IProductVariantResolver>().Object);
        services.AddSingleton(new Mock<IPoDocumentTemplateService>().Object);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
    }

    [Fact]
    public void Each_service_is_registered_per_request_and_its_controller_can_be_built_from_it()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();

        var sales = scope.ServiceProvider.GetRequiredService<ISalesAnalysisReportService>();
        var fulfilment = scope.ServiceProvider.GetRequiredService<IFulfilmentReportService>();

        sales.Should().BeOfType<SalesAnalysisReportService>();
        fulfilment.Should().BeOfType<FulfilmentReportService>();
        scope.ServiceProvider.GetRequiredService<ISalesAnalysisReportService>().Should().BeSameAs(sales, "one instance per request");
        scope.ServiceProvider.GetRequiredService<IFulfilmentReportService>().Should().BeSameAs(fulfilment);
        using var other = provider.CreateScope();
        other.ServiceProvider.GetRequiredService<ISalesAnalysisReportService>().Should().NotBeSameAs(sales);
        other.ServiceProvider.GetRequiredService<IFulfilmentReportService>().Should().NotBeSameAs(fulfilment);
        ActivatorUtilities.CreateInstance<SalesAnalysisReportsController>(scope.ServiceProvider).Should().NotBeNull();
        ActivatorUtilities.CreateInstance<FulfilmentReportsController>(scope.ServiceProvider).Should().NotBeNull();
    }
}

/// <summary>
/// The same endpoints through a real ASP.NET Core pipeline: the routes, the query-string binding, the two
/// permission gates each refusing a caller who holds only the other, and the wire shape a browser would parse.
/// The services behind them are mocks; the feature and exception middleware live in SMS.API and are not here.
/// </summary>
public class SalesAnalysisHttpTests : IAsyncLifetime
{
    private readonly Mock<ISalesAnalysisReportService> _sales = new();
    private readonly Mock<IFulfilmentReportService> _fulfilment = new();
    private IHost _host = null!;
    private HttpClient _client = null!;

    private static readonly Guid Customer  = Guid.NewGuid();
    private static readonly Guid Warehouse = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var byProduct = new SalesByProductReport
        {
            CompanyName = "Northwind Trading", GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0),
            Criteria = new SalesByProductCriteria { DateFrom = new DateTime(2026, 9, 1) },
            Totals = [new SalesByProductTotal { CurrencyCode = "PKR", ProductCount = 1, Revenue = 360m }],
            Items = [new SalesByProductItem { ProductUuid = Guid.NewGuid(), ProductName = "Laptop", CurrencyCode = "PKR", QuantitySold = 10m, Revenue = 360m, AverageUnitPrice = 36m }],
            TotalRecords = 1, Page = 1, PageSize = 20, TotalPages = 1
        };

        var byCustomer = new SalesByCustomerReport
        {
            CompanyName = "Northwind Trading", GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0),
            Totals = [new SalesByCustomerTotal { CurrencyCode = "PKR", CustomerCount = 1, OrderCount = 2, InvoiceCount = 3, Revenue = 1400m, AverageOrderValue = 700m }],
            Items = [new SalesByCustomerItem { PartnerId = Customer, CustomerName = "Acme Ltd", CurrencyCode = "PKR", OrderCount = 2, InvoiceCount = 3, Revenue = 1400m, AverageOrderValue = 700m }],
            TotalRecords = 1, Page = 1, PageSize = 20, TotalPages = 1
        };

        var fulfilment = new FulfilmentStatusReport
        {
            CompanyName = "Northwind Trading", GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0),
            ByStatus = [new FulfilmentStatusCount { Status = "PICKING", Count = 1 }],
            ByWarehouse = [new FulfilmentWarehouseCount { WarehouseUuid = Warehouse, WarehouseName = "Lahore Main", Count = 1 }],
            ByDeliveryMode = [new FulfilmentModeCount { DeliveryMode = "SHIP", Count = 1 }],
            Items = [new FulfilmentStatusItem
            {
                DeliveryUuid = Guid.NewGuid(), DeliveryNumber = "DLV-2026-00007", SaleOrderNumber = "SO-2026-00042", CustomerName = "Acme Ltd",
                Status = "PICKING", DeliveryMode = "SHIP", WarehouseUuid = Warehouse, WarehouseName = "Lahore Main", DaysOpen = 10,
                LineCount = 2, QuantityOrdered = 15.5m, QuantityDelivered = 4m
            }],
            TotalRecords = 1, Page = 1, PageSize = 20, TotalPages = 1
        };

        _sales.Setup(s => s.GetSalesByProductAsync(It.IsAny<SalesByProductFilter>())).ReturnsAsync(byProduct);
        _sales.Setup(s => s.GetSalesByProductForExportAsync(It.IsAny<SalesByProductFilter>())).ReturnsAsync(byProduct);
        _sales.Setup(s => s.GetSalesByCustomerAsync(It.IsAny<SalesByCustomerFilter>())).ReturnsAsync(byCustomer);
        _sales.Setup(s => s.GetSalesByCustomerForExportAsync(It.IsAny<SalesByCustomerFilter>())).ReturnsAsync(byCustomer);
        _fulfilment.Setup(s => s.GetFulfilmentStatusAsync(It.IsAny<FulfilmentStatusFilter>())).ReturnsAsync(fulfilment);
        _fulfilment.Setup(s => s.GetFulfilmentStatusForExportAsync(It.IsAny<FulfilmentStatusFilter>())).ReturnsAsync(fulfilment);

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
                }).ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyTheseControllers()));

                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Test", _ => { });
                services.AddAuthorization();
                services.AddSingleton<IAuthorizationPolicyProvider, PermissionClaimPolicyProvider>();
                services.AddSingleton(_sales.Object);
                services.AddSingleton(_fulfilment.Object);
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

    private sealed class OnlyTheseControllers : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            feature.Controllers.Add(typeof(SalesAnalysisReportsController).GetTypeInfo());
            feature.Controllers.Add(typeof(FulfilmentReportsController).GetTypeInfo());
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

    private const string Product  = "/api/reports/sales/sales-by-product";
    private const string Cust     = "/api/reports/sales/sales-by-customer";
    private const string Fulfil   = "/api/reports/sales/fulfillment-status";

    private static readonly string[] SalesView      = [PermissionCodes.REPORT_VIEW, PermissionCodes.SALES_INVOICE_VIEW];
    private static readonly string[] SalesExport    = [PermissionCodes.REPORT_EXPORT, PermissionCodes.SALES_INVOICE_VIEW];
    private static readonly string[] DeliveryView   = [PermissionCodes.REPORT_VIEW, PermissionCodes.DELIVERY_VIEW];
    private static readonly string[] DeliveryExport = [PermissionCodes.REPORT_EXPORT, PermissionCodes.DELIVERY_VIEW];

    public static IEnumerable<object[]> Endpoints() =>
    [
        [Product, string.Join(',', SalesView)], [$"{Product}/pdf", string.Join(',', SalesExport)], [$"{Product}/excel", string.Join(',', SalesExport)],
        [Cust, string.Join(',', SalesView)],    [$"{Cust}/pdf", string.Join(',', SalesExport)],    [$"{Cust}/excel", string.Join(',', SalesExport)],
        [Fulfil, string.Join(',', DeliveryView)], [$"{Fulfil}/pdf", string.Join(',', DeliveryExport)], [$"{Fulfil}/excel", string.Join(',', DeliveryExport)],
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
    [InlineData(Product)]
    [InlineData(Cust)]
    [InlineData(Fulfil)]
    public async Task Viewing_reports_is_not_downloading_them_and_downloading_is_not_viewing(string url)
    {
        var owner = url == Fulfil ? PermissionCodes.DELIVERY_VIEW : PermissionCodes.SALES_INVOICE_VIEW;

        (await Get($"{url}/pdf", "42", PermissionCodes.REPORT_VIEW, owner)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Get($"{url}/excel", "42", PermissionCodes.REPORT_VIEW, owner)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Get(url, "42", PermissionCodes.REPORT_EXPORT, owner)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_permission_that_opens_the_invoices_does_not_open_the_deliveries_or_the_other_way_round()
    {
        (await Get(Fulfil, "42", PermissionCodes.REPORT_VIEW, PermissionCodes.SALES_INVOICE_VIEW)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Get(Product, "42", PermissionCodes.REPORT_VIEW, PermissionCodes.DELIVERY_VIEW)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Get(Cust, "42", PermissionCodes.REPORT_VIEW, PermissionCodes.DELIVERY_VIEW)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_permissions_that_gate_the_other_sales_reports_open_none_of_these()
    {
        foreach (var url in new[] { Product, Cust, Fulfil })
            (await Get(url, "42", PermissionCodes.REPORT_VIEW, PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.CUSTOMER_LEDGER_VIEW, PermissionCodes.PRODUCT_LEDGER_VIEW))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
    }

    // ── Routing and binding ──────────────────────────────────────────────────

    [Fact]
    public async Task Only_get_is_answered()
    {
        var post = new HttpRequestMessage(HttpMethod.Post, Product);
        post.Headers.Add("X-User", "42");
        post.Headers.Add("X-Permissions", string.Join(',', SalesView));

        (await _client.SendAsync(post)).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task The_sales_by_product_filter_is_bound_from_the_query_string_and_the_downloads_ask_for_the_whole_report()
    {
        SalesByProductFilter? seen = null;
        _sales.Setup(s => s.GetSalesByProductAsync(It.IsAny<SalesByProductFilter>())).Callback<SalesByProductFilter>(f => seen = f).ReturnsAsync(new SalesByProductReport());

        (await Get($"{Product}?dateFrom=2026-09-01&dateTo=2026-09-20&partnerId={Customer}&page=3&pageSize=10", "42", SalesView)).StatusCode.Should().Be(HttpStatusCode.OK);
        (seen!.DateFrom, seen.DateTo, seen.PartnerId, seen.Page, seen.PageSize)
            .Should().Be(((DateTime?)new DateTime(2026, 9, 1), (DateTime?)new DateTime(2026, 9, 20), (Guid?)Customer, 3, 10));

        SalesByProductFilter? exported = null;
        _sales.Setup(s => s.GetSalesByProductForExportAsync(It.IsAny<SalesByProductFilter>())).Callback<SalesByProductFilter>(f => exported = f).ReturnsAsync(new SalesByProductReport());
        await Get($"{Product}/pdf?dateFrom=2026-09-01&partnerId={Customer}", "42", SalesExport);
        (exported!.DateFrom, exported.PartnerId).Should().Be(((DateTime?)new DateTime(2026, 9, 1), (Guid?)Customer));
        _sales.Verify(s => s.GetSalesByProductAsync(It.IsAny<SalesByProductFilter>()), Times.Once, "only the first request paged");
    }

    [Fact]
    public async Task The_sales_by_customer_filter_is_bound_from_the_query_string()
    {
        SalesByCustomerFilter? seen = null;
        _sales.Setup(s => s.GetSalesByCustomerAsync(It.IsAny<SalesByCustomerFilter>())).Callback<SalesByCustomerFilter>(f => seen = f).ReturnsAsync(new SalesByCustomerReport());

        await Get($"{Cust}?dateFrom=2026-09-01&dateTo=2026-09-20&page=2&pageSize=5", "42", SalesView);

        (seen!.DateFrom, seen.DateTo, seen.Page, seen.PageSize).Should().Be(((DateTime?)new DateTime(2026, 9, 1), (DateTime?)new DateTime(2026, 9, 20), 2, 5));
    }

    [Fact]
    public async Task The_fulfilment_filter_is_bound_from_the_query_string_and_the_downloads_ask_for_the_whole_report()
    {
        FulfilmentStatusFilter? seen = null;
        _fulfilment.Setup(s => s.GetFulfilmentStatusAsync(It.IsAny<FulfilmentStatusFilter>())).Callback<FulfilmentStatusFilter>(f => seen = f).ReturnsAsync(new FulfilmentStatusReport());

        await Get($"{Fulfil}?status=in_transit&warehouseId={Warehouse}&deliveryMode=self_pickup&page=2&pageSize=5", "42", DeliveryView);
        (seen!.Status, seen.WarehouseId, seen.DeliveryMode, seen.Page, seen.PageSize).Should().Be(("in_transit", (Guid?)Warehouse, "self_pickup", 2, 5));

        FulfilmentStatusFilter? exported = null;
        _fulfilment.Setup(s => s.GetFulfilmentStatusForExportAsync(It.IsAny<FulfilmentStatusFilter>())).Callback<FulfilmentStatusFilter>(f => exported = f).ReturnsAsync(new FulfilmentStatusReport());
        await Get($"{Fulfil}/excel?status=PICKING&warehouseId={Warehouse}&deliveryMode=SHIP", "42", DeliveryExport);
        (exported!.Status, exported.WarehouseId, exported.DeliveryMode).Should().Be(("PICKING", (Guid?)Warehouse, "SHIP"));
    }

    [Fact]
    public async Task With_no_query_string_every_report_is_unfiltered_and_the_first_twenty()
    {
        SalesByProductFilter? product = null;
        SalesByCustomerFilter? customer = null;
        FulfilmentStatusFilter? fulfilment = null;
        _sales.Setup(s => s.GetSalesByProductAsync(It.IsAny<SalesByProductFilter>())).Callback<SalesByProductFilter>(f => product = f).ReturnsAsync(new SalesByProductReport());
        _sales.Setup(s => s.GetSalesByCustomerAsync(It.IsAny<SalesByCustomerFilter>())).Callback<SalesByCustomerFilter>(f => customer = f).ReturnsAsync(new SalesByCustomerReport());
        _fulfilment.Setup(s => s.GetFulfilmentStatusAsync(It.IsAny<FulfilmentStatusFilter>())).Callback<FulfilmentStatusFilter>(f => fulfilment = f).ReturnsAsync(new FulfilmentStatusReport());

        await Get(Product, "42", SalesView);
        await Get(Cust, "42", SalesView);
        await Get(Fulfil, "42", DeliveryView);

        (product!.DateFrom, product.DateTo, product.PartnerId, product.Page, product.PageSize).Should().Be(((DateTime?)null, (DateTime?)null, (Guid?)null, 1, 20));
        (customer!.DateFrom, customer.DateTo, customer.Page, customer.PageSize).Should().Be(((DateTime?)null, (DateTime?)null, 1, 20));
        (fulfilment!.Status, fulfilment.WarehouseId, fulfilment.DeliveryMode, fulfilment.Page, fulfilment.PageSize).Should().Be(((string?)null, (Guid?)null, (string?)null, 1, 20));
    }

    [Theory]
    [InlineData("/api/reports/sales/sales-by-product?partnerId=not-a-guid")]
    [InlineData("/api/reports/sales/sales-by-product?dateFrom=yesterday")]
    [InlineData("/api/reports/sales/sales-by-customer?dateTo=soon")]
    [InlineData("/api/reports/sales/fulfillment-status?warehouseId=not-a-guid")]
    public async Task A_query_value_that_does_not_parse_is_a_bad_request_and_never_reaches_the_service(string url)
    {
        var response = await Get(url, "42", PermissionCodes.REPORT_VIEW, PermissionCodes.SALES_INVOICE_VIEW, PermissionCodes.DELIVERY_VIEW);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _sales.Invocations.Should().BeEmpty();
        _fulfilment.Invocations.Should().BeEmpty();
    }

    // ── The wire ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sales_by_product_comes_back_in_the_usual_envelope_with_totals_rows_and_paging()
    {
        using var json = JsonDocument.Parse(await (await Get(Product, "42", SalesView)).Content.ReadAsStringAsync());
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        var result = json.RootElement.GetProperty("result");

        result.GetProperty("companyName").GetString().Should().Be("Northwind Trading");
        (result.GetProperty("totalRecords").GetInt32(), result.GetProperty("totalPages").GetInt32()).Should().Be((1, 1));
        var total = result.GetProperty("totals")[0];
        (total.GetProperty("currencyCode").GetString(), total.GetProperty("productCount").GetInt32(), total.GetProperty("revenue").GetDecimal()).Should().Be(("PKR", 1, 360m));
        var row = result.GetProperty("items")[0];
        (row.GetProperty("productName").GetString(), row.GetProperty("quantitySold").GetDecimal(), row.GetProperty("revenue").GetDecimal(), row.GetProperty("averageUnitPrice").GetDecimal())
            .Should().Be(("Laptop", 10m, 360m, 36m));
    }

    [Fact]
    public async Task Sales_by_customer_comes_back_with_orders_invoices_revenue_and_the_average_order_value()
    {
        using var json = JsonDocument.Parse(await (await Get(Cust, "42", SalesView)).Content.ReadAsStringAsync());
        var result = json.RootElement.GetProperty("result");

        var row = result.GetProperty("items")[0];
        (row.GetProperty("customerName").GetString(), row.GetProperty("orderCount").GetInt32(), row.GetProperty("invoiceCount").GetInt32(),
         row.GetProperty("revenue").GetDecimal(), row.GetProperty("averageOrderValue").GetDecimal()).Should().Be(("Acme Ltd", 2, 3, 1400m, 700m));
        var total = result.GetProperty("totals")[0];
        (total.GetProperty("customerCount").GetInt32(), total.GetProperty("averageOrderValue").GetDecimal()).Should().Be((1, 700m));
    }

    [Fact]
    public async Task Fulfilment_status_comes_back_with_the_three_counts_and_the_deliveries()
    {
        using var json = JsonDocument.Parse(await (await Get(Fulfil, "42", DeliveryView)).Content.ReadAsStringAsync());
        var result = json.RootElement.GetProperty("result");

        (result.GetProperty("byStatus")[0].GetProperty("status").GetString(), result.GetProperty("byStatus")[0].GetProperty("count").GetInt32()).Should().Be(("PICKING", 1));
        (result.GetProperty("byWarehouse")[0].GetProperty("warehouseName").GetString(), result.GetProperty("byDeliveryMode")[0].GetProperty("deliveryMode").GetString())
            .Should().Be(("Lahore Main", "SHIP"));

        var row = result.GetProperty("items")[0];
        (row.GetProperty("deliveryNumber").GetString(), row.GetProperty("saleOrderNumber").GetString(), row.GetProperty("customerName").GetString(),
         row.GetProperty("status").GetString(), row.GetProperty("daysOpen").GetInt32(), row.GetProperty("quantityOrdered").GetDecimal(), row.GetProperty("quantityDelivered").GetDecimal())
            .Should().Be(("DLV-2026-00007", "SO-2026-00042", "Acme Ltd", "PICKING", 10, 15.5m, 4m));
    }

    [Theory]
    [InlineData(Product, "sales-by-product", true)]
    [InlineData(Product, "sales-by-product", false)]
    [InlineData(Cust, "sales-by-customer", true)]
    [InlineData(Cust, "sales-by-customer", false)]
    [InlineData(Fulfil, "fulfillment-status", true)]
    [InlineData(Fulfil, "fulfillment-status", false)]
    public async Task The_downloads_are_attachments_of_the_right_type_named_by_their_date(string url, string name, bool pdf)
    {
        var response = await Get($"{url}/{(pdf ? "pdf" : "excel")}", "42", url == Fulfil ? DeliveryExport : SalesExport);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(pdf ? "application/pdf" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        response.Content.Headers.ContentDisposition!.FileName.Should().Be($"{name}-20260920.{(pdf ? "pdf" : "xlsx")}");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (pdf) Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
        else (bytes[0], bytes[1]).Should().Be(((byte)'P', (byte)'K'));
    }
}
