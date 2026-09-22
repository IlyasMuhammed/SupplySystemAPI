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
/// A29-P9-02 / P9-03 §15 R2 and R3 — the receivables report endpoints: that each lives at the route the
/// spec names, is gated by both the reports permission and the one that opens the same books elsewhere,
/// and hands the service what the request said.
/// </summary>
public class ReceivablesReportsControllerTests
{
    private static readonly Type Controller = typeof(ReceivablesReportsController);

    private static IEnumerable<MethodInfo> Actions() =>
        Controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetCustomAttributes<HttpMethodAttribute>().Any());

    private static string[] Policies(string action) =>
        [.. Controller.GetMethod(action)!.GetCustomAttributes<RequirePermissionAttribute>().Select(a => a.Policy!).Order()];

    // ── Attributes ───────────────────────────────────────────────────────────

    [Fact]
    public void The_six_actions_are_found()
    {
        Actions().Should().HaveCount(6, "guards the reflection: a query that matches nothing passes everything below");
    }

    [Fact]
    public void The_controller_is_an_api_controller_behind_the_reports_feature_and_not_anonymous()
    {
        Controller.GetCustomAttribute<ApiControllerAttribute>().Should().NotBeNull();
        Controller.GetCustomAttribute<RequiresFeatureAttribute>()!.FeatureCode.Should().Be("MODULE_REPORTS");
        Controller.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
        Actions().Should().OnlyContain(a => a.GetCustomAttribute<AllowAnonymousAttribute>() == null);
    }

    [Fact]
    public void The_routes_sit_beside_the_sales_order_register_under_the_same_prefix()
    {
        var prefix = Controller.GetCustomAttribute<RouteAttribute>()!.Template;
        prefix.Should().Be(typeof(SalesReportsController).GetCustomAttribute<RouteAttribute>()!.Template);

        Actions().SelectMany(a => a.GetCustomAttributes<HttpMethodAttribute>().Select(h => $"{h.HttpMethods.Single()} {prefix}/{h.Template}"))
                 .Should().BeEquivalentTo(
                 [
                     "GET api/reports/sales/customer-ledger",
                     "GET api/reports/sales/customer-ledger/pdf",
                     "GET api/reports/sales/customer-ledger/excel",
                     "GET api/reports/sales/aging-receivables",
                     "GET api/reports/sales/aging-receivables/pdf",
                     "GET api/reports/sales/aging-receivables/excel",
                 ]);
    }

    [Fact]
    public void No_route_here_is_one_the_sales_controller_already_answers()
    {
        string[] Templates(Type t) => [.. t.GetMethods().SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>()).Select(h => h.Template!)];

        Templates(Controller).Intersect(Templates(typeof(SalesReportsController))).Should().BeEmpty();
    }

    [Fact]
    public void The_ledger_report_is_opened_by_the_reports_permission_together_with_the_customer_ledger_permission()
    {
        Policies(nameof(ReceivablesReportsController.GetCustomerLedger)).Should().Equal("Permission:CUSTOMER_LEDGER_VIEW", "Permission:REPORT_VIEW");
    }

    [Theory]
    [InlineData(nameof(ReceivablesReportsController.ExportCustomerLedgerPdf))]
    [InlineData(nameof(ReceivablesReportsController.ExportCustomerLedgerExcel))]
    public void The_ledger_downloads_need_the_export_permission_together_with_the_customer_ledger_permission(string action)
    {
        Policies(action).Should().Equal("Permission:CUSTOMER_LEDGER_VIEW", "Permission:REPORT_EXPORT");
    }

    [Fact]
    public void The_aging_report_is_opened_by_the_reports_permission_together_with_the_sales_invoice_permission()
    {
        Policies(nameof(ReceivablesReportsController.GetAgingReceivables)).Should().Equal("Permission:REPORT_VIEW", "Permission:SALES_INVOICE_VIEW");
    }

    [Theory]
    [InlineData(nameof(ReceivablesReportsController.ExportAgingReceivablesPdf))]
    [InlineData(nameof(ReceivablesReportsController.ExportAgingReceivablesExcel))]
    public void The_aging_downloads_need_the_export_permission_together_with_the_sales_invoice_permission(string action)
    {
        Policies(action).Should().Equal("Permission:REPORT_EXPORT", "Permission:SALES_INVOICE_VIEW");
    }

    // ── What each action does ────────────────────────────────────────────────

    private static T Body<T>(IActionResult result) where T : class =>
        ((ApiResponse<T>)((OkObjectResult)result).Value!).Result!;

    private static CustomerLedgerReport LedgerSample() => new()
    {
        CompanyName = "Northwind Trading", GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0),
        Criteria = new CustomerLedgerReportCriteria { CustomerName = "Acme Ltd" }, TotalRecords = 0, Page = 1, PageSize = 1
    };

    private static AgingReceivablesReport AgingSample() => new()
    {
        CompanyName = "Northwind Trading", GeneratedAt = new DateTime(2026, 9, 21, 8, 0, 0),
        Criteria = new AgingReceivablesCriteria { AsOf = new DateTime(2026, 9, 20) }, TotalRecords = 0, Page = 1, PageSize = 1
    };

    [Fact]
    public async Task The_ledger_endpoint_hands_the_filter_to_the_paged_query_and_wraps_the_report()
    {
        var svc    = new Mock<IReceivablesReportService>();
        var filter = new CustomerLedgerReportFilter { PartnerId = Guid.NewGuid(), Page = 2, PageSize = 5 };
        var report = LedgerSample();
        svc.Setup(s => s.GetCustomerLedgerAsync(filter)).ReturnsAsync(report);

        var result = await new ReceivablesReportsController(svc.Object).GetCustomerLedger(filter);

        Body<CustomerLedgerReport>(result).Should().BeSameAs(report);
        svc.Verify(s => s.GetCustomerLedgerForExportAsync(It.IsAny<CustomerLedgerReportFilter>()), Times.Never);
    }

    [Fact]
    public async Task The_ledger_pdf_prints_the_whole_account_not_the_page_and_names_the_file_by_its_date()
    {
        var svc    = new Mock<IReceivablesReportService>();
        var filter = new CustomerLedgerReportFilter { PartnerId = Guid.NewGuid(), Page = 4, PageSize = 2 };
        svc.Setup(s => s.GetCustomerLedgerForExportAsync(filter)).ReturnsAsync(LedgerSample());

        var file = (FileContentResult)await new ReceivablesReportsController(svc.Object).ExportCustomerLedgerPdf(filter);

        (file.ContentType, file.FileDownloadName).Should().Be(("application/pdf", "customer-ledger-20260920.pdf"));
        Encoding.ASCII.GetString(file.FileContents, 0, 5).Should().Be("%PDF-");
        svc.Verify(s => s.GetCustomerLedgerAsync(It.IsAny<CustomerLedgerReportFilter>()), Times.Never);
    }

    [Fact]
    public async Task The_ledger_workbook_lists_the_whole_account_not_the_page_and_names_the_file_by_its_date()
    {
        var svc    = new Mock<IReceivablesReportService>();
        var filter = new CustomerLedgerReportFilter { PartnerId = Guid.NewGuid(), Page = 4, PageSize = 2 };
        svc.Setup(s => s.GetCustomerLedgerForExportAsync(filter)).ReturnsAsync(LedgerSample());

        var file = (FileContentResult)await new ReceivablesReportsController(svc.Object).ExportCustomerLedgerExcel(filter);

        (file.ContentType, file.FileDownloadName).Should().Be(("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "customer-ledger-20260920.xlsx"));
        (file.FileContents[0], file.FileContents[1]).Should().Be(((byte)'P', (byte)'K'));
        svc.Verify(s => s.GetCustomerLedgerAsync(It.IsAny<CustomerLedgerReportFilter>()), Times.Never);
    }

    [Fact]
    public async Task The_aging_endpoint_hands_the_filter_to_the_paged_query_and_wraps_the_report()
    {
        var svc    = new Mock<IReceivablesReportService>();
        var filter = new AgingReceivablesFilter { AsOf = new DateTime(2026, 9, 20), Page = 2, PageSize = 5 };
        var report = AgingSample();
        svc.Setup(s => s.GetAgingReceivablesAsync(filter)).ReturnsAsync(report);

        var result = await new ReceivablesReportsController(svc.Object).GetAgingReceivables(filter);

        Body<AgingReceivablesReport>(result).Should().BeSameAs(report);
        svc.Verify(s => s.GetAgingReceivablesForExportAsync(It.IsAny<AgingReceivablesFilter>()), Times.Never);
    }

    [Fact]
    public async Task The_aging_pdf_prints_every_invoice_and_names_the_file_by_the_day_it_is_as_of_not_the_day_it_was_made()
    {
        var svc    = new Mock<IReceivablesReportService>();
        var filter = new AgingReceivablesFilter { Page = 4, PageSize = 2 };
        svc.Setup(s => s.GetAgingReceivablesForExportAsync(filter)).ReturnsAsync(AgingSample());

        var file = (FileContentResult)await new ReceivablesReportsController(svc.Object).ExportAgingReceivablesPdf(filter);

        (file.ContentType, file.FileDownloadName).Should().Be(("application/pdf", "aging-receivables-20260920.pdf"));
        Encoding.ASCII.GetString(file.FileContents, 0, 5).Should().Be("%PDF-");
        svc.Verify(s => s.GetAgingReceivablesAsync(It.IsAny<AgingReceivablesFilter>()), Times.Never);
    }

    [Fact]
    public async Task The_aging_workbook_lists_every_invoice_and_names_the_file_by_the_day_it_is_as_of()
    {
        var svc    = new Mock<IReceivablesReportService>();
        var filter = new AgingReceivablesFilter { Page = 4, PageSize = 2 };
        svc.Setup(s => s.GetAgingReceivablesForExportAsync(filter)).ReturnsAsync(AgingSample());

        var file = (FileContentResult)await new ReceivablesReportsController(svc.Object).ExportAgingReceivablesExcel(filter);

        (file.ContentType, file.FileDownloadName).Should().Be(("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "aging-receivables-20260920.xlsx"));
        (file.FileContents[0], file.FileContents[1]).Should().Be(((byte)'P', (byte)'K'));
        svc.Verify(s => s.GetAgingReceivablesAsync(It.IsAny<AgingReceivablesFilter>()), Times.Never);
    }
}

/// <summary>
/// That the module registers the services its controllers take, so the endpoints do not answer 500 at
/// runtime for want of one line in <c>AddReportsModule</c>. What the services need from other modules is
/// stood in for; what is under test is that the Reports registration is enough.
/// </summary>
public class ReceivablesReportsRegistrationTests
{
    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddReportsModule(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=unused;Database=unused" }).Build());

        services.AddSingleton<ITenantContext>(new StaticTenantContext());
        services.AddDbContext<FinanceDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton(new Mock<ISupplierNameLookupService>().Object);
        services.AddSingleton(new Mock<IPoDocumentTemplateService>().Object);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
    }

    [Fact]
    public void The_service_is_registered_per_request_and_the_controller_can_be_built_from_it()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<IReceivablesReportService>();

        service.Should().BeOfType<ReceivablesReportService>();
        scope.ServiceProvider.GetRequiredService<IReceivablesReportService>().Should().BeSameAs(service, "one instance per request");
        using var other = provider.CreateScope();
        other.ServiceProvider.GetRequiredService<IReceivablesReportService>().Should().NotBeSameAs(service);
        ActivatorUtilities.CreateInstance<ReceivablesReportsController>(scope.ServiceProvider).Should().NotBeNull();
    }
}

/// <summary>
/// The same endpoints through a real ASP.NET Core pipeline: the routes, the query-string binding, the two
/// permission gates each refusing a caller who holds only the other, and the wire shape a browser would parse.
/// The service behind them is a mock; the feature and exception middleware live in SMS.API and are not here.
/// </summary>
public class ReceivablesReportsHttpTests : IAsyncLifetime
{
    private readonly Mock<IReceivablesReportService> _svc = new();
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var ledger = new CustomerLedgerReport
        {
            CompanyName = "Northwind Trading", GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0),
            Criteria = new CustomerLedgerReportCriteria { PartnerId = Guid.NewGuid(), CustomerName = "Acme Ltd", DateFrom = new DateTime(2026, 9, 1) },
            Summaries = [new CustomerLedgerReportSummary { CurrencyCode = "PKR", OpeningBalance = 250m, TotalDebit = 1500m, TotalCredit = 300m, ClosingBalance = 1450m, EntryCount = 2 }],
            Entries =
            [
                new CustomerLedgerReportEntry
                {
                    SequenceNo = 1, EntryDate = new DateTime(2026, 9, 1), EntryType = "INVOICE", ReferenceType = "SalesInvoice", ReferenceNumber = "SINV-1",
                    CurrencyCode = "PKR", DebitAmount = 1500m, Balance = 1750m
                }
            ],
            TotalRecords = 2, Page = 1, PageSize = 1, TotalPages = 2
        };

        var aging = new AgingReceivablesReport
        {
            CompanyName = "Northwind Trading", GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0),
            Criteria = new AgingReceivablesCriteria { AsOf = new DateTime(2026, 9, 20) },
            Totals = [new AgingReceivablesTotal { CurrencyCode = "PKR", InvoiceCount = 1, Days31To60 = 200m, Total = 200m }],
            Customers = [new AgingReceivablesCustomer { CustomerName = "Acme Ltd", CurrencyCode = "PKR", InvoiceCount = 1, Days31To60 = 200m, Total = 200m }],
            Invoices =
            [
                new AgingReceivablesInvoice
                {
                    InvoiceNumber = "SINV-1", CustomerName = "Acme Ltd", CurrencyCode = "PKR", DaysPastDue = 40, Bucket = "31-60",
                    GrandTotal = 250m, AmountPaid = 50m, Outstanding = 200m
                }
            ],
            TotalRecords = 1, Page = 1, PageSize = 20, TotalPages = 1
        };

        _svc.Setup(s => s.GetCustomerLedgerAsync(It.IsAny<CustomerLedgerReportFilter>())).ReturnsAsync(ledger);
        _svc.Setup(s => s.GetCustomerLedgerForExportAsync(It.IsAny<CustomerLedgerReportFilter>())).ReturnsAsync(ledger);
        _svc.Setup(s => s.GetAgingReceivablesAsync(It.IsAny<AgingReceivablesFilter>())).ReturnsAsync(aging);
        _svc.Setup(s => s.GetAgingReceivablesForExportAsync(It.IsAny<AgingReceivablesFilter>())).ReturnsAsync(aging);

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
                }).ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyTheReceivablesReportsController()));

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

    private sealed class OnlyTheReceivablesReportsController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            feature.Controllers.Add(typeof(ReceivablesReportsController).GetTypeInfo());
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

    private static readonly Guid Customer = Guid.NewGuid();

    private static readonly string LedgerUrl = $"/api/reports/sales/customer-ledger?partnerId={Customer}";
    private static readonly string LedgerPdf = $"/api/reports/sales/customer-ledger/pdf?partnerId={Customer}";
    private static readonly string LedgerXls = $"/api/reports/sales/customer-ledger/excel?partnerId={Customer}";
    private const string AgingUrl = "/api/reports/sales/aging-receivables";
    private const string AgingPdf = "/api/reports/sales/aging-receivables/pdf";
    private const string AgingXls = "/api/reports/sales/aging-receivables/excel";

    private static readonly string[] LedgerView   = [PermissionCodes.REPORT_VIEW, PermissionCodes.CUSTOMER_LEDGER_VIEW];
    private static readonly string[] LedgerExport = [PermissionCodes.REPORT_EXPORT, PermissionCodes.CUSTOMER_LEDGER_VIEW];
    private static readonly string[] AgingView    = [PermissionCodes.REPORT_VIEW, PermissionCodes.SALES_INVOICE_VIEW];
    private static readonly string[] AgingExport  = [PermissionCodes.REPORT_EXPORT, PermissionCodes.SALES_INVOICE_VIEW];

    public static IEnumerable<object[]> Endpoints() =>
    [
        [LedgerUrl, string.Join(',', LedgerView)],
        [LedgerPdf, string.Join(',', LedgerExport)],
        [LedgerXls, string.Join(',', LedgerExport)],
        [AgingUrl, string.Join(',', AgingView)],
        [AgingPdf, string.Join(',', AgingExport)],
        [AgingXls, string.Join(',', AgingExport)],
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
    [InlineData(true)]
    [InlineData(false)]
    public async Task Being_allowed_to_view_reports_is_not_being_allowed_to_download_them_and_the_other_way_round(bool ledger)
    {
        var owner = ledger ? PermissionCodes.CUSTOMER_LEDGER_VIEW : PermissionCodes.SALES_INVOICE_VIEW;

        foreach (var url in ledger ? new[] { LedgerPdf, LedgerXls } : new[] { AgingPdf, AgingXls })
            (await Get(url, "42", PermissionCodes.REPORT_VIEW, owner)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);

        (await Get(ledger ? LedgerUrl : AgingUrl, "42", PermissionCodes.REPORT_EXPORT, owner)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_ledger_permission_does_not_open_the_aging_report_nor_the_invoice_permission_the_ledger()
    {
        (await Get(AgingUrl, "42", PermissionCodes.REPORT_VIEW, PermissionCodes.CUSTOMER_LEDGER_VIEW)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Get(LedgerUrl, "42", PermissionCodes.REPORT_VIEW, PermissionCodes.SALES_INVOICE_VIEW)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_sale_order_permission_that_gates_the_order_register_opens_neither_of_these()
    {
        foreach (var url in new[] { LedgerUrl, AgingUrl })
            (await Get(url, "42", PermissionCodes.REPORT_VIEW, PermissionCodes.SALE_ORDER_VIEW)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
    }

    // ── Routing and binding ──────────────────────────────────────────────────

    [Fact]
    public async Task Only_get_is_answered()
    {
        var post = new HttpRequestMessage(HttpMethod.Post, AgingUrl);
        post.Headers.Add("X-User", "42");
        post.Headers.Add("X-Permissions", string.Join(',', AgingView));

        (await _client.SendAsync(post)).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task The_ledger_filter_is_bound_from_the_query_string()
    {
        CustomerLedgerReportFilter? seen = null;
        _svc.Setup(s => s.GetCustomerLedgerAsync(It.IsAny<CustomerLedgerReportFilter>())).Callback<CustomerLedgerReportFilter>(f => seen = f)
            .ReturnsAsync(new CustomerLedgerReport());

        var response = await Get($"{LedgerUrl}&dateFrom=2026-09-01&dateTo=2026-09-20&page=3&pageSize=10", "42", LedgerView);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (seen!.PartnerId, seen.DateFrom, seen.DateTo, seen.Page, seen.PageSize)
            .Should().Be(((Guid?)Customer, (DateTime?)new DateTime(2026, 9, 1), (DateTime?)new DateTime(2026, 9, 20), 3, 10));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_ledger_downloads_bind_the_same_filter_and_ask_for_the_whole_account(bool pdf)
    {
        CustomerLedgerReportFilter? seen = null;
        _svc.Setup(s => s.GetCustomerLedgerForExportAsync(It.IsAny<CustomerLedgerReportFilter>())).Callback<CustomerLedgerReportFilter>(f => seen = f)
            .ReturnsAsync(new CustomerLedgerReport());

        await Get($"{(pdf ? LedgerPdf : LedgerXls)}&dateFrom=2026-09-01", "42", LedgerExport);

        (seen!.PartnerId, seen.DateFrom).Should().Be(((Guid?)Customer, (DateTime?)new DateTime(2026, 9, 1)));
        _svc.Verify(s => s.GetCustomerLedgerAsync(It.IsAny<CustomerLedgerReportFilter>()), Times.Never);
    }

    [Fact]
    public async Task The_aging_filter_is_bound_from_the_query_string()
    {
        AgingReceivablesFilter? seen = null;
        _svc.Setup(s => s.GetAgingReceivablesAsync(It.IsAny<AgingReceivablesFilter>())).Callback<AgingReceivablesFilter>(f => seen = f)
            .ReturnsAsync(new AgingReceivablesReport());

        var response = await Get($"{AgingUrl}?asOf=2026-08-31&partnerId={Customer}&page=2&pageSize=5", "42", AgingView);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (seen!.AsOf, seen.PartnerId, seen.Page, seen.PageSize).Should().Be(((DateTime?)new DateTime(2026, 8, 31), (Guid?)Customer, 2, 5));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_aging_downloads_bind_the_same_filter_and_ask_for_every_invoice(bool pdf)
    {
        AgingReceivablesFilter? seen = null;
        _svc.Setup(s => s.GetAgingReceivablesForExportAsync(It.IsAny<AgingReceivablesFilter>())).Callback<AgingReceivablesFilter>(f => seen = f)
            .ReturnsAsync(new AgingReceivablesReport());

        await Get($"{(pdf ? AgingPdf : AgingXls)}?asOf=2026-08-31&partnerId={Customer}", "42", AgingExport);

        (seen!.AsOf, seen.PartnerId).Should().Be(((DateTime?)new DateTime(2026, 8, 31), (Guid?)Customer));
        _svc.Verify(s => s.GetAgingReceivablesAsync(It.IsAny<AgingReceivablesFilter>()), Times.Never);
    }

    [Fact]
    public async Task With_no_query_string_the_aging_is_of_today_for_everyone_and_the_first_twenty_invoices()
    {
        AgingReceivablesFilter? seen = null;
        _svc.Setup(s => s.GetAgingReceivablesAsync(It.IsAny<AgingReceivablesFilter>())).Callback<AgingReceivablesFilter>(f => seen = f)
            .ReturnsAsync(new AgingReceivablesReport());

        await Get(AgingUrl, "42", AgingView);

        (seen!.AsOf, seen.PartnerId, seen.Page, seen.PageSize).Should().Be(((DateTime?)null, (Guid?)null, 1, 20));
    }

    [Theory]
    [InlineData("/api/reports/sales/aging-receivables?partnerId=not-a-guid")]
    [InlineData("/api/reports/sales/aging-receivables?asOf=yesterday")]
    [InlineData("/api/reports/sales/customer-ledger?partnerId=not-a-guid")]
    public async Task A_query_value_that_does_not_parse_is_a_bad_request_and_never_reaches_the_service(string url)
    {
        var response = await Get(url, "42", PermissionCodes.REPORT_VIEW, PermissionCodes.SALES_INVOICE_VIEW, PermissionCodes.CUSTOMER_LEDGER_VIEW);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _svc.Invocations.Should().BeEmpty();
    }

    // ── The wire ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_ledger_comes_back_in_the_usual_envelope_with_summaries_entries_and_paging()
    {
        var response = await Get(LedgerUrl, "42", LedgerView);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        var result = json.RootElement.GetProperty("result");

        result.GetProperty("companyName").GetString().Should().Be("Northwind Trading");
        result.GetProperty("criteria").GetProperty("customerName").GetString().Should().Be("Acme Ltd");
        (result.GetProperty("totalRecords").GetInt32(), result.GetProperty("totalPages").GetInt32()).Should().Be((2, 2));

        var summary = result.GetProperty("summaries")[0];
        summary.GetProperty("currencyCode").GetString().Should().Be("PKR");
        (summary.GetProperty("openingBalance").GetDecimal(), summary.GetProperty("totalDebit").GetDecimal(),
         summary.GetProperty("totalCredit").GetDecimal(), summary.GetProperty("closingBalance").GetDecimal()).Should().Be((250m, 1500m, 300m, 1450m));

        var entry = result.GetProperty("entries")[0];
        (entry.GetProperty("sequenceNo").GetInt32(), entry.GetProperty("entryType").GetString(), entry.GetProperty("referenceNumber").GetString(),
         entry.GetProperty("debitAmount").GetDecimal(), entry.GetProperty("balance").GetDecimal()).Should().Be((1, "INVOICE", "SINV-1", 1500m, 1750m));
    }

    [Fact]
    public async Task The_aging_comes_back_in_the_usual_envelope_with_totals_customers_and_invoices()
    {
        var response = await Get(AgingUrl, "42", AgingView);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = json.RootElement.GetProperty("result");

        result.GetProperty("criteria").GetProperty("asOf").GetDateTime().Should().Be(new DateTime(2026, 9, 20));
        var total = result.GetProperty("totals")[0];
        (total.GetProperty("currencyCode").GetString(), total.GetProperty("invoiceCount").GetInt32(), total.GetProperty("days31To60").GetDecimal(),
         total.GetProperty("over90").GetDecimal(), total.GetProperty("total").GetDecimal()).Should().Be(("PKR", 1, 200m, 0m, 200m));

        result.GetProperty("customers")[0].GetProperty("customerName").GetString().Should().Be("Acme Ltd");

        var invoice = result.GetProperty("invoices")[0];
        (invoice.GetProperty("invoiceNumber").GetString(), invoice.GetProperty("daysPastDue").GetInt32(), invoice.GetProperty("bucket").GetString(),
         invoice.GetProperty("grandTotal").GetDecimal(), invoice.GetProperty("amountPaid").GetDecimal(), invoice.GetProperty("outstanding").GetDecimal())
            .Should().Be(("SINV-1", 40, "31-60", 250m, 50m, 200m));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task The_downloads_are_attachments_of_the_right_type_named_by_their_date(bool ledger, bool pdf)
    {
        var url = ledger ? (pdf ? LedgerPdf : LedgerXls) : (pdf ? AgingPdf : AgingXls);
        var response = await Get(url, "42", ledger ? LedgerExport : AgingExport);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var expectedType = pdf ? "application/pdf" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        var expectedName = $"{(ledger ? "customer-ledger" : "aging-receivables")}-20260920.{(pdf ? "pdf" : "xlsx")}";
        response.Content.Headers.ContentType!.MediaType.Should().Be(expectedType);
        response.Content.Headers.ContentDisposition!.FileName.Should().Be(expectedName);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (pdf) Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
        else (bytes[0], bytes[1]).Should().Be(((byte)'P', (byte)'K'));
    }
}
