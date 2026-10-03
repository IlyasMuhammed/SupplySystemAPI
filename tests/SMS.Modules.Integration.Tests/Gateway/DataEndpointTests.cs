using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Auth;
using SMS.Modules.Integration.Controllers.Admin;
using SMS.Modules.Integration.Controllers.Gateway;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Authorization;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Gateway;

/// <summary>
/// The API-key data endpoints over a real test server: routing, the scheme at class level, the scope
/// filter, GatewayResult → HTTP mapping and the camelCase body. The real API-key handler belongs to the
/// connection work package; a stand-in handler under the same scheme name issues the same claims.
/// </summary>
public class DataEndpointTests : IAsyncLifetime
{
    private const string Base = "/api/gateway/quickbooks/v1";

    private readonly RecordingGateway _gateway = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddControllers().AddApplicationPart(typeof(QuickBooksDataController).Assembly);
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, StandInApiKeyHandler>(ApiKeyDefaults.Scheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(o => o.AddPolicy(ApiKeyDefaults.RateLimitPolicy, _ => RateLimitPartition.GetNoLimiter("test")));
        builder.Services.AddSingleton<IQuickBooksGateway>(_gateway);

        _app = builder.Build();
        _app.UseRouting();
        _app.UseRateLimiter();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapControllers();
        await _app.StartAsync();

        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null, params string[] scopes)
    {
        var request = new HttpRequestMessage(method, Base + path);
        if (scopes.Length > 0) request.Headers.Add(StandInApiKeyHandler.ScopesHeader, string.Join(',', scopes));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    // ── Authentication and scopes ────────────────────────────────────────────

    [Fact]
    public async Task No_API_key_is_401_and_the_gateway_is_never_reached()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Put, "/customers/C-1", TestPayloads.Customer()));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _gateway.Received.Should().BeEmpty();

        (await _client.SendAsync(Request(HttpMethod.Get, "/customers/C-1"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _client.SendAsync(Request(HttpMethod.Post, "/status", new { kind = "customers", externalIds = new[] { "C-1" } }))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_key_without_the_scope_is_403_insufficient_scope()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Put, "/customers/C-1", TestPayloads.Customer(), ApiScopesText.VendorsWrite));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(response)).GetProperty("code").GetString().Should().Be("insufficient_scope");
        _gateway.Received.Should().BeEmpty();
    }

    [Theory]
    [InlineData("PUT", "/customers/X", ApiScopesText.CustomersWrite, nameof(IQuickBooksGateway.UpsertCustomerAsync))]
    [InlineData("PUT", "/vendors/X", ApiScopesText.VendorsWrite, nameof(IQuickBooksGateway.UpsertVendorAsync))]
    [InlineData("PUT", "/items/X", ApiScopesText.ItemsWrite, nameof(IQuickBooksGateway.UpsertItemAsync))]
    [InlineData("PUT", "/sales-invoices/X", ApiScopesText.InvoicesWrite, nameof(IQuickBooksGateway.UpsertSalesInvoiceAsync))]
    [InlineData("POST", "/sales-invoices/X/void", ApiScopesText.InvoicesWrite, nameof(IQuickBooksGateway.VoidSalesInvoiceAsync))]
    [InlineData("PUT", "/bills/X", ApiScopesText.BillsWrite, nameof(IQuickBooksGateway.UpsertBillAsync))]
    public async Task Each_write_endpoint_needs_its_own_scope_and_reaches_its_gateway_method(string method, string path, string scope, string gatewayMethod)
    {
        var wrong = await _client.SendAsync(Request(new HttpMethod(method), path, new { }, ApiScopesText.StatusRead));
        wrong.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var right = await _client.SendAsync(Request(new HttpMethod(method), path, new { }, scope));
        right.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _gateway.Received.Should().ContainSingle().Which.Method.Should().Be(gatewayMethod);
    }

    // ── GatewayResult → HTTP ─────────────────────────────────────────────────

    [Fact]
    public async Task Accepted_is_202_with_outcome_and_state_in_camelCase_and_the_route_id_is_used()
    {
        var body = TestPayloads.Customer(id: "");
        var response = await _client.SendAsync(Request(HttpMethod.Put, "/customers/C-77", body, ApiScopesText.CustomersWrite));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var json = await Json(response);
        json.GetProperty("outcome").GetString().Should().Be("Accepted");
        json.GetProperty("state").GetString().Should().Be("Pending");
        json.TryGetProperty("missingDependencies", out _).Should().BeFalse();

        ((CustomerPayload)_gateway.Received.Single().Payload!).ExternalId.Should().Be("C-77");
    }

    [Fact]
    public async Task A_different_id_in_the_body_is_400_and_the_same_one_is_fine()
    {
        var mismatch = await _client.SendAsync(Request(HttpMethod.Put, "/customers/C-1", TestPayloads.Customer(id: "C-2"), ApiScopesText.CustomersWrite));
        mismatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(mismatch)).GetProperty("code").GetString().Should().Be("external_id_mismatch");
        _gateway.Received.Should().BeEmpty();

        var same = await _client.SendAsync(Request(HttpMethod.Put, "/customers/C-1", TestPayloads.Customer(id: "C-1"), ApiScopesText.CustomersWrite));
        same.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Waiting_on_dependencies_is_202_with_the_records_the_caller_must_send()
    {
        _gateway.Next = GatewayResult.Waiting([new GatewayDependency(SyncKind.Customer, "C-1"), new GatewayDependency(SyncKind.Item, "I-9")]);

        var response = await _client.SendAsync(Request(HttpMethod.Put, "/sales-invoices/SI-1", TestPayloads.Invoice(), ApiScopesText.InvoicesWrite));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var json = await Json(response);
        json.GetProperty("outcome").GetString().Should().Be("WaitingOnDependency");
        json.GetProperty("state").GetString().Should().Be("WaitingOnDependency");
        var missing = json.GetProperty("missingDependencies").EnumerateArray().Select(e => (e.GetProperty("kind").GetString(), e.GetProperty("externalId").GetString())).ToList();
        missing.Should().Equal(("Customer", "C-1"), ("Item", "I-9"));
    }

    [Fact]
    public async Task Invalid_is_400_validation_failed_with_field_level_errors()
    {
        _gateway.Next = GatewayResult.Invalid([new GatewayError("displayName", "NAME_TOO_LONG", "Too long."), new GatewayError("email", "EMAIL_INVALID", "Bad email.")]);

        var response = await _client.SendAsync(Request(HttpMethod.Put, "/customers/C-1", TestPayloads.Customer(), ApiScopesText.CustomersWrite));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await Json(response);
        json.GetProperty("code").GetString().Should().Be("validation_failed");
        json.GetProperty("message").GetString().Should().Contain("2 validation rules");
        var errors = json.GetProperty("errors").EnumerateArray().ToList();
        errors.Should().HaveCount(2);
        errors[0].GetProperty("field").GetString().Should().Be("displayName");
        errors[0].GetProperty("code").GetString().Should().Be("NAME_TOO_LONG");
        errors[0].GetProperty("message").GetString().Should().Be("Too long.");
    }

    [Fact]
    public async Task Not_connected_is_409_and_disabled_is_200()
    {
        _gateway.Next = GatewayResult.NotConnected();
        var notConnected = await _client.SendAsync(Request(HttpMethod.Put, "/vendors/V-1", TestPayloads.Vendor(), ApiScopesText.VendorsWrite));
        notConnected.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(notConnected)).GetProperty("code").GetString().Should().Be("not_connected");

        _gateway.Next = GatewayResult.Disabled();
        var disabled = await _client.SendAsync(Request(HttpMethod.Put, "/vendors/V-1", TestPayloads.Vendor(), ApiScopesText.VendorsWrite));
        disabled.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await Json(disabled);
        json.GetProperty("outcome").GetString().Should().Be("Disabled");
        json.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Void_is_202()
    {
        _gateway.Next = GatewayResult.Accepted(SyncState.Voided);
        var response = await _client.SendAsync(Request(HttpMethod.Post, "/sales-invoices/SI-1/void", null, ApiScopesText.InvoicesWrite));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Json(response)).GetProperty("state").GetString().Should().Be("Voided");
        _gateway.Received.Single().Payload.Should().Be("SI-1");
    }

    // ── Status ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_of_one_record_by_kind_path()
    {
        _gateway.Statuses.Add(new SyncStatus(SyncKind.SalesInvoice, "SI-1", SyncState.Synced, "901", "INV-1", null, null, new DateTime(2026, 9, 1), "https://qbo/app/invoice?txnId=901"));

        var found = await _client.SendAsync(Request(HttpMethod.Get, "/sales-invoices/SI-1", null, ApiScopesText.StatusRead));
        found.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await Json(found);
        json.GetProperty("state").GetString().Should().Be("Synced");
        json.GetProperty("remoteId").GetString().Should().Be("901");
        json.GetProperty("remoteDocNumber").GetString().Should().Be("INV-1");

        (await _client.SendAsync(Request(HttpMethod.Get, "/sales-invoices/SI-404", null, ApiScopesText.StatusRead))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var unknownKind = await _client.SendAsync(Request(HttpMethod.Get, "/widgets/SI-1", null, ApiScopesText.StatusRead));
        unknownKind.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(unknownKind)).GetProperty("code").GetString().Should().Be("unknown_kind");

        (await _client.SendAsync(Request(HttpMethod.Get, "/sales-invoices/SI-1", null, ApiScopesText.InvoicesWrite))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Batch_status()
    {
        _gateway.Statuses.Add(new SyncStatus(SyncKind.Customer, "C-1", SyncState.Pending, null, null, null, null, null, null));

        var response = await _client.SendAsync(Request(HttpMethod.Post, "/status", new { kind = "customers", externalIds = new[] { "C-1", "C-2" } }, ApiScopesText.StatusRead));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = (await Json(response)).GetProperty("items").EnumerateArray().ToList();
        items.Should().ContainSingle();
        items[0].GetProperty("externalId").GetString().Should().Be("C-1");
        items[0].GetProperty("kind").GetString().Should().Be("Customer");

        var bad = await _client.SendAsync(Request(HttpMethod.Post, "/status", new { kind = "nope", externalIds = new[] { "C-1" } }, ApiScopesText.StatusRead));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Attributes: the security contract, checked on the types themselves ──

    [Fact]
    public void Data_controller_is_API_key_only_feature_gated_rate_limited_and_never_anonymous()
    {
        var type = typeof(QuickBooksDataController);

        type.GetCustomAttributes<AuthorizeAttribute>().Should().ContainSingle(a => a.AuthenticationSchemes == ApiKeyDefaults.Scheme);
        type.GetCustomAttribute<RequiresFeatureAttribute>()!.FeatureCode.Should().Be("MODULE_INTEGRATION");
        type.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName.Should().Be(ApiKeyDefaults.RateLimitPolicy);
        type.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();

        foreach (var action in Actions(type))
        {
            action.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull($"{action.Name} must never be anonymous");
            action.GetCustomAttributes<RequireApiScopeAttribute>().Should().ContainSingle($"{action.Name} needs a scope");
        }
    }

    [Fact]
    public void Admin_controllers_need_a_permission_on_every_action_and_the_feature()
    {
        var expected = new Dictionary<string, string>
        {
            ["MatchingController.Scan"]      = PermissionCodes.INTEGRATION_MANAGE,
            ["MatchingController.List"]      = PermissionCodes.INTEGRATION_VIEW,
            ["MatchingController.Confirm"]   = PermissionCodes.INTEGRATION_MANAGE,
            ["SyncController.Summary"]       = PermissionCodes.INTEGRATION_VIEW,
            ["SyncController.Items"]         = PermissionCodes.INTEGRATION_VIEW,
            ["SyncController.Log"]           = PermissionCodes.INTEGRATION_VIEW,
            ["SyncController.Retry"]         = PermissionCodes.INTEGRATION_SYNC,
            ["SyncController.Resolve"]       = PermissionCodes.INTEGRATION_MANAGE,
            ["SyncController.Push"]          = PermissionCodes.INTEGRATION_SYNC,
            ["SyncController.Backfill"]      = PermissionCodes.INTEGRATION_MANAGE,
            ["SyncController.StatusLookup"]  = PermissionCodes.INTEGRATION_VIEW
        };

        foreach (var type in new[] { typeof(MatchingController), typeof(SyncController) })
        {
            type.GetCustomAttribute<RequiresFeatureAttribute>()!.FeatureCode.Should().Be("MODULE_INTEGRATION");
            type.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();

            foreach (var action in Actions(type))
            {
                var key = $"{type.Name}.{action.Name}";
                expected.Should().ContainKey(key);
                action.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
                action.GetCustomAttribute<RequirePermissionAttribute>()!.Policy.Should().Be($"Permission:{expected[key]}", key);
            }
        }

        Actions(typeof(MatchingController))
            .SelectMany(a => a.GetCustomAttributes<HttpMethodAttribute>())
            .Should().NotContain(a => a.Template != null && a.Template.Contains("complete"), "/matching/complete belongs to the setup endpoints");
    }

    private static IEnumerable<MethodInfo> Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

    /// <summary>Scope strings as the tests send them (ApiScopes is internal to the module).</summary>
    public static class ApiScopesText
    {
        public const string CustomersWrite = "customers:write";
        public const string VendorsWrite   = "vendors:write";
        public const string ItemsWrite     = "items:write";
        public const string InvoicesWrite  = "invoices:write";
        public const string BillsWrite     = "bills:write";
        public const string StatusRead     = "status:read";
    }
}

/// <summary>
/// Stand-in for the connection work package's API-key handler: authenticates when the test sends a
/// scopes header, with the same claims the real one issues (org, client id and name, one claim per scope).
/// </summary>
internal sealed class StandInApiKeyHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string ScopesHeader = "X-Test-Scopes";

    public StandInApiKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ScopesHeader, out var scopes)) return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim>
        {
            new("organizationId", Guid.NewGuid().ToString()),
            new(IntegrationClaims.ApiClientId, "7"),
            new(IntegrationClaims.ApiClientName, "POS")
        };
        claims.AddRange(scopes.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => new Claim(IntegrationClaims.Scope, s)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
