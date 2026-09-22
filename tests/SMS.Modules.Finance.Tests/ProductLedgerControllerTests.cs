using System.Net;
using System.Reflection;
using System.Security.Claims;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SMS.Modules.Finance.Controllers;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P8-05 §11.4 — the product ledger endpoints: that each is gated by the one permission that opens
/// cost and margin, lives at the route the spec names, and hands the service what the request said.
/// </summary>
public class ProductLedgerControllerTests
{
    private static readonly Type[] Controllers = [typeof(ProductLedgerController), typeof(ProductProfitabilityController)];

    private static IEnumerable<MethodInfo> ActionsOf(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                  .Where(m => !m.IsSpecialName && m.GetCustomAttributes<HttpMethodAttribute>().Any());

    private static IEnumerable<(string Verb, string Route)> RoutesOf(Type controller)
    {
        var prefix = controller.GetCustomAttribute<RouteAttribute>()!.Template!;

        foreach (var action in ActionsOf(controller))
            foreach (var http in action.GetCustomAttributes<HttpMethodAttribute>())
                yield return (http.HttpMethods.Single(), http.Template is null ? prefix : $"{prefix}/{http.Template}");
    }

    // ── Attributes ───────────────────────────────────────────────────────────

    [Fact]
    public void Both_controllers_and_all_three_actions_are_found()
    {
        Controllers.SelectMany(ActionsOf).Should().HaveCount(3, "guards the reflection: a query that matches nothing passes everything below");
    }

    [Fact]
    public void Every_action_needs_the_product_ledger_permission_and_none_is_anonymous()
    {
        foreach (var controller in Controllers)
        {
            controller.GetCustomAttribute<ApiControllerAttribute>().Should().NotBeNull(controller.Name);
            controller.GetCustomAttribute<RequiresFeatureAttribute>()!.FeatureCode.Should().Be("MODULE_FINANCE", controller.Name);
            controller.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();

            foreach (var action in ActionsOf(controller))
            {
                action.GetCustomAttribute<RequirePermissionAttribute>()!.Policy.Should().Be("Permission:PRODUCT_LEDGER_VIEW", $"{controller.Name}.{action.Name}");
                action.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
            }
        }
    }

    [Fact]
    public void Cost_and_margin_are_not_opened_by_the_stock_or_generic_report_permissions()
    {
        var used = Controllers.SelectMany(ActionsOf).Select(a => a.GetCustomAttribute<RequirePermissionAttribute>()!.Policy).Distinct().ToList();

        used.Should().NotContain(["Permission:INVENTORY_VIEW", "Permission:REPORT_VIEW", "Permission:CUSTOMER_LEDGER_VIEW"]);
    }

    [Fact]
    public void The_routes_are_the_ones_section_11_4_names()
    {
        Controllers.SelectMany(RoutesOf).Select(r => $"{r.Verb} {r.Route}").Should().BeEquivalentTo(
        [
            "GET api/product-ledger/{variantId:guid}",
            "GET api/product-ledger/{variantId:guid}/summary",
            "GET api/reports/product-profitability",
        ]);
    }

    [Fact]
    public void They_take_the_variant_by_uuid_because_every_cross_module_reference_here_is_one()
    {
        typeof(ProductLedgerController).GetMethod(nameof(ProductLedgerController.GetLedger))!.GetParameters()[0].ParameterType.Should().Be(typeof(Guid));
        typeof(ProductLedgerController).GetMethod(nameof(ProductLedgerController.GetSummary))!.GetParameters()[0].ParameterType.Should().Be(typeof(Guid));
    }

    // ── What each action hands the service ───────────────────────────────────

    private static T Body<T>(IActionResult result) where T : class =>
        ((ApiResponse<T>)((OkObjectResult)result).Value!).Result!;

    [Fact]
    public async Task The_ledger_endpoint_asks_for_the_variant_in_the_route_with_the_filter_from_the_query()
    {
        var query = new Mock<IProductLedgerQueryService>();
        var variant = Guid.NewGuid();
        var filter = new ProductLedgerFilter { EntryType = "SALE", Direction = "OUT", DateFrom = new DateTime(2026, 9, 1), Page = 2, PageSize = 10 };
        var page = new PaginatedResponse<ProductLedgerEntryModel> { TotalRecords = 12 };
        query.Setup(s => s.GetLedgerAsync(variant, filter)).ReturnsAsync(page);

        Body<PaginatedResponse<ProductLedgerEntryModel>>(await new ProductLedgerController(query.Object).GetLedger(variant, filter)).Should().BeSameAs(page);
    }

    [Fact]
    public async Task The_summary_endpoint_asks_for_the_variant_in_the_route()
    {
        var query = new Mock<IProductLedgerQueryService>();
        var variant = Guid.NewGuid();
        var summary = new ProductLedgerSummaryModel { VariantUuid = variant, StockValue = 700m };
        query.Setup(s => s.GetSummaryAsync(variant)).ReturnsAsync(summary);

        Body<ProductLedgerSummaryModel>(await new ProductLedgerController(query.Object).GetSummary(variant)).Should().BeSameAs(summary);
    }

    [Fact]
    public async Task The_profitability_endpoint_passes_its_filter_through_untouched()
    {
        var query = new Mock<IProductLedgerQueryService>();
        var filter = new ProductProfitabilityFilter { DateFrom = new DateTime(2026, 9, 1), Page = 3 };
        var page = new PaginatedResponse<ProductProfitabilityItemModel> { TotalRecords = 7 };
        query.Setup(s => s.GetProfitabilityAsync(filter)).ReturnsAsync(page);

        Body<PaginatedResponse<ProductProfitabilityItemModel>>(await new ProductProfitabilityController(query.Object).GetProductProfitability(filter)).Should().BeSameAs(page);
    }
}

/// <summary>
/// The same three endpoints through a real ASP.NET Core pipeline, as <see cref="ReceivablesHttpTests"/>
/// does for the receivables: the route, the query-string binding, the permission gate refusing a caller
/// who lacks it, and the wire shape a browser would parse. The service behind them is a mock.
/// </summary>
public class ProductLedgerHttpTests : IAsyncLifetime
{
    private static readonly Guid Variant = Guid.NewGuid();

    private readonly Mock<IProductLedgerQueryService> _query = new();
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _query.Setup(s => s.GetLedgerAsync(It.IsAny<Guid>(), It.IsAny<ProductLedgerFilter>())).ReturnsAsync(new PaginatedResponse<ProductLedgerEntryModel>());
        _query.Setup(s => s.GetSummaryAsync(It.IsAny<Guid>())).ReturnsAsync(new ProductLedgerSummaryModel
        {
            VariantUuid = Variant, PurchasedQuantity = 100m, PurchasedCost = 1000m, SoldQuantity = 30m, CostOfGoodsSold = 300m,
            CurrentQuantity = 70m, StockValue = 700m, WeightedAverageCost = 10m, EntryCount = 2
        });
        _query.Setup(s => s.GetProfitabilityAsync(It.IsAny<ProductProfitabilityFilter>())).ReturnsAsync(new PaginatedResponse<ProductProfitabilityItemModel>
        {
            Data = [new ProductProfitabilityItemModel
            {
                ProductUuid = Variant, ProductName = "Laptop", CurrencyCode = "PKR", QuantitySold = 30m, Revenue = 450m,
                CostOfGoodsSold = 300m, GrossProfit = 150m, MarginPercent = 33.33m
            }],
            TotalRecords = 1, Page = 1, PageSize = 20, TotalPages = 1
        });

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
                }).ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyTheProductLedgerControllers()));

                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Test", _ => { });
                services.AddAuthorization();
                services.AddSingleton<IAuthorizationPolicyProvider, PermissionClaimPolicyProvider>();
                services.AddSingleton(_query.Object);
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

    private sealed class OnlyTheProductLedgerControllers : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            feature.Controllers.Add(typeof(ProductLedgerController).GetTypeInfo());
            feature.Controllers.Add(typeof(ProductProfitabilityController).GetTypeInfo());
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

    private Task<HttpResponseMessage> Get(string url, string? user = "42", params string[] permissions)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (user is not null) request.Headers.Add("X-User", user);
        request.Headers.Add("X-Permissions", string.Join(',', permissions.Length == 0 ? [PermissionCodes.PRODUCT_LEDGER_VIEW] : permissions));
        return _client.SendAsync(request);
    }

    public static IEnumerable<object[]> Urls() =>
    [
        [$"/api/product-ledger/{Variant}"],
        [$"/api/product-ledger/{Variant}/summary"],
        ["/api/reports/product-profitability"]
    ];

    [Theory]
    [MemberData(nameof(Urls))]
    public async Task Nobody_who_has_not_logged_in_gets_anything(string url) =>
        (await Get(url, user: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Theory]
    [MemberData(nameof(Urls))]
    public async Task A_caller_holding_every_neighbouring_permission_but_not_this_one_is_refused(string url)
    {
        var response = await Get(url, "42",
            PermissionCodes.INVENTORY_VIEW, PermissionCodes.REPORT_VIEW, PermissionCodes.REPORT_EXPORT, PermissionCodes.CUSTOMER_LEDGER_VIEW,
            PermissionCodes.SALES_INVOICE_VIEW, PermissionCodes.INVOICE_VIEW, PermissionCodes.STOCK_MANAGE);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [MemberData(nameof(Urls))]
    public async Task The_permission_the_endpoints_name_is_enough_to_reach_them(string url) =>
        (await Get(url)).StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task An_id_that_is_not_a_guid_does_not_match_a_route_and_only_get_is_answered()
    {
        (await Get("/api/product-ledger/not-a-guid")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var post = new HttpRequestMessage(HttpMethod.Post, $"/api/product-ledger/{Variant}");
        post.Headers.Add("X-User", "42");
        post.Headers.Add("X-Permissions", PermissionCodes.PRODUCT_LEDGER_VIEW);
        (await _client.SendAsync(post)).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, "the ledger has no write endpoint: nothing but the business actions writes to it");
    }

    [Fact]
    public async Task The_ledger_binds_the_variant_from_the_route_and_every_filter_from_the_query_string()
    {
        Guid seenVariant = Guid.Empty;
        ProductLedgerFilter? seen = null;
        _query.Setup(s => s.GetLedgerAsync(It.IsAny<Guid>(), It.IsAny<ProductLedgerFilter>()))
              .Callback<Guid, ProductLedgerFilter>((v, f) => { seenVariant = v; seen = f; })
              .ReturnsAsync(new PaginatedResponse<ProductLedgerEntryModel>());

        var response = await Get($"/api/product-ledger/{Variant}?dateFrom=2026-09-01&dateTo=2026-09-20&entryType=sale&direction=OUT&page=3&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        seenVariant.Should().Be(Variant);
        (seen!.DateFrom, seen.DateTo, seen.EntryType, seen.Direction, seen.Page, seen.PageSize)
            .Should().Be(((DateTime?)new DateTime(2026, 9, 1), (DateTime?)new DateTime(2026, 9, 20), "sale", "OUT", 3, 10));
    }

    [Fact]
    public async Task With_no_query_string_paging_is_the_first_twenty()
    {
        ProductLedgerFilter? seen = null;
        _query.Setup(s => s.GetLedgerAsync(It.IsAny<Guid>(), It.IsAny<ProductLedgerFilter>()))
              .Callback<Guid, ProductLedgerFilter>((_, f) => seen = f).ReturnsAsync(new PaginatedResponse<ProductLedgerEntryModel>());

        await Get($"/api/product-ledger/{Variant}");

        (seen!.Page, seen.PageSize, seen.EntryType, seen.DateFrom).Should().Be((1, 20, (string?)null, (DateTime?)null));
    }

    [Fact]
    public async Task The_profitability_report_binds_its_period_and_paging()
    {
        ProductProfitabilityFilter? seen = null;
        _query.Setup(s => s.GetProfitabilityAsync(It.IsAny<ProductProfitabilityFilter>()))
              .Callback<ProductProfitabilityFilter>(f => seen = f).ReturnsAsync(new PaginatedResponse<ProductProfitabilityItemModel>());

        await Get("/api/reports/product-profitability?dateFrom=2026-09-01&dateTo=2026-09-30&page=2&pageSize=5");

        (seen!.DateFrom, seen.DateTo, seen.Page, seen.PageSize).Should().Be(((DateTime?)new DateTime(2026, 9, 1), (DateTime?)new DateTime(2026, 9, 30), 2, 5));
    }

    [Fact]
    public async Task The_summary_comes_back_in_the_usual_envelope_with_the_figures_tc10_asks_for()
    {
        var response = await Get($"/api/product-ledger/{Variant}/summary");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        var result = json.RootElement.GetProperty("result");
        result.GetProperty("variantUuid").GetGuid().Should().Be(Variant);
        result.GetProperty("purchasedQuantity").GetDecimal().Should().Be(100m);
        result.GetProperty("soldQuantity").GetDecimal().Should().Be(30m);
        result.GetProperty("costOfGoodsSold").GetDecimal().Should().Be(300m);
        result.GetProperty("stockValue").GetDecimal().Should().Be(700m);
        result.GetProperty("weightedAverageCost").GetDecimal().Should().Be(10m);
    }

    [Fact]
    public async Task The_profitability_report_comes_back_paged_with_revenue_cost_profit_and_margin_per_product()
    {
        var response = await Get("/api/reports/product-profitability");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = json.RootElement.GetProperty("result");
        result.GetProperty("totalRecords").GetInt32().Should().Be(1);
        var row = result.GetProperty("data")[0];
        row.GetProperty("productName").GetString().Should().Be("Laptop");
        row.GetProperty("currencyCode").GetString().Should().Be("PKR");
        (row.GetProperty("revenue").GetDecimal(), row.GetProperty("costOfGoodsSold").GetDecimal(), row.GetProperty("grossProfit").GetDecimal(), row.GetProperty("marginPercent").GetDecimal())
            .Should().Be((450m, 300m, 150m, 33.33m));
    }
}
