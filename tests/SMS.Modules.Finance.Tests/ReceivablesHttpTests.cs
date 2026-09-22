using System.Net;
using System.Net.Http.Json;
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
/// A29-P7-08 — the three receivables controllers run through a real ASP.NET Core pipeline (an
/// in-memory test server), so what is checked is what a browser would meet: the route, the query-string
/// and JSON binding, the empty-body case the FIFO allocate depends on, and the permission gate actually
/// refusing a caller who lacks the permission. The services behind them are mocks; their behaviour has
/// its own tests.
/// <para>
/// The host is set up the way <c>Program.cs</c> sets up MVC — authentication required by default and
/// implicit "[Required] on non-nullable reference types" switched off — because both change how a
/// request is bound and answered. The permission check is the same claim test the real handler does
/// (<c>permission</c> claim equal to the code).
/// </para>
/// </summary>
public class ReceivablesHttpTests : IAsyncLifetime
{
    private static readonly Guid Id = Guid.NewGuid();

    private readonly Mock<ISalesInvoiceService> _invoices = new();
    private readonly Mock<ISalesInvoiceDocumentService> _documents = new();
    private readonly Mock<ISalesInvoiceDocumentArchive> _archive = new();
    private readonly Mock<ICustomerPaymentService> _payments = new();
    private readonly Mock<ICustomerLedgerQueryService> _ledger = new();

    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _invoices.Setup(s => s.GetAsync(It.IsAny<Guid>())).ReturnsAsync(new SalesInvoiceDetailModel { Uuid = Id, InvoiceNumber = "SINV-20260920-0001", Status = "ISSUED" });
        _invoices.Setup(s => s.ListAsync(It.IsAny<SalesInvoiceFilter>())).ReturnsAsync(new PaginatedResponse<SalesInvoiceListItemModel>());
        _invoices.Setup(s => s.CreateFromFulfillmentAsync(It.IsAny<Guid>(), It.IsAny<int>())).ReturnsAsync(new SalesInvoiceCreated(Id, "SINV-20260920-0001", 100m, "PKR", false));
        _invoices.Setup(s => s.IssueAsync(It.IsAny<Guid>(), It.IsAny<int>())).ReturnsAsync(new SalesInvoiceIssued(Id, "SINV-20260920-0001", "ISSUED", 100m, 100m));
        _archive.Setup(a => a.TryFileIssuedPdfAsync(It.IsAny<Guid>(), It.IsAny<int>())).ReturnsAsync((Guid?)Id);
        _archive.Setup(a => a.FilePdfAsync(It.IsAny<Guid>(), It.IsAny<int>())).ReturnsAsync(new StoredAttachment(Id, false));
        _documents.Setup(d => d.GeneratePdfAsync(It.IsAny<Guid>())).ReturnsAsync(new SalesInvoicePdf("SINV-20260920-0001.pdf", "%PDF-1.7 test"u8.ToArray()));
        _payments.Setup(s => s.GetAsync(It.IsAny<Guid>())).ReturnsAsync(new CustomerPaymentDetailModel { Uuid = Id, PaymentNumber = "CPAY-20260920-0001" });
        _payments.Setup(s => s.ListAsync(It.IsAny<CustomerPaymentFilter>())).ReturnsAsync(new PaginatedResponse<CustomerPaymentListItemModel>());
        _payments.Setup(s => s.RecordPaymentAsync(It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CustomerPaymentDetails>(), It.IsAny<int>()))
                 .ReturnsAsync(new CustomerPaymentRecorded(Id, "CPAY-20260920-0001", 1m, 0m, 1m, "PKR", [], 0m));
        _payments.Setup(s => s.AllocateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ManualPaymentAllocation>?>(), It.IsAny<int>()))
                 .ReturnsAsync(new CustomerPaymentAllocated(Id, "CPAY-20260920-0001", 1m, 1m, 0m, []));
        _ledger.Setup(s => s.GetLedgerAsync(It.IsAny<Guid>(), It.IsAny<CustomerLedgerFilter>())).ReturnsAsync(new PaginatedResponse<CustomerLedgerEntryModel>());

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
                }).ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyTheReceivablesControllers()));

                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Test", _ => { });
                services.AddAuthorization();
                services.AddSingleton<IAuthorizationPolicyProvider, PermissionClaimPolicyProvider>();

                services.AddSingleton(_invoices.Object);
                services.AddSingleton(_documents.Object);
                services.AddSingleton(_archive.Object);
                services.AddSingleton(_payments.Object);
                services.AddSingleton(_ledger.Object);
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

    // ── Test host plumbing ───────────────────────────────────────────────────

    private sealed class OnlyTheReceivablesControllers : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            foreach (var type in new[] { typeof(SalesInvoicesController), typeof(CustomerPaymentsController), typeof(CustomerLedgerController) })
                feature.Controllers.Add(type.GetTypeInfo());
        }
    }

    /// <summary>Who is calling, taken from two headers: <c>X-User</c> and a comma-separated <c>X-Permissions</c>.</summary>
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

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }

    /// <summary>"Permission:CODE" means the caller holds a <c>permission</c> claim equal to CODE — the real system's rule.</summary>
    private sealed class PermissionClaimPolicyProvider : DefaultAuthorizationPolicyProvider
    {
        public PermissionClaimPolicyProvider(IOptions<AuthorizationOptions> options) : base(options) { }

        public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
            policyName.StartsWith("Permission:", StringComparison.Ordinal)
                ? Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser().RequireClaim("permission", policyName["Permission:".Length..]).Build())
                : base.GetPolicyAsync(policyName);
    }

    private static readonly string[] AllReceivablesPermissions =
    [
        PermissionCodes.SALES_INVOICE_VIEW, PermissionCodes.SALES_INVOICE_MANAGE,
        PermissionCodes.CUSTOMER_PAYMENT_VIEW, PermissionCodes.CUSTOMER_PAYMENT_RECORD, PermissionCodes.CUSTOMER_LEDGER_VIEW
    ];

    private static HttpRequestMessage Request(HttpMethod method, string url, string? user = "42", IEnumerable<string>? permissions = null, string? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (user is not null) request.Headers.Add("X-User", user);
        if (permissions is not null) request.Headers.Add("X-Permissions", string.Join(',', permissions));
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string? json = null, params string[] permissions) =>
        _client.SendAsync(Request(method, url, "42", permissions.Length == 0 ? AllReceivablesPermissions : permissions, json));

    // ── The permission gate, end to end ──────────────────────────────────────

    public static IEnumerable<object[]> Endpoints()
    {
        var id = Id;
        (HttpMethod, string, string)[] rows =
        [
            (HttpMethod.Post,   "/api/sales-invoices",                       PermissionCodes.SALES_INVOICE_MANAGE),
            (HttpMethod.Get,    "/api/sales-invoices",                       PermissionCodes.SALES_INVOICE_VIEW),
            (HttpMethod.Get,    $"/api/sales-invoices/{id}",                 PermissionCodes.SALES_INVOICE_VIEW),
            (HttpMethod.Put,    $"/api/sales-invoices/{id}",                 PermissionCodes.SALES_INVOICE_MANAGE),
            (HttpMethod.Delete, $"/api/sales-invoices/{id}",                 PermissionCodes.SALES_INVOICE_MANAGE),
            (HttpMethod.Post,   $"/api/sales-invoices/{id}/issue",           PermissionCodes.SALES_INVOICE_MANAGE),
            (HttpMethod.Post,   $"/api/sales-invoices/{id}/attach-pdf",      PermissionCodes.SALES_INVOICE_MANAGE),
            (HttpMethod.Get,    $"/api/sales-invoices/{id}/pdf",             PermissionCodes.SALES_INVOICE_VIEW),
            (HttpMethod.Post,   "/api/customer-payments",                    PermissionCodes.CUSTOMER_PAYMENT_RECORD),
            (HttpMethod.Get,    "/api/customer-payments",                    PermissionCodes.CUSTOMER_PAYMENT_VIEW),
            (HttpMethod.Get,    $"/api/customer-payments/{id}",              PermissionCodes.CUSTOMER_PAYMENT_VIEW),
            (HttpMethod.Post,   $"/api/customer-payments/{id}/allocate",     PermissionCodes.CUSTOMER_PAYMENT_RECORD),
            (HttpMethod.Get,    $"/api/partners/{id}/ledger",                PermissionCodes.CUSTOMER_LEDGER_VIEW),
        ];
        foreach (var (method, url, permission) in rows)
            yield return [method.Method, url, permission];
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Nobody_who_has_not_logged_in_gets_anything(string method, string url, string permission)
    {
        _ = permission;
        var response = await _client.SendAsync(Request(new HttpMethod(method), url, user: null, json: "{}"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_logged_in_caller_without_the_permission_is_refused_even_holding_every_other_receivables_permission(string method, string url, string permission)
    {
        var others = AllReceivablesPermissions.Where(p => p != permission).Concat(["INVOICE_VIEW", "INVOICE_PROCESS", "PAYMENT_VIEW", "PAYMENT_PROCESS", "PAYMENT_APPROVE"]);

        var response = await _client.SendAsync(Request(new HttpMethod(method), url, "42", others, "{}"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task The_permission_the_action_names_is_enough_to_reach_it(string method, string url, string permission)
    {
        var response = await _client.SendAsync(Request(new HttpMethod(method), url, "42", [permission], "{}"));

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized).And.NotBe(HttpStatusCode.Forbidden);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }

    // ── Routes ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_id_that_is_not_a_guid_does_not_match_a_route()
    {
        var response = await Send(HttpMethod.Get, "/api/sales-invoices/not-a-guid");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _invoices.Verify(s => s.GetAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task The_wrong_verb_on_a_route_is_a_405_not_a_different_action()
    {
        (await Send(HttpMethod.Patch, $"/api/sales-invoices/{Id}")).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await Send(HttpMethod.Delete, "/api/customer-payments/" + Id)).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await Send(HttpMethod.Put, $"/api/customer-payments/{Id}/allocate")).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    // ── Query-string binding ─────────────────────────────────────────────────

    [Fact]
    public async Task The_invoice_list_binds_every_filter_from_the_query_string()
    {
        var partner = Guid.NewGuid();
        var order = Guid.NewGuid();
        SalesInvoiceFilter? seen = null;
        _invoices.Setup(s => s.ListAsync(It.IsAny<SalesInvoiceFilter>()))
                 .Callback<SalesInvoiceFilter>(f => seen = f).ReturnsAsync(new PaginatedResponse<SalesInvoiceListItemModel>());

        var response = await Send(HttpMethod.Get,
            $"/api/sales-invoices?partnerId={partner}&saleOrderUuid={order}&status=OVERDUE&dateFrom=2026-09-01&dateTo=2026-09-20&search=acme%20ltd&page=2&pageSize=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        seen.Should().NotBeNull();
        (seen!.PartnerId, seen.SaleOrderUuid, seen.Status, seen.Search, seen.Page, seen.PageSize).Should().Be((partner, order, "OVERDUE", "acme ltd", 2, 5));
        (seen.DateFrom, seen.DateTo).Should().Be((new DateTime(2026, 9, 1), new DateTime(2026, 9, 20)));
    }

    [Fact]
    public async Task With_no_query_string_the_filters_are_empty_and_paging_is_the_first_twenty()
    {
        SalesInvoiceFilter? seen = null;
        _invoices.Setup(s => s.ListAsync(It.IsAny<SalesInvoiceFilter>()))
                 .Callback<SalesInvoiceFilter>(f => seen = f).ReturnsAsync(new PaginatedResponse<SalesInvoiceListItemModel>());

        await Send(HttpMethod.Get, "/api/sales-invoices");

        (seen!.PartnerId, seen.Status, seen.DateFrom, seen.Search, seen.Page, seen.PageSize).Should().Be(((Guid?)null, (string?)null, (DateTime?)null, (string?)null, 1, 20));
    }

    [Fact]
    public async Task The_payment_list_binds_its_filters_including_the_unallocated_flag()
    {
        CustomerPaymentFilter? seen = null;
        _payments.Setup(s => s.ListAsync(It.IsAny<CustomerPaymentFilter>()))
                 .Callback<CustomerPaymentFilter>(f => seen = f).ReturnsAsync(new PaginatedResponse<CustomerPaymentListItemModel>());

        await Send(HttpMethod.Get, "/api/customer-payments?unallocated=true&method=cheque&status=RECEIVED&dateFrom=2026-09-01&search=chq");

        (seen!.Unallocated, seen.Method, seen.Status, seen.Search, seen.DateFrom).Should().Be((true, "cheque", "RECEIVED", "chq", (DateTime?)new DateTime(2026, 9, 1)));
    }

    [Fact]
    public async Task The_ledger_takes_the_customer_from_the_route_and_the_range_and_paging_from_the_query()
    {
        var partner = Guid.NewGuid();
        Guid seenPartner = Guid.Empty;
        CustomerLedgerFilter? seen = null;
        _ledger.Setup(s => s.GetLedgerAsync(It.IsAny<Guid>(), It.IsAny<CustomerLedgerFilter>()))
               .Callback<Guid, CustomerLedgerFilter>((p, f) => { seenPartner = p; seen = f; })
               .ReturnsAsync(new PaginatedResponse<CustomerLedgerEntryModel>());

        var response = await Send(HttpMethod.Get, $"/api/partners/{partner}/ledger?dateFrom=2026-09-01&dateTo=2026-09-20&page=3&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        seenPartner.Should().Be(partner);
        (seen!.DateFrom, seen.DateTo, seen.Page, seen.PageSize).Should().Be(((DateTime?)new DateTime(2026, 9, 1), (DateTime?)new DateTime(2026, 9, 20), 3, 10));
    }

    // ── JSON body binding ────────────────────────────────────────────────────

    [Fact]
    public async Task Recording_a_payment_binds_the_json_body_and_the_calling_user()
    {
        var partner = Guid.NewGuid();
        var invoice = Guid.NewGuid();
        CustomerPaymentDetails? details = null;
        _payments.Setup(s => s.RecordPaymentAsync(It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CustomerPaymentDetails>(), It.IsAny<int>()))
                 .Callback<Guid, decimal, string, CustomerPaymentDetails, int>((_, _, _, d, _) => details = d)
                 .ReturnsAsync(new CustomerPaymentRecorded(Id, "CPAY-1", 1m, 0m, 1m, "PKR", [], 0m));

        var response = await Send(HttpMethod.Post, "/api/customer-payments", $$"""
            { "partnerId": "{{partner}}", "amount": 2500.5, "method": "CHEQUE", "currencyCode": "PKR",
              "paymentDate": "2026-09-18", "chequeNumber": "004521",
              "allocations": [ { "invoiceUuid": "{{invoice}}", "amount": 1000 } ] }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _payments.Verify(s => s.RecordPaymentAsync(partner, 2500.5m, "CHEQUE", It.IsAny<CustomerPaymentDetails>(), 42), Times.Once);
        (details!.CurrencyCode, details.ChequeNumber, details.PaymentDate).Should().Be(("PKR", "004521", (DateTime?)new DateTime(2026, 9, 18)));
        details.Allocations.Should().ContainSingle().Which.Should().Be(new ManualPaymentAllocation(invoice, 1000m));
    }

    [Fact]
    public async Task A_payment_body_missing_string_fields_reaches_the_service_to_be_refused_there_not_bounced_by_the_framework()
    {
        // The app switches off implicit [Required] on non-nullable strings: validation is the services'.
        // Left on, a missing "method" would be an opaque framework 400 instead of the service's own message.
        var response = await Send(HttpMethod.Post, "/api/customer-payments", """{ "partnerId": "00000000-0000-0000-0000-000000000001", "amount": 1 }""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _payments.Verify(s => s.RecordPaymentAsync(It.IsAny<Guid>(), 1m, string.Empty, It.IsAny<CustomerPaymentDetails>(), 42), Times.Once);
    }

    [Fact]
    public async Task Malformed_json_is_a_400_before_any_service_is_called()
    {
        var response = await Send(HttpMethod.Post, "/api/customer-payments", "{ not json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _payments.Verify(s => s.RecordPaymentAsync(It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CustomerPaymentDetails>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Creating_and_updating_an_invoice_bind_their_json_bodies()
    {
        var delivery = Guid.NewGuid();
        UpdateSalesInvoiceRequest? update = null;
        _invoices.Setup(s => s.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateSalesInvoiceRequest>(), It.IsAny<int>()))
                 .Callback<Guid, UpdateSalesInvoiceRequest, int>((_, r, _) => update = r).Returns(Task.CompletedTask);

        (await Send(HttpMethod.Post, "/api/sales-invoices", $$"""{ "deliveryUuid": "{{delivery}}" }""")).StatusCode.Should().Be(HttpStatusCode.OK);
        _invoices.Verify(s => s.CreateFromFulfillmentAsync(delivery, 42), Times.Once);

        (await Send(HttpMethod.Put, $"/api/sales-invoices/{Id}", """{ "dueDate": "2026-10-30", "notes": "Net 45" }""")).StatusCode.Should().Be(HttpStatusCode.OK);
        (update!.DueDate, update.Notes).Should().Be((new DateTime(2026, 10, 30), "Net 45"));
    }

    // ── The allocate endpoint's three ways of saying "FIFO" or "exactly this" ─

    [Fact]
    public async Task Allocating_with_no_body_at_all_is_fifo()
    {
        // The most likely call from a browser: a POST with no content and no content type.
        var response = await _client.SendAsync(Request(HttpMethod.Post, $"/api/customer-payments/{Id}/allocate", "42", AllReceivablesPermissions));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _payments.Verify(s => s.AllocateAsync(Id, null, 42), Times.Once);
    }

    [Fact]
    public async Task Allocating_with_an_empty_json_object_is_also_fifo()
    {
        (await Send(HttpMethod.Post, $"/api/customer-payments/{Id}/allocate", "{}")).StatusCode.Should().Be(HttpStatusCode.OK);

        _payments.Verify(s => s.AllocateAsync(Id, null, 42), Times.Once);
    }

    [Fact]
    public async Task Allocating_with_a_list_applies_exactly_that_list()
    {
        var invoice = Guid.NewGuid();

        var response = await Send(HttpMethod.Post, $"/api/customer-payments/{Id}/allocate", $$"""{ "allocations": [ { "invoiceUuid": "{{invoice}}", "amount": 400 } ] }""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _payments.Verify(s => s.AllocateAsync(Id,
            It.Is<IReadOnlyList<ManualPaymentAllocation>?>(a => a != null && a.Count == 1 && a[0] == new ManualPaymentAllocation(invoice, 400m)), 42), Times.Once);
    }

    // ── What comes back ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_response_is_the_usual_envelope_in_camel_case()
    {
        var response = await Send(HttpMethod.Get, $"/api/sales-invoices/{Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("message").GetString().Should().NotBeNull();
        var result = json.RootElement.GetProperty("result");
        result.GetProperty("uuid").GetGuid().Should().Be(Id);
        result.GetProperty("invoiceNumber").GetString().Should().Be("SINV-20260920-0001");
        result.TryGetProperty("lines", out _).Should().BeTrue();
        result.TryGetProperty("payments", out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_record_that_is_not_there_is_a_404_with_the_usual_message()
    {
        _invoices.Setup(s => s.GetAsync(It.IsAny<Guid>())).ReturnsAsync((SalesInvoiceDetailModel?)null);

        var response = await Send(HttpMethod.Get, $"/api/sales-invoices/{Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        body.GetProperty("message").GetString().Should().Be("Record not found.");
    }

    [Fact]
    public async Task The_pdf_endpoint_serves_a_pdf_with_a_filename()
    {
        var response = await Send(HttpMethod.Get, $"/api/sales-invoices/{Id}/pdf");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        response.Content.Headers.ContentDisposition!.FileName.Should().Be("SINV-20260920-0001.pdf");
        Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync(), 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public async Task The_caller_id_reaches_every_service_that_records_who_did_it()
    {
        await _client.SendAsync(Request(HttpMethod.Post, $"/api/sales-invoices/{Id}/issue", user: "77", permissions: AllReceivablesPermissions));
        await _client.SendAsync(Request(HttpMethod.Delete, $"/api/sales-invoices/{Id}", user: "77", permissions: AllReceivablesPermissions));

        _invoices.Verify(s => s.IssueAsync(Id, 77), Times.Once);
        _invoices.Verify(s => s.DeleteAsync(Id, 77), Times.Once);
    }
}
