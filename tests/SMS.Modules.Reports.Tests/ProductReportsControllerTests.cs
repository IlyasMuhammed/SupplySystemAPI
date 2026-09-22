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
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Services;
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
/// A29-P9-06 §15 R9 and R10 — the endpoints: that each lives at the route the row names, is gated by both the
/// reports permission and the ones that open the same data elsewhere, and hands the service what the request said.
/// </summary>
public class ProductReportsControllerTests
{
    private static readonly Type Controller = typeof(ProductLedgerReportsController);

    private static string[] Policies(string action) =>
        [.. Controller.GetMethod(action)!.GetCustomAttributes<RequirePermissionAttribute>().Select(a => a.Policy!).Order()];

    // ── Attributes ───────────────────────────────────────────────────────────

    [Fact]
    public void The_controller_is_an_api_controller_behind_the_reports_feature_and_not_anonymous()
    {
        var actions = Controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any()).ToList();

        Controller.GetCustomAttribute<ApiControllerAttribute>().Should().NotBeNull();
        Controller.GetCustomAttribute<RequiresFeatureAttribute>()!.FeatureCode.Should().Be("MODULE_REPORTS");
        Controller.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
        actions.Should().HaveCount(6).And.OnlyContain(a => a.GetCustomAttribute<AllowAnonymousAttribute>() == null);
    }

    [Fact]
    public void The_routes_are_the_ones_the_task_names_under_the_sales_reports_prefix()
    {
        var prefix = Controller.GetCustomAttribute<RouteAttribute>()!.Template;

        Controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(a => a.GetCustomAttributes<HttpMethodAttribute>().Select(h => $"{h.HttpMethods.Single()} {prefix}/{h.Template}"))
            .Should().BeEquivalentTo(
            [
                "GET api/reports/sales/product-ledger", "GET api/reports/sales/product-ledger/pdf", "GET api/reports/sales/product-ledger/excel",
                "GET api/reports/sales/product-profitability", "GET api/reports/sales/product-profitability/pdf", "GET api/reports/sales/product-profitability/excel",
            ]);
    }

    [Theory]
    [InlineData(nameof(ProductLedgerReportsController.GetProductLedger), "REPORT_VIEW")]
    [InlineData(nameof(ProductLedgerReportsController.ExportProductLedgerPdf), "REPORT_EXPORT")]
    [InlineData(nameof(ProductLedgerReportsController.ExportProductLedgerExcel), "REPORT_EXPORT")]
    public void The_product_ledger_needs_the_reports_permission_together_with_the_product_ledger_permission(string action, string reports)
    {
        Policies(action).Should().Equal(new[] { $"Permission:{reports}", "Permission:PRODUCT_LEDGER_VIEW" }.Order());
    }

    [Theory]
    [InlineData(nameof(ProductLedgerReportsController.GetProductProfitability), "REPORT_VIEW")]
    [InlineData(nameof(ProductLedgerReportsController.ExportProductProfitabilityPdf), "REPORT_EXPORT")]
    [InlineData(nameof(ProductLedgerReportsController.ExportProductProfitabilityExcel), "REPORT_EXPORT")]
    public void Product_profitability_needs_the_reports_permission_with_both_the_product_ledger_and_the_invoice_permissions(string action, string reports)
    {
        Policies(action).Should().Equal(new[] { $"Permission:{reports}", "Permission:PRODUCT_LEDGER_VIEW", "Permission:SALES_INVOICE_VIEW" }.Order());
    }

    // ── What each action does ────────────────────────────────────────────────

    private static T Body<T>(IActionResult result) where T : class =>
        ((ApiResponse<T>)((OkObjectResult)result).Value!).Result!;

    private static readonly DateTime Made = new(2026, 9, 20, 10, 30, 0);
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [Fact]
    public async Task Each_page_endpoint_hands_its_filter_to_the_paged_query_and_wraps_the_report()
    {
        var svc = new Mock<IProductLedgerReportService>();
        var ledgerFilter = new ProductLedgerReportFilter { ProductId = Guid.NewGuid(), Page = 2 };
        var profitFilter = new ProfitabilityReportFilter { Page = 3 };
        var ledger = new ProductLedgerReport { GeneratedAt = Made, Page = 1, PageSize = 1 };
        var profit = new ProfitabilityReport { GeneratedAt = Made, Page = 1, PageSize = 1 };
        svc.Setup(s => s.GetProductLedgerAsync(ledgerFilter)).ReturnsAsync(ledger);
        svc.Setup(s => s.GetProductProfitabilityAsync(profitFilter)).ReturnsAsync(profit);
        var controller = new ProductLedgerReportsController(svc.Object);

        Body<ProductLedgerReport>(await controller.GetProductLedger(ledgerFilter)).Should().BeSameAs(ledger);
        Body<ProfitabilityReport>(await controller.GetProductProfitability(profitFilter)).Should().BeSameAs(profit);
        svc.Verify(s => s.GetProductLedgerForExportAsync(It.IsAny<ProductLedgerReportFilter>()), Times.Never);
        svc.Verify(s => s.GetProductProfitabilityForExportAsync(It.IsAny<ProfitabilityReportFilter>()), Times.Never);
    }

    [Fact]
    public async Task The_downloads_print_the_whole_report_not_the_page_and_are_named_by_the_day_they_were_made()
    {
        var svc = new Mock<IProductLedgerReportService>();
        svc.Setup(s => s.GetProductLedgerForExportAsync(It.IsAny<ProductLedgerReportFilter>())).ReturnsAsync(new ProductLedgerReport { GeneratedAt = Made });
        svc.Setup(s => s.GetProductProfitabilityForExportAsync(It.IsAny<ProfitabilityReportFilter>())).ReturnsAsync(new ProfitabilityReport { GeneratedAt = Made });
        var controller = new ProductLedgerReportsController(svc.Object);

        var files = new[]
        {
            (FileContentResult)await controller.ExportProductLedgerPdf(new ProductLedgerReportFilter { Page = 4 }),
            (FileContentResult)await controller.ExportProductLedgerExcel(new ProductLedgerReportFilter { Page = 4 }),
            (FileContentResult)await controller.ExportProductProfitabilityPdf(new ProfitabilityReportFilter { Page = 4 }),
            (FileContentResult)await controller.ExportProductProfitabilityExcel(new ProfitabilityReportFilter { Page = 4 }),
        };

        files.Select(f => (f.ContentType, f.FileDownloadName)).Should().Equal(
            ("application/pdf", "product-ledger-20260920.pdf"), (Xlsx, "product-ledger-20260920.xlsx"),
            ("application/pdf", "product-profitability-20260920.pdf"), (Xlsx, "product-profitability-20260920.xlsx"));
        Encoding.ASCII.GetString(files[0].FileContents, 0, 5).Should().Be("%PDF-");
        Encoding.ASCII.GetString(files[2].FileContents, 0, 5).Should().Be("%PDF-");
        (files[1].FileContents[0], files[1].FileContents[1]).Should().Be(((byte)'P', (byte)'K'));
        (files[3].FileContents[0], files[3].FileContents[1]).Should().Be(((byte)'P', (byte)'K'));
        svc.Verify(s => s.GetProductLedgerAsync(It.IsAny<ProductLedgerReportFilter>()), Times.Never);
        svc.Verify(s => s.GetProductProfitabilityAsync(It.IsAny<ProfitabilityReportFilter>()), Times.Never);
    }
}

/// <summary>
/// That the module registers the service the controller takes, so the endpoints do not answer 500 at runtime for
/// want of a line in <c>AddReportsModule</c>. What the service needs from other modules is stood in for; the
/// profitability query is Finance's, registered by Finance.
/// </summary>
public class ProductReportsRegistrationTests
{
    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddReportsModule(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=unused;Database=unused" }).Build());

        services.AddSingleton<ITenantContext>(new StaticTenantContext());
        services.AddDbContext<FinanceDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton(new Mock<ISupplierNameLookupService>().Object);
        services.AddSingleton(new Mock<IProductVariantResolver>().Object);
        services.AddSingleton(new Mock<IProductLedgerQueryService>().Object);
        services.AddSingleton(new Mock<IPoDocumentTemplateService>().Object);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
    }

    [Fact]
    public void The_service_is_registered_per_request_and_its_controller_can_be_built_from_it()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<IProductLedgerReportService>();

        service.Should().BeOfType<ProductLedgerReportService>();
        scope.ServiceProvider.GetRequiredService<IProductLedgerReportService>().Should().BeSameAs(service, "one instance per request");
        using var other = provider.CreateScope();
        other.ServiceProvider.GetRequiredService<IProductLedgerReportService>().Should().NotBeSameAs(service);
        ActivatorUtilities.CreateInstance<ProductLedgerReportsController>(scope.ServiceProvider).Should().NotBeNull();
    }
}

/// <summary>
/// The same endpoints through a real ASP.NET Core pipeline: the routes, the query-string binding, every
/// permission gate refusing a caller who holds only some of them, and the wire shape a browser would parse.
/// The service behind them is a mock; the feature and exception middleware live in SMS.API and are not here.
/// </summary>
public class ProductReportsHttpTests : IAsyncLifetime
{
    private readonly Mock<IProductLedgerReportService> _svc = new();
    private IHost _host = null!;
    private HttpClient _client = null!;

    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Variant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var ledger = new ProductLedgerReport
        {
            CompanyName = "Northwind Trading", GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0),
            Criteria = new ProductLedgerReportCriteria { ProductUuid = Product, ProductName = "Laptop" },
            Summary = new ProductLedgerReportSummary { OpeningQuantity = 20m, OpeningValue = 200m, QuantityIn = 100m, ValueIn = 1000m, ClosingQuantity = 120m, ClosingValue = 1200m, ClosingWeightedAverageCost = 10m, MovementCount = 1 },
            Items = [new ProductLedgerReportEntry
            {
                EntryUuid = Guid.NewGuid(), VariantUuid = Variant, Sku = "LAP-BLK", VariantName = "Black", EntryDate = new DateTime(2026, 9, 5), EntryType = "PURCHASE", Direction = "IN",
                ReferenceNumber = "GRN-2026-00001", PartnerName = "Globex Corp", Quantity = 100m, UnitCost = 10m, TotalCost = 1000m, RunningQty = 120m, RunningValue = 1200m,
                WeightedAverageCost = 10m, VariantRunningQty = 100m, VariantRunningValue = 1000m
            }],
            TotalRecords = 1, Page = 1, PageSize = 20, TotalPages = 1
        };

        var profit = new ProfitabilityReport
        {
            CompanyName = "Northwind Trading", GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0),
            Totals = [new ProfitabilityReportTotal { CurrencyCode = "PKR", ProductCount = 1, Revenue = 5300m, CostOfGoodsSold = 3200m, GrossProfit = 2100m, MarginPercent = 39.62m }],
            Items = [new ProfitabilityReportItem { Rank = 1, ProductUuid = Product, ProductName = "Laptop", CurrencyCode = "PKR", QuantitySold = 5m, Revenue = 5300m, CostOfGoodsSold = 3200m, GrossProfit = 2100m, MarginPercent = 39.62m }],
            TotalRecords = 1, Page = 1, PageSize = 20, TotalPages = 1
        };

        _svc.Setup(s => s.GetProductLedgerAsync(It.IsAny<ProductLedgerReportFilter>())).ReturnsAsync(ledger);
        _svc.Setup(s => s.GetProductLedgerForExportAsync(It.IsAny<ProductLedgerReportFilter>())).ReturnsAsync(ledger);
        _svc.Setup(s => s.GetProductProfitabilityAsync(It.IsAny<ProfitabilityReportFilter>())).ReturnsAsync(profit);
        _svc.Setup(s => s.GetProductProfitabilityForExportAsync(It.IsAny<ProfitabilityReportFilter>())).ReturnsAsync(profit);

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
                }).ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyThisController()));

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

    private sealed class OnlyThisController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            feature.Controllers.Add(typeof(ProductLedgerReportsController).GetTypeInfo());
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

    private const string LedgerUrl = "/api/reports/sales/product-ledger";
    private const string ProfitUrl = "/api/reports/sales/product-profitability";

    private static readonly string[] LedgerView   = [PermissionCodes.REPORT_VIEW, PermissionCodes.PRODUCT_LEDGER_VIEW];
    private static readonly string[] LedgerExport = [PermissionCodes.REPORT_EXPORT, PermissionCodes.PRODUCT_LEDGER_VIEW];
    private static readonly string[] ProfitView   = [PermissionCodes.REPORT_VIEW, PermissionCodes.PRODUCT_LEDGER_VIEW, PermissionCodes.SALES_INVOICE_VIEW];
    private static readonly string[] ProfitExport = [PermissionCodes.REPORT_EXPORT, PermissionCodes.PRODUCT_LEDGER_VIEW, PermissionCodes.SALES_INVOICE_VIEW];

    public static IEnumerable<object[]> Endpoints() =>
    [
        [LedgerUrl, string.Join(',', LedgerView)], [$"{LedgerUrl}/pdf", string.Join(',', LedgerExport)], [$"{LedgerUrl}/excel", string.Join(',', LedgerExport)],
        [ProfitUrl, string.Join(',', ProfitView)], [$"{ProfitUrl}/pdf", string.Join(',', ProfitExport)], [$"{ProfitUrl}/excel", string.Join(',', ProfitExport)],
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
    [InlineData(LedgerUrl)]
    [InlineData(ProfitUrl)]
    public async Task Viewing_reports_is_not_downloading_them_and_downloading_is_not_viewing(string url)
    {
        var owners = url == ProfitUrl
            ? new[] { PermissionCodes.PRODUCT_LEDGER_VIEW, PermissionCodes.SALES_INVOICE_VIEW }
            : new[] { PermissionCodes.PRODUCT_LEDGER_VIEW };

        (await Get($"{url}/pdf", "42", [PermissionCodes.REPORT_VIEW, .. owners])).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Get($"{url}/excel", "42", [PermissionCodes.REPORT_VIEW, .. owners])).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Get(url, "42", [PermissionCodes.REPORT_EXPORT, .. owners])).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_other_sales_reports_permissions_open_neither_product_report()
    {
        foreach (var url in new[] { LedgerUrl, ProfitUrl })
            (await Get(url, "42", PermissionCodes.REPORT_VIEW, PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.SALES_INVOICE_VIEW, PermissionCodes.CUSTOMER_LEDGER_VIEW, PermissionCodes.DELIVERY_VIEW))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
    }

    // ── Routing and binding ──────────────────────────────────────────────────

    [Fact]
    public async Task Only_get_is_answered()
    {
        foreach (var url in new[] { LedgerUrl, ProfitUrl })
        {
            var post = new HttpRequestMessage(HttpMethod.Post, url);
            post.Headers.Add("X-User", "42");
            post.Headers.Add("X-Permissions", string.Join(',', ProfitView));

            (await _client.SendAsync(post)).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, url);
        }
    }

    [Fact]
    public async Task The_ledger_filter_is_bound_from_the_query_string_and_the_downloads_ask_for_the_whole_report()
    {
        ProductLedgerReportFilter? seen = null;
        _svc.Setup(s => s.GetProductLedgerAsync(It.IsAny<ProductLedgerReportFilter>())).Callback<ProductLedgerReportFilter>(f => seen = f).ReturnsAsync(new ProductLedgerReport());

        (await Get($"{LedgerUrl}?productId={Product}&variantId={Variant}&dateFrom=2026-09-01&dateTo=2026-09-20&page=3&pageSize=10", "42", LedgerView)).StatusCode.Should().Be(HttpStatusCode.OK);
        (seen!.ProductId, seen.VariantId, seen.DateFrom, seen.DateTo, seen.Page, seen.PageSize)
            .Should().Be(((Guid?)Product, (Guid?)Variant, (DateTime?)new DateTime(2026, 9, 1), (DateTime?)new DateTime(2026, 9, 20), 3, 10));

        ProductLedgerReportFilter? exported = null;
        _svc.Setup(s => s.GetProductLedgerForExportAsync(It.IsAny<ProductLedgerReportFilter>())).Callback<ProductLedgerReportFilter>(f => exported = f).ReturnsAsync(new ProductLedgerReport());
        await Get($"{LedgerUrl}/excel?productId={Product}&dateFrom=2026-09-01", "42", LedgerExport);
        (exported!.ProductId, exported.VariantId, exported.DateFrom).Should().Be(((Guid?)Product, (Guid?)null, (DateTime?)new DateTime(2026, 9, 1)));
        _svc.Verify(s => s.GetProductLedgerAsync(It.IsAny<ProductLedgerReportFilter>()), Times.Once, "only the first request paged");
    }

    [Fact]
    public async Task The_profitability_filter_is_bound_from_the_query_string_and_the_downloads_ask_for_the_whole_report()
    {
        ProfitabilityReportFilter? seen = null;
        _svc.Setup(s => s.GetProductProfitabilityAsync(It.IsAny<ProfitabilityReportFilter>())).Callback<ProfitabilityReportFilter>(f => seen = f).ReturnsAsync(new ProfitabilityReport());

        await Get($"{ProfitUrl}?dateFrom=2026-09-01&dateTo=2026-09-20&page=2&pageSize=5", "42", ProfitView);
        (seen!.DateFrom, seen.DateTo, seen.Page, seen.PageSize).Should().Be(((DateTime?)new DateTime(2026, 9, 1), (DateTime?)new DateTime(2026, 9, 20), 2, 5));

        ProfitabilityReportFilter? exported = null;
        _svc.Setup(s => s.GetProductProfitabilityForExportAsync(It.IsAny<ProfitabilityReportFilter>())).Callback<ProfitabilityReportFilter>(f => exported = f).ReturnsAsync(new ProfitabilityReport());
        await Get($"{ProfitUrl}/pdf?dateTo=2026-09-20", "42", ProfitExport);
        exported!.DateTo.Should().Be(new DateTime(2026, 9, 20));
    }

    [Fact]
    public async Task With_no_query_string_both_reports_are_unfiltered_and_the_first_twenty()
    {
        ProductLedgerReportFilter? ledger = null;
        ProfitabilityReportFilter? profit = null;
        _svc.Setup(s => s.GetProductLedgerAsync(It.IsAny<ProductLedgerReportFilter>())).Callback<ProductLedgerReportFilter>(f => ledger = f).ReturnsAsync(new ProductLedgerReport());
        _svc.Setup(s => s.GetProductProfitabilityAsync(It.IsAny<ProfitabilityReportFilter>())).Callback<ProfitabilityReportFilter>(f => profit = f).ReturnsAsync(new ProfitabilityReport());

        await Get(LedgerUrl, "42", LedgerView);
        await Get(ProfitUrl, "42", ProfitView);

        (ledger!.ProductId, ledger.VariantId, ledger.DateFrom, ledger.DateTo, ledger.Page, ledger.PageSize).Should().Be(((Guid?)null, (Guid?)null, (DateTime?)null, (DateTime?)null, 1, 20));
        (profit!.DateFrom, profit.DateTo, profit.Page, profit.PageSize).Should().Be(((DateTime?)null, (DateTime?)null, 1, 20));
    }

    [Theory]
    [InlineData("/api/reports/sales/product-ledger?productId=not-a-guid")]
    [InlineData("/api/reports/sales/product-ledger?variantId=nope")]
    [InlineData("/api/reports/sales/product-ledger?dateFrom=yesterday")]
    [InlineData("/api/reports/sales/product-profitability?dateTo=soon")]
    [InlineData("/api/reports/sales/product-profitability?page=first")]
    public async Task A_query_value_that_does_not_parse_is_a_bad_request_and_never_reaches_the_service(string url)
    {
        var response = await Get(url, "42", ProfitView);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _svc.Invocations.Should().BeEmpty();
    }

    // ── The wire ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_product_ledger_comes_back_in_the_usual_envelope_with_the_summary_the_movements_and_paging()
    {
        using var json = JsonDocument.Parse(await (await Get(LedgerUrl, "42", LedgerView)).Content.ReadAsStringAsync());
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        var result = json.RootElement.GetProperty("result");

        result.GetProperty("companyName").GetString().Should().Be("Northwind Trading");
        result.GetProperty("criteria").GetProperty("productName").GetString().Should().Be("Laptop");
        var summary = result.GetProperty("summary");
        (summary.GetProperty("openingQuantity").GetDecimal(), summary.GetProperty("closingValue").GetDecimal(), summary.GetProperty("movementCount").GetInt32()).Should().Be((20m, 1200m, 1));
        (result.GetProperty("totalRecords").GetInt32(), result.GetProperty("totalPages").GetInt32()).Should().Be((1, 1));
        var row = result.GetProperty("items")[0];
        (row.GetProperty("sku").GetString(), row.GetProperty("entryType").GetString(), row.GetProperty("direction").GetString(), row.GetProperty("partnerName").GetString(),
         row.GetProperty("quantity").GetDecimal(), row.GetProperty("runningQty").GetDecimal(), row.GetProperty("runningValue").GetDecimal(),
         row.GetProperty("variantRunningQty").GetDecimal(), row.GetProperty("weightedAverageCost").GetDecimal())
            .Should().Be(("LAP-BLK", "PURCHASE", "IN", "Globex Corp", 100m, 120m, 1200m, 100m, 10m));
    }

    [Fact]
    public async Task Product_profitability_comes_back_ranked_with_totals_per_currency()
    {
        using var json = JsonDocument.Parse(await (await Get(ProfitUrl, "42", ProfitView)).Content.ReadAsStringAsync());
        var result = json.RootElement.GetProperty("result");

        var row = result.GetProperty("items")[0];
        (row.GetProperty("rank").GetInt32(), row.GetProperty("productName").GetString(), row.GetProperty("currencyCode").GetString(), row.GetProperty("revenue").GetDecimal(),
         row.GetProperty("costOfGoodsSold").GetDecimal(), row.GetProperty("grossProfit").GetDecimal(), row.GetProperty("marginPercent").GetDecimal())
            .Should().Be((1, "Laptop", "PKR", 5300m, 3200m, 2100m, 39.62m));
        var total = result.GetProperty("totals")[0];
        (total.GetProperty("productCount").GetInt32(), total.GetProperty("grossProfit").GetDecimal()).Should().Be((1, 2100m));
    }

    [Theory]
    [InlineData(LedgerUrl, "product-ledger", true)]
    [InlineData(LedgerUrl, "product-ledger", false)]
    [InlineData(ProfitUrl, "product-profitability", true)]
    [InlineData(ProfitUrl, "product-profitability", false)]
    public async Task The_downloads_are_attachments_of_the_right_type_named_by_their_date(string url, string name, bool pdf)
    {
        var response = await Get($"{url}/{(pdf ? "pdf" : "excel")}", "42", url == ProfitUrl ? ProfitExport : LedgerExport);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(pdf ? "application/pdf" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        response.Content.Headers.ContentDisposition!.FileName.Should().Be($"{name}-20260920.{(pdf ? "pdf" : "xlsx")}");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (pdf) Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
        else (bytes[0], bytes[1]).Should().Be(((byte)'P', (byte)'K'));
    }
}
