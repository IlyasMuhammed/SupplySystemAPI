using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using SMS.Shared.Pagination;
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
using SMS.Shared.Common;
using Xunit;
using Microsoft.AspNetCore.Builder;

namespace SMS.Modules.Finance.Tests.SupplierInvoices;

/// <summary>
/// POST /api/finance/invoices/{uuid}/reverse through a real ASP.NET Core pipeline (as ReceivablesHttpTests):
/// the route, the INVOICE_PROCESS gate, the { reason } body and the answer. The service is a mock — its
/// behaviour has its own tests.
/// </summary>
public class ReverseEndpointHttpTests : IAsyncLifetime
{
    private static readonly Guid Id = Guid.NewGuid();

    private readonly Mock<IInvoiceService> _invoices = new();
    private readonly Mock<INotificationService> _notifications = new();
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _invoices.Setup(s => s.ReverseAsync(Id, It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(true);
        _invoices.Setup(s => s.ReverseAsync(It.Is<Guid>(g => g != Id), It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(false);
        _invoices.Setup(s => s.GetByUuidAsync(Id)).ReturnsAsync(new InvoiceDetailModel
        {
            UUID = Id, InvoiceNumber = "INV-2026-00042", MatchStatus = "Reversed", ReversalReason = "Entered twice", CreatedBy = 3
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
                }).ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyTheInvoicesController()));

                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Test", _ => { });
                services.AddAuthorization();
                services.AddSingleton<IAuthorizationPolicyProvider, PermissionClaimPolicyProvider>();

                services.AddSingleton(_invoices.Object);
                services.AddSingleton(Mock.Of<IInvoiceDocumentService>());
                services.AddSingleton(_notifications.Object);
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

    private Task<HttpResponseMessage> PostAsync(string url, string? json, params string[] permissions)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-User", "42");
        if (permissions.Length > 0) request.Headers.Add("X-Permissions", string.Join(',', permissions));
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Without_INVOICE_PROCESS_the_reversal_is_forbidden_and_the_service_is_never_asked()
    {
        var response = await PostAsync($"/api/finance/invoices/{Id}/reverse", """{"reason":"x"}""",
            PermissionCodes.INVOICE_VIEW, PermissionCodes.PAYMENT_PROCESS, PermissionCodes.PAYMENT_APPROVE);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _invoices.Verify(s => s.ReverseAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task With_INVOICE_PROCESS_the_reason_reaches_the_service_and_the_reversed_invoice_comes_back()
    {
        var response = await PostAsync($"/api/finance/invoices/{Id}/reverse", """{"reason":"Entered twice"}""", PermissionCodes.INVOICE_PROCESS);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _invoices.Verify(s => s.ReverseAsync(Id, "Entered twice", 42), Times.Once);

        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("result").GetProperty("matchStatus").GetString().Should().Be("Reversed");
        body.RootElement.GetProperty("result").GetProperty("reversalReason").GetString().Should().Be("Entered twice");
        _notifications.Verify(n => n.TryCreateAsync(It.Is<NotificationRequest>(r => r.Type == "INVOICE_REVERSED" && r.UserId == 3)), Times.Once);
    }

    [Fact]
    public async Task An_unknown_invoice_is_a_404()
    {
        var response = await PostAsync($"/api/finance/invoices/{Guid.NewGuid()}/reverse", """{"reason":"x"}""", PermissionCodes.INVOICE_PROCESS);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void The_action_names_its_permission_and_route()
    {
        var method = typeof(InvoicesController).GetMethod(nameof(InvoicesController.Reverse))!;

        method.GetCustomAttribute<RequirePermissionAttribute>()!.Policy.Should().Be($"Permission:{PermissionCodes.INVOICE_PROCESS}");
        method.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("{uuid:guid}/reverse");
    }

    // ── Every action of the controller (hardening: only reverse used to be gated) ──

    private static IEnumerable<MethodInfo> Actions =>
        typeof(InvoicesController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetCustomAttributes<HttpMethodAttribute>().Any());

    [Fact]
    public void Every_action_requires_a_permission_and_none_is_anonymous()
    {
        Actions.Should().HaveCount(9);
        Actions.Where(a => a.GetCustomAttributes<RequirePermissionAttribute>().Count() != 1
                        || a.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Select(a => a.Name)
            .Should().BeEmpty("a supplier invoice must not be readable or changeable by every signed-in user of the organization");
        typeof(InvoicesController).GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
    }

    [Theory]
    // Reading is INVOICE_VIEW (the Auditor has it); everything that changes an invoice is INVOICE_PROCESS — the
    // seeded "Verify and approve invoices for payment" permission of the Finance Officer and Finance Manager, and
    // what the invoice pages check before they offer an action. Approval is not PAYMENT_APPROVE: that is the
    // Finance Manager's sign-off on supplier payments, and the Finance Officer, who approves invoices, lacks it.
    [InlineData(nameof(InvoicesController.GetList),          "INVOICE_VIEW")]
    [InlineData(nameof(InvoicesController.GetById),          "INVOICE_VIEW")]
    [InlineData(nameof(InvoicesController.DownloadPdf),      "INVOICE_VIEW")]
    [InlineData(nameof(InvoicesController.Create),           "INVOICE_PROCESS")]
    [InlineData(nameof(InvoicesController.Patch),            "INVOICE_PROCESS")]
    [InlineData(nameof(InvoicesController.Approve),          "INVOICE_PROCESS")]
    [InlineData(nameof(InvoicesController.Reject),           "INVOICE_PROCESS")]
    [InlineData(nameof(InvoicesController.Reverse),          "INVOICE_PROCESS")]
    [InlineData(nameof(InvoicesController.UploadAttachment), "INVOICE_PROCESS")]
    public void Each_action_needs_the_permission_that_matches_what_it_does(string action, string code) =>
        typeof(InvoicesController).GetMethod(action)!.GetCustomAttribute<RequirePermissionAttribute>()!.Policy
            .Should().Be($"Permission:{code}");

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? json, params string[] permissions)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-User", "42");
        if (permissions.Length > 0) request.Headers.Add("X-Permissions", string.Join(',', permissions));
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task An_auditor_reads_an_invoice_but_cannot_approve_reject_edit_or_create_one()
    {
        _invoices.Setup(s => s.GetListAsync(It.IsAny<InvoiceFilter>())).ReturnsAsync(new PaginatedResponse<InvoiceListItemModel>());

        (await SendAsync(HttpMethod.Get, "/api/finance/invoices", null, PermissionCodes.INVOICE_VIEW)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, $"/api/finance/invoices/{Id}", null, PermissionCodes.INVOICE_VIEW)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await SendAsync(HttpMethod.Post, $"/api/finance/invoices/{Id}/approve", "{}", PermissionCodes.INVOICE_VIEW, PermissionCodes.PAYMENT_APPROVE))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, $"/api/finance/invoices/{Id}/reject", """{"reason":"x"}""", PermissionCodes.INVOICE_VIEW))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Patch, $"/api/finance/invoices/{Id}", """{"notes":"x"}""", PermissionCodes.INVOICE_VIEW))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/finance/invoices", "{}", PermissionCodes.INVOICE_VIEW))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _invoices.Verify(s => s.ApproveAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
        _invoices.Verify(s => s.RejectAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        _invoices.Verify(s => s.PatchAsync(It.IsAny<Guid>(), It.IsAny<PatchInvoiceRequest>(), It.IsAny<int>()), Times.Never);
        _invoices.Verify(s => s.CreateAsync(It.IsAny<CreateInvoiceRequest>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task A_signed_in_user_with_neither_permission_cannot_even_read_one()
    {
        (await SendAsync(HttpMethod.Get, $"/api/finance/invoices/{Id}", null, PermissionCodes.PAYMENT_VIEW)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/api/finance/invoices", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _invoices.Verify(s => s.GetListAsync(It.IsAny<InvoiceFilter>()), Times.Never);
        _invoices.Verify(s => s.GetByUuidAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task An_INVOICE_PROCESS_holder_approves_without_holding_PAYMENT_APPROVE()
    {
        _invoices.Setup(s => s.ApproveAsync(Id, It.IsAny<string?>(), It.IsAny<int>())).ReturnsAsync(true);

        var response = await SendAsync(HttpMethod.Post, $"/api/finance/invoices/{Id}/approve", """{"notes":"OK"}""",
            PermissionCodes.INVOICE_VIEW, PermissionCodes.INVOICE_PROCESS);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _invoices.Verify(s => s.ApproveAsync(Id, "OK", 42), Times.Once);
    }

    // ── Test host plumbing (as ReceivablesHttpTests) ─────────────────────────

    private sealed class OnlyTheInvoicesController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            feature.Controllers.Add(typeof(InvoicesController).GetTypeInfo());
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

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
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
}
