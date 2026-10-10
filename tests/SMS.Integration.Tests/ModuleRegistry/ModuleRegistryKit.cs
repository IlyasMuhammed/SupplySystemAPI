using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;

namespace SMS.Integration.Tests.ModuleRegistry;

/// <summary>An organization created through the platform admin's real create path, and a kit acting as its admin.</summary>
internal sealed record ModOrg(Guid OrgId, SapKit K, Guid Pkr, string Email);

/// <summary>
/// A37 (QA) — what <see cref="ModuleRegistryE2ETests"/> needs on top of <see cref="SapKit"/>, PreOrder and Rc: an organization on
/// any plan, the org admin's Settings › Modules calls (API-CONTRACT §1.1), the super admin's licence calls (§1.2), the module
/// 403 body (§1.4), and the two things only the database or the host can do — end a grace period now and run the daily
/// grace-expiry job in-process.
/// </summary>
internal static class Mods
{
    public const string Manufacturing = "MODULE_MANUFACTURING";
    public const string Services      = "MODULE_SERVICES";
    public const string Inventory     = "MODULE_INVENTORY";
    public const string Logistics     = "MODULE_LOGISTICS";
    public const string Warehouse     = "MODULE_WAREHOUSE";
    public const string Demand        = "MODULE_DEMAND";
    public const string Suppliers     = "MODULE_SUPPLIERS";
    public const string Customers     = "MODULE_CUSTOMERS";
    public const string Finance       = "MODULE_FINANCE";
    public const string Bom           = "FEATURE_BOM_MANAGEMENT";

    /// <param name="root">The seeded super admin's kit.</param>
    /// <param name="currency">PKR as the organization's base and an org currency (selling and buying need it; a Basic org has no Finance).</param>
    public static async Task<ModOrg> OrgAsync(this SapKit root, string prefix, string plan = "ENTERPRISE", bool currency = true)
    {
        var f = root.F;
        var pkr = currency ? await root.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs") : Guid.Empty;
        var email = $"{prefix.ToLowerInvariant()}-{Guid.NewGuid():N}@a37-qa.test";
        var created = await root.Ok(root.Post("/api/system/organizations", new
        {
            OrgCode = $"M{Guid.NewGuid():N}"[..10].ToUpperInvariant(), OrgName = $"{root.Marker} {prefix} {Guid.NewGuid():N}"[..30],
            Plan = plan, AdminFirstName = "Module", AdminLastName = "Admin", AdminEmail = email
        }), $"create a {plan} organization");
        var orgId = created.G("organizationId");

        await f.SetPasswordAsync(email, "A37Qa@12345!");
        await f.ExecuteAsync("UPDATE auth.UserAccounts SET IsActive = 1 WHERE Email = @e", ("@e", email));
        var k = new SapKit(f, prefix, f.CreateBearerClient(await f.LoginAsync(email, "A37Qa@12345!")));
        if (currency)
        {
            await root.SetOrgBaseCurrencyAsync(orgId, pkr);
            await k.EnsureOrgCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        }
        return new ModOrg(orgId, k, pkr, email);
    }

    // ── Org admin: Settings › Modules ──────────────────────────────────────────

    public static async Task<List<JsonElement>> ModulesAsync(this SapKit k) => (await k.Ok(k.Get("/api/tenant/modules"), "list modules")).Items();

    public static async Task<JsonElement> CardAsync(this SapKit k, string code) => (await k.ModulesAsync()).Single(c => c.S("code") == code);

    public static async Task<JsonElement> FeatureOfAsync(this SapKit k, string module, string feature) =>
        (await k.CardAsync(module)).A("features").Single(f => f.S("code") == feature);

    public static Task<Api> Enable(this SapKit k, string code, string? rowVersion = null) =>
        k.Post($"/api/tenant/modules/{code}/enable", new { rowVersion });

    public static Task<Api> Disable(this SapKit k, string code, int? graceDays = null, string? notes = null, string? rowVersion = null) =>
        k.Post($"/api/tenant/modules/{code}/disable", new { graceDays, notes, rowVersion });

    public static Task<Api> SetFeature(this SapKit k, string module, string feature, bool enabled, string? rowVersion = null) =>
        k.Put($"/api/tenant/modules/{module}/features/{feature}", new { enabled, rowVersion });

    public static async Task<JsonElement> EnableOkAsync(this SapKit k, string code) => await k.Ok(k.Enable(code), $"enable {code}");

    public static async Task<JsonElement> DisableOkAsync(this SapKit k, string code, int? graceDays = 0, string? notes = null) =>
        await k.Ok(k.Disable(code, graceDays, notes), $"disable {code}");

    public static async Task<JsonElement> FeatureOkAsync(this SapKit k, string module, string feature, bool enabled) =>
        await k.Ok(k.SetFeature(module, feature, enabled), $"{feature} {(enabled ? "on" : "off")}");

    public static async Task<List<JsonElement>> HistoryAsync(this SapKit k, string code) =>
        (await k.Ok(k.Get($"/api/tenant/modules/{code}/history"), $"history of {code}")).Items();

    public static async Task<JsonElement> EnabledAsync(this SapKit k, HttpClient? client = null) =>
        await k.Ok(k.Get("/api/tenant/modules/enabled", client), "enabled modules");

    public static List<string> Strings(this JsonElement e, string name) => e.A(name).Select(x => x.GetString()!).ToList();

    // ── Super admin: the licence ───────────────────────────────────────────────

    public static async Task LicenceAsync(this SapKit root, Guid orgId, params (string Code, bool On)[] items) =>
        await root.Ok(root.Put($"/api/system/organizations/{orgId}/features",
            new { features = items.Select(i => new { featureCode = i.Code, isEnabled = i.On }).ToArray() }), "licence change");

    public static async Task<List<JsonElement>> SuperHistoryAsync(this SapKit root, Guid orgId) =>
        (await root.Ok(root.Get($"/api/system/organizations/{orgId}/features/history"), "super-admin feature history")).Items();

    // ── What only the database / the host can do ───────────────────────────────

    public static async Task InvalidateAsync(this SapWebApplicationFactory f, Guid orgId)
    {
        await using var scope = f.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantSnapshotProvider>().Invalidate(orgId);
    }

    /// <summary>Moves the running grace period of <paramref name="codes"/> a minute into the past (the 30 days "passed").</summary>
    public static async Task EndGraceNowAsync(this SapWebApplicationFactory f, Guid orgId, params string[] codes)
    {
        foreach (var code in codes)
        {
            var n = await f.ExecuteAsync(
                "UPDATE f SET GracePeriodEndsAt = DATEADD(MINUTE, -1, SYSUTCDATETIME()) FROM tenant.OrganizationFeatures f " +
                "JOIN tenant.FeatureDefinitions d ON d.Id = f.FeatureDefinitionId " +
                "WHERE f.OrganizationId = @o AND d.FeatureCode = @c AND f.IsEnabled = 0 AND f.GracePeriodEndsAt IS NOT NULL",
                ("@o", orgId), ("@c", code));
            n.Should().Be(1, $"{code} is in grace for the organization");
        }
        await f.InvalidateAsync(orgId);
    }

    public static async Task<DateTime?> GraceEndsAtInDbAsync(this SapWebApplicationFactory f, Guid orgId, string code) =>
        (await f.QueryAsync(
            "SELECT f.GracePeriodEndsAt AS G FROM tenant.OrganizationFeatures f JOIN tenant.FeatureDefinitions d ON d.Id = f.FeatureDefinitionId " +
            "WHERE f.OrganizationId = @o AND d.FeatureCode = @c", ("@o", orgId), ("@c", code))).Single()["G"] as DateTime?;

    /// <summary>REG's daily <c>ModuleGraceExpiryJob</c> (internal), run from a fresh scope as Hangfire would.</summary>
    public static async Task RunGraceExpiryJobAsync(this SapWebApplicationFactory f)
    {
        var type = typeof(SMS.Modules.Tenancy.ITenancyModule).Assembly.GetType("SMS.Modules.Tenancy.Services.ModuleGraceExpiryJob", throwOnError: true)!;
        await using var scope = f.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService(type);
        await (Task)type.GetMethod("RunAsync")!.Invoke(job, null)!;
    }

    // ── The module 403 (API-CONTRACT §1.4) ─────────────────────────────────────

    public static void ShouldBeModuleRefusal(this Api api, string module, bool inGrace, string because)
    {
        api.Status.Should().Be(HttpStatusCode.Forbidden, $"{because} — {api}");
        api.Body.B("success").Should().BeFalse(because);
        api.Body.S("errorCode").Should().Be("MODULE_NOT_LICENSED", because);
        api.Result.S("module").Should().Be(module, because);
        api.Result.IsNull("graceEndsAt").Should().Be(!inGrace, $"{because}: graceEndsAt {(inGrace ? "set" : "null")} — {api}");
        api.Message.Should().EndWith("module is not enabled for your organization.", because);
    }

    public static void ShouldPassTheGate(this Api api, string because) =>
        api.Status.Should().NotBe(HttpStatusCode.Forbidden, $"{because} — {api}");

    /// <summary>A standalone production order for <paramref name="fg"/> (planned at create).</summary>
    public static Task<Api> TryCreateProductionOrder(this SapKit k, Product fg, Warehouse wh, decimal qty) =>
        k.Post("/api/production-orders", new
        {
            ProductUuid = fg.ProductUuid, PlannedQuantity = qty, WarehouseUuid = wh.Uuid,
            RequiredDate = PreOrder.Day(PreOrder.Today.AddDays(7)), Plan = true
        });

    public static object Customer(string name, string type = "COMPANY", string? phone = null, decimal? creditLimit = null) =>
        new { name, customerType = type, phone, creditLimit };
}
