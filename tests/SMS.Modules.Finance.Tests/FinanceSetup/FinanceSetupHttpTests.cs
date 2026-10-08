using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Controllers;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Middleware;
using SMS.Shared.Pagination;
using Xunit;

namespace SMS.Modules.Finance.Tests.FinanceSetup;

/// <summary>
/// The two controllers through a real ASP.NET Core pipeline over the real services and an in-memory
/// database: routes, query and JSON binding, the camelCase / yyyy-MM-dd wire format the Angular service
/// expects, the permission gate, the MODULE_FINANCE gate on writes only, tenancy as the API resolves it, and
/// 400/404/409 as a browser meets them. The pipeline is the one <c>Program.cs</c> builds, in its order: the
/// exception middleware first, then routing, authentication, authorization and <see cref="TenantMiddleware"/>;
/// MVC with authentication required by default and the global feature filter; the tenant read from the
/// token's <c>organizationId</c> / <c>is_super_admin</c> claims by the real <c>TenantContext</c>. The
/// exception-to-status step is a copy of SMS.API's GlobalExceptionMiddleware (SMS.API cannot be referenced
/// from here) and <see cref="The_exception_mapping_matches_the_api_middleware_arm_for_arm"/> fails the moment
/// the two drift apart.
/// </summary>
public class FinanceSetupHttpTests : IAsyncLifetime
{
    private static readonly Guid Org   = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly SetupWorld _world = new();
    private readonly FakeSnapshots _snapshots = new();
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
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
                    options.Filters.Add<FeatureAuthorizationFilter>();
                }).ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyTheSetupControllers()));

                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Test", _ => { });
                services.AddAuthorization();
                services.AddSingleton<IAuthorizationPolicyProvider, PermissionClaimPolicyProvider>();

                // The real tenant context: organization and super-admin flag from the signed-in user's claims.
                services.AddTenantContext();
                services.AddSingleton<ITenantSnapshotProvider>(_snapshots);
                services.AddDbContext<FinanceDbContext>(o => o.UseInMemoryDatabase(_world.DbName));
                services.AddDbContext<DemandDbContext>(o => o.UseInMemoryDatabase(_world.DbName));
                services.AddSingleton(_world.Lookups().Object);
                services.AddSingleton<TimeProvider>(_world.Clock);
                services.AddScoped<IExchangeRateProvider, ExchangeRateProvider>();
                services.AddScoped<ITaxCodeService>(sp => new TaxCodeService(
                    sp.GetRequiredService<FinanceDbContext>(), sp.GetRequiredService<DemandDbContext>(), sp.GetRequiredService<TimeProvider>()));
                services.AddScoped<IExchangeRateService>(sp => new ExchangeRateService(
                    sp.GetRequiredService<FinanceDbContext>(), sp.GetRequiredService<ILookupsService>(),
                    sp.GetRequiredService<IExchangeRateProvider>(), sp.GetRequiredService<TimeProvider>()));
            });
            web.Configure(app =>
            {
                app.Use(ApiExceptionMiddleware);
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseMiddleware<TenantMiddleware>();
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

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private sealed class OnlyTheSetupControllers : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            feature.Controllers.Add(typeof(TaxCodesController).GetTypeInfo());
            feature.Controllers.Add(typeof(ExchangeRatesController).GetTypeInfo());
        }
    }

    /// <summary>Every organization active with MODULE_FINANCE on, unless a test says otherwise.</summary>
    private sealed class FakeSnapshots : ITenantSnapshotProvider
    {
        public HashSet<string> Features { get; } = ["MODULE_FINANCE"];
        public HashSet<Guid> Inactive { get; } = [];
        public Task<TenantSnapshot?> GetSnapshotAsync(Guid organizationId) =>
            Task.FromResult<TenantSnapshot?>(new TenantSnapshot(!Inactive.Contains(organizationId), Features.ToHashSet()));
        public void Invalidate(Guid organizationId) { }
    }

    /// <summary>
    /// Signs the request in from headers, with the claims the real JWT carries: <c>sub</c>, <c>permission</c>,
    /// <c>organizationId</c> and <c>is_super_admin</c>.
    /// </summary>
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
            if (Request.Headers.TryGetValue("X-Org", out var org))
                claims.Add(new Claim("organizationId", org.ToString()));
            if (Request.Headers.TryGetValue("X-Super-Admin", out var superAdmin))
                claims.Add(new Claim("is_super_admin", superAdmin.ToString()));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }

    private sealed class PermissionClaimPolicyProvider : DefaultAuthorizationPolicyProvider
    {
        public PermissionClaimPolicyProvider(IOptions<AuthorizationOptions> options) : base(options) { }

        public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
            policyName.StartsWith("Permission:", StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser().RequireClaim("permission", policyName["Permission:".Length..]).Build())
                : base.GetPolicyAsync(policyName);
    }

    /// <summary>
    /// SMS.API's GlobalExceptionMiddleware.HandleExceptionAsync, arm for arm (exception type → status), and its
    /// body: <c>ApiResponse.Fail(message, ErrorDetail)</c> in camelCase. Anything else is a 500 with the
    /// generic message, as there.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> ApiStatusByException = new Dictionary<string, int>
    {
        [nameof(NotFoundException)]               = 404,
        [nameof(AccountLockedException)]          = 429,
        [nameof(ConflictException)]               = 409,
        [nameof(BadRequestException)]             = 400,
        [nameof(ForbiddenException)]              = 403,
        [nameof(UnauthorizedException)]           = 401,
        [nameof(UnprocessableEntityException)]    = 422,
        [nameof(WorkflowNotFoundException)]       = 404,
        [nameof(WorkflowConfigurationException)]  = 422,
        [nameof(ApproverResolutionException)]     = 422,
        [nameof(DbUpdateConcurrencyException)]    = 409,
    };

    private static async Task ApiExceptionMiddleware(HttpContext context, Func<Task> next)
    {
        try
        {
            await next();
        }
        catch (Exception ex)
        {
            var known   = ApiStatusByException.TryGetValue(ex.GetType().Name, out var status);
            var message = !known ? "An unexpected error occurred."
                        : ex is DbUpdateConcurrencyException
                            ? "Another user or process changed the same stock at the same time, so nothing was saved. Please try again."
                            : ex.Message;

            context.Response.ContentType = "application/json";
            context.Response.StatusCode  = known ? status : 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                ApiResponse.Fail(message, new ErrorDetail { ExceptionMessage = ex.Message, ExceptionMessageDetail = ex.InnerException?.Message }),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
    }

    private Task<HttpResponseMessage> Send(
        HttpMethod method, string url, object? body = null, bool manage = true, bool signedIn = true, Guid? org = null, bool superAdmin = false)
    {
        var request = new HttpRequestMessage(method, url);
        if (signedIn)
        {
            request.Headers.Add("X-User", "42");
            request.Headers.Add("X-Org", (org ?? Org).ToString());
            if (superAdmin) request.Headers.Add("X-Super-Admin", "true");
        }
        if (manage) request.Headers.Add("X-Permissions", PermissionCodes.FINANCE_SETUP_MANAGE);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, Web), Encoding.UTF8, "application/json");
        return _client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string?> Message(HttpResponseMessage response) =>
        (await Json(response)).GetProperty("message").GetString();

    // ── The exception mapping is SMS.API's ───────────────────────────────────

    [Fact]
    public void The_exception_mapping_matches_the_api_middleware_arm_for_arm()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "SMS.API", "Middleware", "GlobalExceptionMiddleware.cs"));

        // "NotFoundException ex => (HttpStatusCode.NotFound, …)", "AccountLockedException ex => ((HttpStatusCode)429, …)",
        // "DbUpdateConcurrencyException => (HttpStatusCode.Conflict, …)".
        var arms = Regex.Matches(source, @"(\w+Exception)(?:\s+\w+)?\s*=>\s*\(\s*(?:HttpStatusCode\.(\w+)|\(HttpStatusCode\)(\d+))")
            .ToDictionary(
                m => m.Groups[1].Value,
                m => m.Groups[2].Success ? (int)Enum.Parse<HttpStatusCode>(m.Groups[2].Value) : int.Parse(m.Groups[3].Value));

        arms.Should().NotBeEmpty("the middleware's switch was found");
        arms.Should().BeEquivalentTo(ApiStatusByException, "this test host maps exceptions exactly as SMS.API does");
        // The 500 arm's message (SMS.API appends a log reference to it and sends no exception text).
        source.Should().Contain("_ => (HttpStatusCode.InternalServerError, \"An unexpected error occurred.\"");
        source.Should().Contain("ApiResponse.Fail(message, new ErrorDetail");
    }

    /// <summary>
    /// Above the test binaries, or — when they were built to an output folder outside the repository (the test
    /// host then runs there too) — above this source file as it was compiled.
    /// </summary>
    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        var starts = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile) ?? string.Empty };
        foreach (var start in starts.Where(s => s.Length > 0))
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "src", "SMS.API", "Middleware", "GlobalExceptionMiddleware.cs")))
                    return dir.FullName;
        throw new InvalidOperationException(
            $"The repository root (with src/SMS.API) was not found above {string.Join(" or ", starts)}.");
    }

    // ── Permissions and the module gate ──────────────────────────────────────

    public static IEnumerable<object[]> Writes()
    {
        var id = Guid.NewGuid();
        yield return [HttpMethod.Post,   "/api/finance/tax-codes"];
        yield return [HttpMethod.Put,    $"/api/finance/tax-codes/{id}"];
        yield return [HttpMethod.Post,   "/api/finance/tax-codes/from-rates-in-use"];
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Every_write_needs_finance_setup_manage(HttpMethod method, string url)
    {
        var response = await Send(method, url, new { }, manage: false);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Every_write_needs_the_finance_module(HttpMethod method, string url)
    {
        _snapshots.Features.Clear();
        var response = await Send(method, url, new { });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Message(response)).Should().Contain("MODULE_FINANCE");
    }

    [Theory]
    [InlineData("/api/finance/tax-codes")]
    [InlineData("/api/finance/tax-codes?side=SALES")]
    [InlineData("/api/finance/exchange-rates")]
    [InlineData("/api/finance/exchange-rates/quote?from=USD&to=PKR&date=2026-10-01")]
    public async Task Reads_need_only_a_sign_in_not_the_permission_nor_the_finance_module(string url)
    {
        _snapshots.Features.Clear(); // an organization selling without the Finance module still picks tax codes

        (await Send(HttpMethod.Get, url, manage: false)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Get, url, manage: false, signedIn: false)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_deactivated_organization_is_turned_away_before_any_read()
    {
        _snapshots.Inactive.Add(Org);

        (await Send(HttpMethod.Get, "/api/finance/tax-codes", manage: false)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Tax codes over the wire ──────────────────────────────────────────────

    [Fact]
    public async Task A_tax_code_round_trips_in_camel_case_with_the_save_message()
    {
        var created = await Send(HttpMethod.Post, "/api/finance/tax-codes", new
        {
            code = "gst17", name = "GST 17%", description = (string?)null, ratePercent = 17, usage = "SALES", isDefault = true, isActive = true
        });

        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(created);
        body.GetProperty("success").GetBoolean().Should().BeTrue();
        body.GetProperty("message").GetString().Should().Contain("GST17").And.Contain("default for sales");
        var result = body.GetProperty("result");
        result.GetProperty("code").GetString().Should().Be("GST17");
        result.GetProperty("ratePercent").GetDecimal().Should().Be(17m);
        result.GetProperty("usage").GetString().Should().Be("SALES");
        result.GetProperty("isDefault").GetBoolean().Should().BeTrue();
        result.GetProperty("isActive").GetBoolean().Should().BeTrue();
        result.GetProperty("description").ValueKind.Should().Be(JsonValueKind.Null);
        var uuid = result.GetProperty("uuid").GetGuid();

        var updated = await Send(HttpMethod.Put, $"/api/finance/tax-codes/{uuid}", new
        {
            code = "GST17", name = "GST 17%", ratePercent = 18, usage = "SALES", isDefault = true, isActive = true
        });
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Message(updated)).Should().Contain("documents already raised keep 17%");

        var list = await Json(await Send(HttpMethod.Get, "/api/finance/tax-codes?side=SALES", manage: false));
        list.GetProperty("result").EnumerateArray().Select(e => e.GetProperty("ratePercent").GetDecimal()).Should().Equal(18m);
    }

    [Fact]
    public async Task Bad_input_is_400_a_taken_code_is_409_and_an_unknown_code_is_404()
    {
        var bad = await Send(HttpMethod.Post, "/api/finance/tax-codes", new { code = "GST 17", name = "x", ratePercent = 17, usage = "SALES", isActive = true });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var badBody = await Json(bad);
        badBody.GetProperty("success").GetBoolean().Should().BeFalse();
        badBody.GetProperty("message").GetString().Should().Contain("only the letters");

        var ok = new { code = "GST17", name = "GST", ratePercent = 17, usage = "SALES", isActive = true };
        (await Send(HttpMethod.Post, "/api/finance/tax-codes", ok)).StatusCode.Should().Be(HttpStatusCode.OK);
        var taken = await Send(HttpMethod.Post, "/api/finance/tax-codes", ok);
        taken.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Message(taken)).Should().Contain("already a tax code GST17");

        (await Send(HttpMethod.Put, $"/api/finance/tax-codes/{Guid.NewGuid()}", ok)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Get, "/api/finance/tax-codes?side=BOTH", manage: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("GST 17", 17, "only the letters")]
    [InlineData("ÄST", 17, "only the letters")]
    [InlineData("GST٣", 17, "only the letters")]   // an Arabic-Indic digit is not 0–9
    [InlineData("ABCDEFGHIJKLMNOPQRSTU", 17, "at most 20")]
    [InlineData("GST", 17.125, "at most two decimals")]
    [InlineData("GST", 100.01, "between 0 and 100")]
    [InlineData("GST", -1, "between 0 and 100")]
    public async Task Each_bad_tax_code_is_a_400_with_the_reason(string code, double rate, string why)
    {
        var response = await Send(HttpMethod.Post, "/api/finance/tax-codes", new { code, name = "n", ratePercent = (decimal)rate, usage = "SALES", isActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Message(response)).Should().Contain(why);
    }

    [Theory]
    [InlineData(" vat-0_a ", 0, "VAT-0_A")]
    [InlineData("full", 100, "FULL")]
    [InlineData("half", 7.5, "HALF")]
    public async Task Lower_case_padded_codes_and_rates_0_to_100_are_accepted(string code, double rate, string stored)
    {
        var response = await Send(HttpMethod.Post, "/api/finance/tax-codes", new { code, name = "n", ratePercent = (decimal)rate, usage = "both", isActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await Json(response)).GetProperty("result");
        result.GetProperty("code").GetString().Should().Be(stored);
        result.GetProperty("ratePercent").GetDecimal().Should().Be((decimal)rate);
        result.GetProperty("usage").GetString().Should().Be("BOTH");
    }

    [Fact]
    public async Task An_inactive_code_sent_as_the_default_is_saved_but_not_as_the_default()
    {
        var response = await Send(HttpMethod.Post, "/api/finance/tax-codes", new { code = "OLD", name = "Old", ratePercent = 5, usage = "SALES", isDefault = true, isActive = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(response)).GetProperty("result").GetProperty("isDefault").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Inactive_codes_are_listed_only_when_asked_for()
    {
        await Send(HttpMethod.Post, "/api/finance/tax-codes", new { code = "OFF", name = "Off", ratePercent = 1, usage = "BOTH", isActive = false });

        (await Json(await Send(HttpMethod.Get, "/api/finance/tax-codes"))).GetProperty("result").GetArrayLength().Should().Be(0);
        (await Json(await Send(HttpMethod.Get, "/api/finance/tax-codes?includeInactive=true"))).GetProperty("result").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Codes_from_rates_in_use_answers_created_and_skipped_rates()
    {
        await _world.For(Org).PlaceOrder(false, 17m, 0m);
        await Send(HttpMethod.Post, "/api/finance/tax-codes", new { code = "ZERO", name = "Zero", ratePercent = 0, usage = "SALES", isActive = true });

        var response = await Send(HttpMethod.Post, "/api/finance/tax-codes/from-rates-in-use", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await Json(response)).GetProperty("result");
        result.GetProperty("created").EnumerateArray().Select(c => c.GetProperty("code").GetString()).Should().Equal("TAX17");
        result.GetProperty("skippedRates").EnumerateArray().Select(r => r.GetDecimal()).Should().Equal(0m);
    }

    // ── Exchange rates over the wire (A35-E-02: reads only, served from finance.currency_rates) ──

    /// <summary>One organization with PKR as its rate currency and USD 278.5 from 2026-10-01.</summary>
    private async Task SeedRatesAsync(Guid org, decimal usd = 278.5m)
    {
        await _world.For(org).Seed(
            new OrgCurrency { OrganizationId = org, CurrencyId = SetupWorld.PkrId, Code = "PKR", Name = "Pakistani Rupee", Symbol = "Rs", DisplayOrder = 1 },
            new OrgCurrency { OrganizationId = org, CurrencyId = SetupWorld.UsdId, Code = "USD", Name = "US Dollar", Symbol = "$", DisplayOrder = 2 },
            new CurrencyRate { OrganizationId = org, CurrencyId = SetupWorld.PkrId, CurrencyCode = "PKR", Rate = 1m, InverseRate = 1m,
                EffectiveFrom = CurrencyConventions.SystemStart, EffectiveTo = CurrencyConventions.OpenEnd, Source = "SYSTEM" },
            new CurrencyRate { OrganizationId = org, CurrencyId = SetupWorld.UsdId, CurrencyCode = "USD", Rate = usd,
                InverseRate = CurrencyConventions.RoundRate(1m / usd), EffectiveFrom = new DateOnly(2026, 10, 1),
                EffectiveTo = CurrencyConventions.OpenEnd, Source = "MANUAL", Notes = "SBP" });
    }

    [Theory]
    [InlineData("POST", "/api/finance/exchange-rates")]
    [InlineData("PUT", "/api/finance/exchange-rates/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/finance/exchange-rates/00000000-0000-0000-0000-000000000001")]
    public async Task The_legacy_writes_are_gone(string method, string url)
    {
        var response = await Send(new HttpMethod(method), url, new { });
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task The_legacy_list_shows_each_range_against_the_rate_currency_with_its_start_as_a_plain_date()
    {
        await SeedRatesAsync(Org);

        var list = (await Json(await Send(HttpMethod.Get, "/api/finance/exchange-rates?from=usd&to=PKR", manage: false)))
            .GetProperty("result").EnumerateArray().ToList();
        list.Should().ContainSingle();
        list[0].GetProperty("fromCurrencyCode").GetString().Should().Be("USD");
        list[0].GetProperty("toCurrencyCode").GetString().Should().Be("PKR");
        list[0].GetProperty("rate").GetDecimal().Should().Be(278.5m);
        list[0].GetProperty("effectiveDate").GetString().Should().Be("2026-10-01");
        list[0].GetProperty("notes").GetString().Should().Be("SBP");

        (await Json(await Send(HttpMethod.Get, "/api/finance/exchange-rates", manage: false)))
            .GetProperty("result").GetArrayLength().Should().Be(1, "the rate currency's SYSTEM row is not a pair");
    }

    [Fact]
    public async Task The_quote_answers_a_rate_its_reverse_or_a_null_result()
    {
        await SeedRatesAsync(Org);

        var direct = await Json(await Send(HttpMethod.Get, "/api/finance/exchange-rates/quote?from=USD&to=PKR&date=2026-10-15", manage: false));
        direct.GetProperty("result").GetProperty("rate").GetDecimal().Should().Be(278.5m);
        direct.GetProperty("result").GetProperty("effectiveDate").GetString().Should().Be("2026-10-01");
        direct.GetProperty("result").GetProperty("inverted").GetBoolean().Should().BeFalse();

        var reverse = await Json(await Send(HttpMethod.Get, "/api/finance/exchange-rates/quote?from=pkr&to=usd&date=2026-10-15", manage: false));
        reverse.GetProperty("result").GetProperty("rate").GetDecimal().Should().Be(0.0035906643m);

        var none = await Send(HttpMethod.Get, "/api/finance/exchange-rates/quote?from=USD&to=EUR&date=2026-10-15", manage: false);
        none.StatusCode.Should().Be(HttpStatusCode.OK);
        var noneBody = await Json(none);
        noneBody.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null);
        noneBody.GetProperty("message").GetString().Should().Contain("No USD → EUR rate");

        (await Send(HttpMethod.Get, "/api/finance/exchange-rates/quote?to=PKR", manage: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Tenancy as the API resolves it ───────────────────────────────────────

    [Fact]
    public async Task Another_organizations_user_sees_and_reaches_none_of_this_organizations_codes_or_rates()
    {
        var code = (await Json(await Send(HttpMethod.Post, "/api/finance/tax-codes",
            new { code = "GST17", name = "GST", ratePercent = 17, usage = "SALES", isDefault = true, isActive = true }))).GetProperty("result").GetProperty("uuid").GetGuid();
        await SeedRatesAsync(Org);

        (await Json(await Send(HttpMethod.Get, "/api/finance/tax-codes?includeInactive=true", org: Other))).GetProperty("result").GetArrayLength().Should().Be(0);
        (await Json(await Send(HttpMethod.Get, "/api/finance/exchange-rates", org: Other))).GetProperty("result").GetArrayLength().Should().Be(0);
        (await Json(await Send(HttpMethod.Get, "/api/finance/exchange-rates/quote?from=USD&to=PKR&date=2026-10-15", org: Other)))
            .GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null);

        (await Send(HttpMethod.Put, $"/api/finance/tax-codes/{code}", new { code = "GST17", name = "x", ratePercent = 1, usage = "SALES", isActive = true }, org: Other))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Its own GST17 does not collide with ours, and becoming its default leaves ours alone.
        (await Send(HttpMethod.Post, "/api/finance/tax-codes",
            new { code = "gst17", name = "Theirs", ratePercent = 16, usage = "BOTH", isDefault = true, isActive = true }, org: Other))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var ours = (await Json(await Send(HttpMethod.Get, "/api/finance/tax-codes"))).GetProperty("result").EnumerateArray().Single();
        ours.GetProperty("isDefault").GetBoolean().Should().BeTrue();
        ours.GetProperty("ratePercent").GetDecimal().Should().Be(17m);
    }

    [Fact]
    public async Task A_super_admin_working_in_another_organization_acts_for_that_organization_only()
    {
        var code = (await Json(await Send(HttpMethod.Post, "/api/finance/tax-codes",
            new { code = "GST17", name = "GST", ratePercent = 17, usage = "SALES", isActive = true }))).GetProperty("result").GetProperty("uuid").GetGuid();
        await SeedRatesAsync(Org);
        await SeedRatesAsync(Other, usd: 280m);
        _snapshots.Features.Clear(); // a super admin passes the module gate, as in the API

        // The tenant filter lets a super admin through; the services still answer for the organization in the token.
        (await Json(await Send(HttpMethod.Get, "/api/finance/tax-codes?includeInactive=true", org: Other, superAdmin: true))).GetProperty("result").GetArrayLength().Should().Be(0);
        (await Json(await Send(HttpMethod.Get, "/api/finance/exchange-rates", org: Other, superAdmin: true)))
            .GetProperty("result").EnumerateArray().Single().GetProperty("rate").GetDecimal().Should().Be(280m);
        (await Json(await Send(HttpMethod.Get, "/api/finance/exchange-rates/quote?from=USD&to=PKR&date=2026-10-15", org: Other, superAdmin: true)))
            .GetProperty("result").GetProperty("rate").GetDecimal().Should().Be(280m);
        (await Send(HttpMethod.Put, $"/api/finance/tax-codes/{code}", new { code = "GST17", name = "x", ratePercent = 1, usage = "SALES", isActive = true }, org: Other, superAdmin: true))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var theirs = await Send(HttpMethod.Post, "/api/finance/tax-codes", new { code = "GST17", name = "B's", ratePercent = 16, usage = "SALES", isActive = true }, org: Other, superAdmin: true);
        theirs.StatusCode.Should().Be(HttpStatusCode.OK, "B's GST17 is not A's");

        await using var auditor = _world.Auditor();
        (await auditor.TaxCodes.CountAsync(t => t.OrganizationId == Other)).Should().Be(1);
        (await auditor.TaxCodes.SingleAsync(t => t.OrganizationId == Org)).RatePercent.Should().Be(17m, "A's code was not touched");
    }
}
