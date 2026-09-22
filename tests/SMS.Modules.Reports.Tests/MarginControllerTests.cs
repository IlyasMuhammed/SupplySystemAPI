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
/// A29-P9-05 §15 R7 and R8 — the endpoints: that each lives at the route the row names, is gated by both the
/// reports permission and the ones that open the same data elsewhere, and hands the service what the request said.
/// </summary>
public class MarginControllerTests
{
    private static readonly Type Margin   = typeof(MarginAnalysisReportsController);
    private static readonly Type Analysis = typeof(SalesAnalysisReportsController);

    private static string[] Policies(Type controller, string action) =>
        [.. controller.GetMethod(action)!.GetCustomAttributes<RequirePermissionAttribute>().Select(a => a.Policy!).Order()];

    private static IEnumerable<string> Routes(Type controller, string startsWith)
    {
        var prefix = controller.GetCustomAttribute<RouteAttribute>()!.Template;
        return controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(a => a.GetCustomAttributes<HttpMethodAttribute>().Select(h => $"{h.HttpMethods.Single()} {prefix}/{h.Template}"))
            .Where(r => r.Contains(startsWith));
    }

    // ── Attributes ───────────────────────────────────────────────────────────

    [Fact]
    public void The_margin_controller_is_an_api_controller_behind_the_reports_feature_and_not_anonymous()
    {
        Margin.GetCustomAttribute<ApiControllerAttribute>().Should().NotBeNull();
        Margin.GetCustomAttribute<RequiresFeatureAttribute>()!.FeatureCode.Should().Be("MODULE_REPORTS");
        Margin.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
        Margin.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any()).Should().HaveCount(3).And.OnlyContain(a => a.GetCustomAttribute<AllowAnonymousAttribute>() == null);
    }

    [Fact]
    public void The_routes_are_the_ones_the_task_names_under_the_sales_reports_prefix()
    {
        Routes(Margin, "margin-analysis").Should().BeEquivalentTo(
            ["GET api/reports/sales/margin-analysis", "GET api/reports/sales/margin-analysis/pdf", "GET api/reports/sales/margin-analysis/excel"]);
        Routes(Analysis, "sales-vs-purchase").Should().BeEquivalentTo(
            ["GET api/reports/sales/sales-vs-purchase", "GET api/reports/sales/sales-vs-purchase/pdf", "GET api/reports/sales/sales-vs-purchase/excel"]);
    }

    [Theory]
    [InlineData(nameof(MarginAnalysisReportsController.GetMarginAnalysis), "REPORT_VIEW")]
    [InlineData(nameof(MarginAnalysisReportsController.ExportMarginAnalysisPdf), "REPORT_EXPORT")]
    [InlineData(nameof(MarginAnalysisReportsController.ExportMarginAnalysisExcel), "REPORT_EXPORT")]
    public void Margin_analysis_needs_the_reports_permission_together_with_the_sale_order_permission(string action, string reports)
    {
        Policies(Margin, action).Should().Equal(new[] { $"Permission:{reports}", "Permission:SALE_ORDER_VIEW" }.Order());
    }

    [Theory]
    [InlineData(nameof(SalesAnalysisReportsController.GetSalesVsPurchase), "REPORT_VIEW")]
    [InlineData(nameof(SalesAnalysisReportsController.ExportSalesVsPurchasePdf), "REPORT_EXPORT")]
    [InlineData(nameof(SalesAnalysisReportsController.ExportSalesVsPurchaseExcel), "REPORT_EXPORT")]
    public void Sales_vs_purchase_needs_the_reports_permission_with_both_the_invoice_and_the_product_ledger_permissions(string action, string reports)
    {
        Policies(Analysis, action).Should().Equal(new[] { $"Permission:{reports}", "Permission:SALES_INVOICE_VIEW", "Permission:PRODUCT_LEDGER_VIEW" }.Order());
    }

    // ── What each action does ────────────────────────────────────────────────

    private static T Body<T>(IActionResult result) where T : class =>
        ((ApiResponse<T>)((OkObjectResult)result).Value!).Result!;

    private static readonly DateTime Made = new(2026, 9, 20, 10, 30, 0);
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [Fact]
    public async Task Each_page_endpoint_hands_its_filter_to_the_paged_query_and_wraps_the_report()
    {
        var margin = new Mock<IMarginAnalysisReportService>();
        var marginFilter = new MarginAnalysisFilter { GroupBy = "ORDER", Page = 2 };
        var marginReport = new MarginAnalysisReport { GeneratedAt = Made, Page = 1, PageSize = 1 };
        margin.Setup(s => s.GetMarginAnalysisAsync(marginFilter)).ReturnsAsync(marginReport);

        Body<MarginAnalysisReport>(await new MarginAnalysisReportsController(margin.Object).GetMarginAnalysis(marginFilter)).Should().BeSameAs(marginReport);
        margin.Verify(s => s.GetMarginAnalysisForExportAsync(It.IsAny<MarginAnalysisFilter>()), Times.Never);

        var sales = new Mock<ISalesAnalysisReportService>();
        var vsFilter = new SalesVsPurchaseFilter { Period = "WEEK", Page = 3 };
        var vsReport = new SalesVsPurchaseReport { GeneratedAt = Made, Page = 1, PageSize = 1 };
        sales.Setup(s => s.GetSalesVsPurchaseAsync(vsFilter)).ReturnsAsync(vsReport);

        Body<SalesVsPurchaseReport>(await new SalesAnalysisReportsController(sales.Object).GetSalesVsPurchase(vsFilter)).Should().BeSameAs(vsReport);
        sales.Verify(s => s.GetSalesVsPurchaseForExportAsync(It.IsAny<SalesVsPurchaseFilter>()), Times.Never);
    }

    [Fact]
    public async Task The_downloads_print_the_whole_report_not_the_page_and_are_named_by_the_day_they_were_made()
    {
        var margin = new Mock<IMarginAnalysisReportService>();
        margin.Setup(s => s.GetMarginAnalysisForExportAsync(It.IsAny<MarginAnalysisFilter>())).ReturnsAsync(new MarginAnalysisReport { GeneratedAt = Made });
        var sales = new Mock<ISalesAnalysisReportService>();
        sales.Setup(s => s.GetSalesVsPurchaseForExportAsync(It.IsAny<SalesVsPurchaseFilter>())).ReturnsAsync(new SalesVsPurchaseReport { GeneratedAt = Made });

        var marginController = new MarginAnalysisReportsController(margin.Object);
        var salesController  = new SalesAnalysisReportsController(sales.Object);
        var files = new[]
        {
            (FileContentResult)await marginController.ExportMarginAnalysisPdf(new MarginAnalysisFilter { Page = 4 }),
            (FileContentResult)await marginController.ExportMarginAnalysisExcel(new MarginAnalysisFilter { Page = 4 }),
            (FileContentResult)await salesController.ExportSalesVsPurchasePdf(new SalesVsPurchaseFilter { Page = 4 }),
            (FileContentResult)await salesController.ExportSalesVsPurchaseExcel(new SalesVsPurchaseFilter { Page = 4 }),
        };

        files.Select(f => (f.ContentType, f.FileDownloadName)).Should().Equal(
            ("application/pdf", "margin-analysis-20260920.pdf"), (Xlsx, "margin-analysis-20260920.xlsx"),
            ("application/pdf", "sales-vs-purchase-20260920.pdf"), (Xlsx, "sales-vs-purchase-20260920.xlsx"));
        Encoding.ASCII.GetString(files[0].FileContents, 0, 5).Should().Be("%PDF-");
        Encoding.ASCII.GetString(files[2].FileContents, 0, 5).Should().Be("%PDF-");
        (files[1].FileContents[0], files[1].FileContents[1]).Should().Be(((byte)'P', (byte)'K'));
        (files[3].FileContents[0], files[3].FileContents[1]).Should().Be(((byte)'P', (byte)'K'));
        margin.Verify(s => s.GetMarginAnalysisAsync(It.IsAny<MarginAnalysisFilter>()), Times.Never);
        sales.Verify(s => s.GetSalesVsPurchaseAsync(It.IsAny<SalesVsPurchaseFilter>()), Times.Never);
    }
}

/// <summary>
/// That the module registers the service the new controller takes, so the endpoints do not answer 500 at runtime
/// for want of a line in <c>AddReportsModule</c>. What the services need from other modules is stood in for.
/// </summary>
public class MarginRegistrationTests
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
        services.AddSingleton(new Mock<ILookupsService>().Object);
        services.AddSingleton(new Mock<IProductVariantResolver>().Object);
        services.AddSingleton(new Mock<IPoDocumentTemplateService>().Object);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
    }

    [Fact]
    public void The_margin_service_is_registered_per_request_and_its_controller_can_be_built_from_it()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();

        var margin = scope.ServiceProvider.GetRequiredService<IMarginAnalysisReportService>();

        margin.Should().BeOfType<MarginAnalysisReportService>();
        scope.ServiceProvider.GetRequiredService<IMarginAnalysisReportService>().Should().BeSameAs(margin, "one instance per request");
        using var other = provider.CreateScope();
        other.ServiceProvider.GetRequiredService<IMarginAnalysisReportService>().Should().NotBeSameAs(margin);
        ActivatorUtilities.CreateInstance<MarginAnalysisReportsController>(scope.ServiceProvider).Should().NotBeNull();
    }
}

/// <summary>
/// The same endpoints through a real ASP.NET Core pipeline: the routes, the query-string binding, every
/// permission gate refusing a caller who holds only some of them, and the wire shape a browser would parse.
/// The services behind them are mocks; the feature and exception middleware live in SMS.API and are not here.
/// </summary>
public class MarginHttpTests : IAsyncLifetime
{
    private readonly Mock<IMarginAnalysisReportService> _margin = new();
    private readonly Mock<ISalesAnalysisReportService> _sales = new();
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var margin = new MarginAnalysisReport
        {
            CompanyName = "Northwind Trading", GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0),
            Criteria = new MarginAnalysisCriteria { GroupBy = "PRODUCT" },
            Totals = [new MarginAnalysisTotal { CurrencyCode = "PKR", GroupCount = 1, LineCount = 1, UncostedLineCount = 2, SellingValue = 1000m, Cost = 600m, Margin = 400m, MarginPercent = 40m }],
            Items = [new MarginAnalysisItem
            {
                GroupId = Guid.NewGuid(), Name = "Laptop", CurrencyCode = "PKR", LineCount = 1, Quantity = 10m, SellingValue = 1000m, Cost = 600m,
                Margin = 400m, MarginPercent = 40m, AverageSellingPrice = 100m, AverageCost = 60m
            }],
            TotalRecords = 1, Page = 1, PageSize = 20, TotalPages = 1
        };

        var vs = new SalesVsPurchaseReport
        {
            CompanyName = "Northwind Trading", GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0),
            Criteria = new SalesVsPurchaseCriteria { Period = "MONTH" },
            Totals = [new SalesVsPurchaseTotal { CurrencyCode = "PKR", PeriodCount = 1, InvoiceCount = 2, Revenue = 1400m, CostOfGoodsSold = 850m, GrossMargin = 550m, GrossMarginPercent = 39.29m, UncostedRevenue = 0m }],
            Items = [new SalesVsPurchaseItem
            {
                PeriodStart = new DateTime(2026, 9, 1), PeriodLabel = "2026-09", CurrencyCode = "PKR", InvoiceCount = 2, Revenue = 1400m,
                CostOfGoodsSold = 850m, GrossMargin = 550m, GrossMarginPercent = 39.29m, UncostedRevenue = 0m
            }],
            TotalRecords = 1, Page = 1, PageSize = 20, TotalPages = 1
        };

        _margin.Setup(s => s.GetMarginAnalysisAsync(It.IsAny<MarginAnalysisFilter>())).ReturnsAsync(margin);
        _margin.Setup(s => s.GetMarginAnalysisForExportAsync(It.IsAny<MarginAnalysisFilter>())).ReturnsAsync(margin);
        _sales.Setup(s => s.GetSalesVsPurchaseAsync(It.IsAny<SalesVsPurchaseFilter>())).ReturnsAsync(vs);
        _sales.Setup(s => s.GetSalesVsPurchaseForExportAsync(It.IsAny<SalesVsPurchaseFilter>())).ReturnsAsync(vs);

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
                services.AddSingleton(_margin.Object);
                services.AddSingleton(_sales.Object);
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
            feature.Controllers.Add(typeof(MarginAnalysisReportsController).GetTypeInfo());
            feature.Controllers.Add(typeof(SalesAnalysisReportsController).GetTypeInfo());
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

    private const string MarginUrl = "/api/reports/sales/margin-analysis";
    private const string VsUrl     = "/api/reports/sales/sales-vs-purchase";

    private static readonly string[] MarginView   = [PermissionCodes.REPORT_VIEW, PermissionCodes.SALE_ORDER_VIEW];
    private static readonly string[] MarginExport = [PermissionCodes.REPORT_EXPORT, PermissionCodes.SALE_ORDER_VIEW];
    private static readonly string[] VsView       = [PermissionCodes.REPORT_VIEW, PermissionCodes.SALES_INVOICE_VIEW, PermissionCodes.PRODUCT_LEDGER_VIEW];
    private static readonly string[] VsExport     = [PermissionCodes.REPORT_EXPORT, PermissionCodes.SALES_INVOICE_VIEW, PermissionCodes.PRODUCT_LEDGER_VIEW];

    public static IEnumerable<object[]> Endpoints() =>
    [
        [MarginUrl, string.Join(',', MarginView)], [$"{MarginUrl}/pdf", string.Join(',', MarginExport)], [$"{MarginUrl}/excel", string.Join(',', MarginExport)],
        [VsUrl, string.Join(',', VsView)],         [$"{VsUrl}/pdf", string.Join(',', VsExport)],         [$"{VsUrl}/excel", string.Join(',', VsExport)],
    ];

    // ── Who may ──────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Nobody_who_has_not_logged_in_gets_anything(string url, string permissions) =>
        (await Get(url, null, permissions.Split(','))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task All_the_permissions_together_reach_the_endpoint(string url, string permissions) =>
        (await Get(url, "42", permissions.Split(','))).StatusCode.Should().Be(HttpStatusCode.OK);

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Every_permission_but_one_is_refused_whichever_one_it_is(string url, string permissions)
    {
        var all = permissions.Split(',');
        foreach (var missing in all)
            (await Get(url, "42", all.Where(p => p != missing).ToArray())).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{url} without {missing}");
    }

    [Theory]
    [InlineData(MarginUrl)]
    [InlineData(VsUrl)]
    public async Task Viewing_reports_is_not_downloading_them_and_downloading_is_not_viewing(string url)
    {
        var owners = url == VsUrl
            ? new[] { PermissionCodes.SALES_INVOICE_VIEW, PermissionCodes.PRODUCT_LEDGER_VIEW }
            : new[] { PermissionCodes.SALE_ORDER_VIEW };

        (await Get($"{url}/pdf", "42", [PermissionCodes.REPORT_VIEW, .. owners])).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Get($"{url}/excel", "42", [PermissionCodes.REPORT_VIEW, .. owners])).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Get(url, "42", [PermissionCodes.REPORT_EXPORT, .. owners])).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task What_opens_the_orders_does_not_open_the_invoices_and_the_ledger_or_the_other_way_round()
    {
        (await Get(VsUrl, "42", PermissionCodes.REPORT_VIEW, PermissionCodes.SALE_ORDER_VIEW)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Get(MarginUrl, "42", PermissionCodes.REPORT_VIEW, PermissionCodes.SALES_INVOICE_VIEW, PermissionCodes.PRODUCT_LEDGER_VIEW)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Routing and binding ──────────────────────────────────────────────────

    [Fact]
    public async Task Only_get_is_answered()
    {
        foreach (var url in new[] { MarginUrl, VsUrl })
        {
            var post = new HttpRequestMessage(HttpMethod.Post, url);
            post.Headers.Add("X-User", "42");
            post.Headers.Add("X-Permissions", string.Join(',', VsView.Concat(MarginView)));

            (await _client.SendAsync(post)).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, url);
        }
    }

    [Fact]
    public async Task The_margin_filter_is_bound_from_the_query_string_and_the_downloads_ask_for_the_whole_report()
    {
        MarginAnalysisFilter? seen = null;
        _margin.Setup(s => s.GetMarginAnalysisAsync(It.IsAny<MarginAnalysisFilter>())).Callback<MarginAnalysisFilter>(f => seen = f).ReturnsAsync(new MarginAnalysisReport());

        (await Get($"{MarginUrl}?dateFrom=2026-09-01&dateTo=2026-09-20&groupBy=customer&page=3&pageSize=10", "42", MarginView)).StatusCode.Should().Be(HttpStatusCode.OK);
        (seen!.DateFrom, seen.DateTo, seen.GroupBy, seen.Page, seen.PageSize)
            .Should().Be(((DateTime?)new DateTime(2026, 9, 1), (DateTime?)new DateTime(2026, 9, 20), "customer", 3, 10));

        MarginAnalysisFilter? exported = null;
        _margin.Setup(s => s.GetMarginAnalysisForExportAsync(It.IsAny<MarginAnalysisFilter>())).Callback<MarginAnalysisFilter>(f => exported = f).ReturnsAsync(new MarginAnalysisReport());
        await Get($"{MarginUrl}/excel?dateFrom=2026-09-01&groupBy=ORDER", "42", MarginExport);
        (exported!.DateFrom, exported.GroupBy).Should().Be(((DateTime?)new DateTime(2026, 9, 1), "ORDER"));
        _margin.Verify(s => s.GetMarginAnalysisAsync(It.IsAny<MarginAnalysisFilter>()), Times.Once, "only the first request paged");
    }

    [Fact]
    public async Task The_sales_vs_purchase_filter_is_bound_from_the_query_string_and_the_downloads_ask_for_the_whole_report()
    {
        SalesVsPurchaseFilter? seen = null;
        _sales.Setup(s => s.GetSalesVsPurchaseAsync(It.IsAny<SalesVsPurchaseFilter>())).Callback<SalesVsPurchaseFilter>(f => seen = f).ReturnsAsync(new SalesVsPurchaseReport());

        await Get($"{VsUrl}?dateFrom=2026-09-01&dateTo=2026-09-20&period=week&page=2&pageSize=5", "42", VsView);
        (seen!.DateFrom, seen.DateTo, seen.Period, seen.Page, seen.PageSize)
            .Should().Be(((DateTime?)new DateTime(2026, 9, 1), (DateTime?)new DateTime(2026, 9, 20), "week", 2, 5));

        SalesVsPurchaseFilter? exported = null;
        _sales.Setup(s => s.GetSalesVsPurchaseForExportAsync(It.IsAny<SalesVsPurchaseFilter>())).Callback<SalesVsPurchaseFilter>(f => exported = f).ReturnsAsync(new SalesVsPurchaseReport());
        await Get($"{VsUrl}/pdf?dateTo=2026-09-20&period=DAY", "42", VsExport);
        (exported!.DateTo, exported.Period).Should().Be(((DateTime?)new DateTime(2026, 9, 20), "DAY"));
    }

    [Fact]
    public async Task With_no_query_string_both_reports_are_unfiltered_and_the_first_twenty()
    {
        MarginAnalysisFilter? margin = null;
        SalesVsPurchaseFilter? vs = null;
        _margin.Setup(s => s.GetMarginAnalysisAsync(It.IsAny<MarginAnalysisFilter>())).Callback<MarginAnalysisFilter>(f => margin = f).ReturnsAsync(new MarginAnalysisReport());
        _sales.Setup(s => s.GetSalesVsPurchaseAsync(It.IsAny<SalesVsPurchaseFilter>())).Callback<SalesVsPurchaseFilter>(f => vs = f).ReturnsAsync(new SalesVsPurchaseReport());

        await Get(MarginUrl, "42", MarginView);
        await Get(VsUrl, "42", VsView);

        (margin!.DateFrom, margin.DateTo, margin.GroupBy, margin.Page, margin.PageSize).Should().Be(((DateTime?)null, (DateTime?)null, (string?)null, 1, 20));
        (vs!.DateFrom, vs.DateTo, vs.Period, vs.Page, vs.PageSize).Should().Be(((DateTime?)null, (DateTime?)null, (string?)null, 1, 20));
    }

    [Theory]
    [InlineData("/api/reports/sales/margin-analysis?dateFrom=yesterday")]
    [InlineData("/api/reports/sales/margin-analysis?page=first")]
    [InlineData("/api/reports/sales/sales-vs-purchase?dateTo=soon")]
    [InlineData("/api/reports/sales/sales-vs-purchase?pageSize=many")]
    public async Task A_query_value_that_does_not_parse_is_a_bad_request_and_never_reaches_the_service(string url)
    {
        var response = await Get(url, "42", VsView.Concat(MarginView).ToArray());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _margin.Invocations.Should().BeEmpty();
        _sales.Invocations.Should().BeEmpty();
    }

    // ── The wire ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Margin_analysis_comes_back_in_the_usual_envelope_with_totals_rows_and_paging()
    {
        using var json = JsonDocument.Parse(await (await Get(MarginUrl, "42", MarginView)).Content.ReadAsStringAsync());
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        var result = json.RootElement.GetProperty("result");

        result.GetProperty("companyName").GetString().Should().Be("Northwind Trading");
        result.GetProperty("criteria").GetProperty("groupBy").GetString().Should().Be("PRODUCT");
        (result.GetProperty("totalRecords").GetInt32(), result.GetProperty("totalPages").GetInt32()).Should().Be((1, 1));
        var total = result.GetProperty("totals")[0];
        (total.GetProperty("currencyCode").GetString(), total.GetProperty("uncostedLineCount").GetInt32(), total.GetProperty("margin").GetDecimal(), total.GetProperty("marginPercent").GetDecimal())
            .Should().Be(("PKR", 2, 400m, 40m));
        var row = result.GetProperty("items")[0];
        (row.GetProperty("name").GetString(), row.GetProperty("quantity").GetDecimal(), row.GetProperty("sellingValue").GetDecimal(), row.GetProperty("cost").GetDecimal(),
         row.GetProperty("margin").GetDecimal(), row.GetProperty("averageSellingPrice").GetDecimal(), row.GetProperty("averageCost").GetDecimal())
            .Should().Be(("Laptop", 10m, 1000m, 600m, 400m, 100m, 60m));
    }

    [Fact]
    public async Task Sales_vs_purchase_comes_back_with_revenue_cost_margin_and_what_had_no_cost()
    {
        using var json = JsonDocument.Parse(await (await Get(VsUrl, "42", VsView)).Content.ReadAsStringAsync());
        var result = json.RootElement.GetProperty("result");

        result.GetProperty("criteria").GetProperty("period").GetString().Should().Be("MONTH");
        var row = result.GetProperty("items")[0];
        (row.GetProperty("periodLabel").GetString(), row.GetProperty("invoiceCount").GetInt32(), row.GetProperty("revenue").GetDecimal(), row.GetProperty("costOfGoodsSold").GetDecimal(),
         row.GetProperty("grossMargin").GetDecimal(), row.GetProperty("grossMarginPercent").GetDecimal(), row.GetProperty("uncostedRevenue").GetDecimal())
            .Should().Be(("2026-09", 2, 1400m, 850m, 550m, 39.29m, 0m));
        var total = result.GetProperty("totals")[0];
        (total.GetProperty("periodCount").GetInt32(), total.GetProperty("grossMargin").GetDecimal()).Should().Be((1, 550m));
    }

    [Theory]
    [InlineData(MarginUrl, "margin-analysis", true)]
    [InlineData(MarginUrl, "margin-analysis", false)]
    [InlineData(VsUrl, "sales-vs-purchase", true)]
    [InlineData(VsUrl, "sales-vs-purchase", false)]
    public async Task The_downloads_are_attachments_of_the_right_type_named_by_their_date(string url, string name, bool pdf)
    {
        var response = await Get($"{url}/{(pdf ? "pdf" : "excel")}", "42", url == VsUrl ? VsExport : MarginExport);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(pdf ? "application/pdf" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        response.Content.Headers.ContentDisposition!.FileName.Should().Be($"{name}-20260920.{(pdf ? "pdf" : "xlsx")}");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (pdf) Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
        else (bytes[0], bytes[1]).Should().Be(((byte)'P', (byte)'K'));
    }
}
