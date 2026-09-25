using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using SMS.Integration.Tests.ProcurementCycle.Infrastructure;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Integration.Tests.SaleOrderSettings;

// The requests the Sale Order Settings screen makes, over real HTTP against the real Program.cs
// pipeline and a throwaway LocalDB database. The screen's models are written by hand in TypeScript,
// so the JSON these endpoints return is what they have to match, and only a real host proves that
// the controller's dependencies resolve and that the permission split (anyone with the read
// permission sees it, only the Supply Department Administrator saves it) holds.
public sealed class SaleOrderSettingsEndpointTests : IClassFixture<ProcurementCycleWebApplicationFactory>
{
    private const string Config = "/api/sale-order-config";

    private readonly ProcurementCycleWebApplicationFactory _factory;
    private readonly HttpClient _admin;

    public SaleOrderSettingsEndpointTests(ProcurementCycleWebApplicationFactory factory)
    {
        _factory = factory;
        _admin   = factory.CreateAdminClient();
    }

    // The tests share one database, so only the save test, which is the only one that writes, asserts
    // the defaults; the others check the shape of what they read.
    [Fact]
    public async Task The_policy_is_read_with_the_names_and_types_the_screen_models()
    {
        var config = await GetResultAsync(_admin, Config);

        config.GetProperty("uuid").GetGuid().Should().NotBeEmpty();
        foreach (var flag in new[]
                 {
                     "autoPoEnabled", "dropShipEnabled", "selfPickupEnabled", "partialFulfillmentAllowed",
                     "emailIntimationEnabled", "shipmentRequiredDefault"
                 })
            config.GetProperty(flag).ValueKind.Should().BeOneOf(new[] { JsonValueKind.True, JsonValueKind.False }, flag);
        foreach (var text in new[] { "supplierSelectionMode", "autoPoApprovalMode", "defaultFulfillmentMode" })
            config.GetProperty(text).ValueKind.Should().Be(JsonValueKind.String, text);
        config.GetProperty("reservationTtlHours").ValueKind.Should().Be(JsonValueKind.Number);

        // Reading again finds the same single row, not a second one.
        (await GetResultAsync(_admin, Config)).GetProperty("uuid").GetGuid().Should().Be(config.GetProperty("uuid").GetGuid());
    }

    [Fact]
    public async Task A_system_administrator_can_read_the_policy_but_not_change_it()
    {
        var before = (await GetResultAsync(_admin, Config)).GetRawText();

        var put = await _admin.PutAsJsonAsync(Config, Policy(reservationTtlHours: 5));

        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetResultAsync(_admin, Config)).GetRawText().Should().Be(before);
    }

    [Fact]
    public async Task The_departments_to_choose_from_come_by_name_with_whether_anyone_heads_them()
    {
        var marker = Guid.NewGuid().ToString("N")[..6];
        await InsertDepartmentAsync($"Zeta {marker}", "ZT", headUserId: null);
        await InsertDepartmentAsync($"Alpha {marker}", null, headUserId: _factory.AdminUserId);

        var departments = (await GetResultAsync(_admin, $"{Config}/departments"))
            .EnumerateArray().Where(d => d.GetProperty("name").GetString()!.EndsWith(marker)).ToList();

        departments.Select(d => d.GetProperty("name").GetString()).Should().Equal($"Alpha {marker}", $"Zeta {marker}");
        departments[0].GetProperty("hasHead").GetBoolean().Should().BeTrue();
        IsNullOrAbsent(departments[0], "code").Should().BeTrue();
        departments[1].GetProperty("hasHead").GetBoolean().Should().BeFalse();
        departments[1].GetProperty("code").GetString().Should().Be("ZT");
        departments.Should().OnlyContain(d => d.GetProperty("departmentId").GetInt32() > 0);
    }

    [Fact]
    public async Task The_supply_department_administrator_saves_the_whole_policy_and_every_change_is_recorded()
    {
        var supply = await SupplyDeptAdminClientAsync();
        var departmentId = await InsertDepartmentAsync("Supply " + Guid.NewGuid().ToString("N")[..6], "SUP", _factory.AdminUserId);

        // The starting point, before anything has been saved: what a new organization gets.
        var defaults = await GetResultAsync(_admin, Config);
        defaults.GetProperty("autoPoEnabled").GetBoolean().Should().BeTrue();
        defaults.GetProperty("supplierSelectionMode").GetString().Should().Be("BEST_MATCH");
        defaults.GetProperty("autoPoApprovalMode").GetString().Should().Be("REQUIRE_WORKFLOW");
        defaults.GetProperty("dropShipEnabled").GetBoolean().Should().BeFalse();
        defaults.GetProperty("selfPickupEnabled").GetBoolean().Should().BeTrue();
        defaults.GetProperty("defaultFulfillmentMode").GetString().Should().Be("IN_STOCK");
        defaults.GetProperty("reservationTtlHours").GetInt32().Should().Be(72);
        defaults.GetProperty("partialFulfillmentAllowed").GetBoolean().Should().BeTrue();
        defaults.GetProperty("emailIntimationEnabled").GetBoolean().Should().BeTrue();
        defaults.GetProperty("shipmentRequiredDefault").GetBoolean().Should().BeTrue();
        IsNullOrAbsent(defaults, "intimationDepartmentId").Should().BeTrue();
        IsNullOrAbsent(defaults, "intimationCcEmails").Should().BeTrue();
        IsNullOrAbsent(defaults, "updatedAt").Should().BeTrue();

        // Exactly what the screen sends after every setting has been changed that can change together:
        // customer pickup stays on, because new orders no longer starting as shipped needs it.
        var put = await supply.PutAsJsonAsync(Config, Policy(
            autoPoEnabled: false, supplierSelectionMode: "MANUAL", autoPoApprovalMode: "DRAFT_ONLY",
            dropShipEnabled: true, selfPickupEnabled: true, defaultFulfillmentMode: "BACK_TO_BACK",
            reservationTtlHours: 96, partialFulfillmentAllowed: false, emailIntimationEnabled: false,
            intimationDepartmentId: departmentId, intimationCcEmails: "a@x.com, b@x.com", shipmentRequiredDefault: false));

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var saved = await ResultAsync(put);
        saved.GetProperty("supplierSelectionMode").GetString().Should().Be("MANUAL");
        saved.GetProperty("intimationDepartmentId").GetInt32().Should().Be(departmentId);
        saved.GetProperty("intimationCcEmails").GetString().Should().Be("a@x.com, b@x.com");
        saved.GetProperty("updatedAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));

        // What was saved is what is read back, by anyone who may read it.
        var read = await GetResultAsync(_admin, Config);
        read.GetProperty("autoPoEnabled").GetBoolean().Should().BeFalse();
        read.GetProperty("reservationTtlHours").GetInt32().Should().Be(96);
        read.GetProperty("defaultFulfillmentMode").GetString().Should().Be("BACK_TO_BACK");
        read.GetProperty("shipmentRequiredDefault").GetBoolean().Should().BeFalse();

        // Eleven settings changed, so eleven entries, each saying who and what, in the text the screen words.
        var audit = await AuditAsync();
        audit.Should().HaveCount(11);
        audit.Select(a => a.GetProperty("fieldChanged").GetString()).Should().BeEquivalentTo(
            "AutoPoEnabled", "SupplierSelectionMode", "AutoPoApprovalMode", "DropShipEnabled",
            "DefaultFulfillmentMode", "ReservationTtlHours", "PartialFulfillmentAllowed", "EmailIntimationEnabled",
            "IntimationDepartmentId", "IntimationCcEmails", "ShipmentRequiredDefault");
        audit.Should().OnlyContain(a => a.GetProperty("changedBy").GetInt32() == _factory.AdminUserId);
        audit.Should().OnlyContain(a => !string.IsNullOrWhiteSpace(a.GetProperty("changedByName").GetString()));
        audit.Should().OnlyContain(a => a.GetProperty("changedAt").GetDateTime() > DateTime.UtcNow.AddMinutes(-5));

        Entry(audit, "AutoPoEnabled").Should().Be(("True", "False"));
        Entry(audit, "SupplierSelectionMode").Should().Be(("BEST_MATCH", "MANUAL"));
        Entry(audit, "ReservationTtlHours").Should().Be(("72", "96"));
        Entry(audit, "IntimationDepartmentId").Should().Be((null, departmentId.ToString()));
        Entry(audit, "IntimationCcEmails").Should().Be((null, "a@x.com, b@x.com"));

        // Saving what is already saved records nothing.
        var again = await supply.PutAsJsonAsync(Config, Policy(
            autoPoEnabled: false, supplierSelectionMode: "MANUAL", autoPoApprovalMode: "DRAFT_ONLY",
            dropShipEnabled: true, selfPickupEnabled: true, defaultFulfillmentMode: "BACK_TO_BACK",
            reservationTtlHours: 96, partialFulfillmentAllowed: false, emailIntimationEnabled: false,
            intimationDepartmentId: departmentId, intimationCcEmails: "a@x.com, b@x.com", shipmentRequiredDefault: false));
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AuditAsync()).Should().HaveCount(11);

        // A policy that cannot work is refused by the server itself, whatever sent it, saying why.
        var refusals = new (string Why, object Body, string Says)[]
        {
            ("unknown supplier mode", Policy(supplierSelectionMode: "CHEAPEST"), "not a supplier selection mode"),
            ("unknown approval mode", Policy(autoPoApprovalMode: "SKIP"), "not a purchase order approval mode"),
            ("no hold", Policy(reservationTtlHours: 0), "from 1 to 8760"),
            ("hold over a year", Policy(reservationTtlHours: 9000), "from 1 to 8760"),
            ("bad copy address", Policy(intimationCcEmails: "good@x.com, nope"), "nope"),
            ("drop ship default with it off", Policy(defaultFulfillmentMode: "DROP_SHIP", dropShipEnabled: false), "drop shipping is switched off"),
            ("pickup start with pickup off", Policy(shipmentRequiredDefault: false, selfPickupEnabled: false), "customer pickup is switched off"),
            ("department that does not exist", Policy(intimationDepartmentId: 987654), "department does not exist"),
        };
        foreach (var (why, body, says) in refusals)
        {
            var refused = await supply.PutAsJsonAsync(Config, body);
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, why);
            (await refused.Content.ReadAsStringAsync()).Should().Contain(says, why);
        }
        (await AuditAsync()).Should().HaveCount(11, "a refused save records nothing");
        (await GetResultAsync(_admin, Config)).GetProperty("reservationTtlHours").GetInt32().Should().Be(96);

        // Clearing the department and the copy list sends null, which the server records as none.
        var cleared = await supply.PutAsJsonAsync(Config, Policy(
            autoPoEnabled: false, supplierSelectionMode: "MANUAL", autoPoApprovalMode: "DRAFT_ONLY",
            dropShipEnabled: true, selfPickupEnabled: true, defaultFulfillmentMode: "BACK_TO_BACK",
            reservationTtlHours: 96, partialFulfillmentAllowed: false, emailIntimationEnabled: false,
            intimationDepartmentId: null, intimationCcEmails: null, shipmentRequiredDefault: false));
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await AuditAsync();
        after.Should().HaveCount(13);
        Entry(after, "IntimationDepartmentId", newest: true).Should().Be((departmentId.ToString(), null));
        Entry(after, "IntimationCcEmails", newest: true).Should().Be(("a@x.com, b@x.com", null));
        IsNullOrAbsent((await GetResultAsync(_admin, Config)), "intimationDepartmentId").Should().BeTrue();

        // What the sale order form opens on follows the settings: new orders no longer start as shipped.
        var starts = await GetResultAsync(_admin, "/api/sale-orders/defaults");
        starts.GetProperty("deliveryMode").GetString().Should().Be("SELF_PICKUP");
        starts.GetProperty("selfPickupEnabled").GetBoolean().Should().BeTrue();

        // Switching customer pickup off (and new orders back to shipped, which it needs): the form is told,
        // and an order for collection is refused before anything else about it is looked at.
        var pickupOff = await supply.PutAsJsonAsync(Config, Policy(
            autoPoEnabled: false, supplierSelectionMode: "MANUAL", autoPoApprovalMode: "DRAFT_ONLY",
            dropShipEnabled: true, selfPickupEnabled: false, defaultFulfillmentMode: "BACK_TO_BACK",
            reservationTtlHours: 96, partialFulfillmentAllowed: false, emailIntimationEnabled: false,
            shipmentRequiredDefault: true));
        pickupOff.StatusCode.Should().Be(HttpStatusCode.OK, await pickupOff.Content.ReadAsStringAsync());

        var now = await GetResultAsync(_admin, "/api/sale-orders/defaults");
        now.GetProperty("deliveryMode").GetString().Should().Be("SHIP");
        now.GetProperty("selfPickupEnabled").GetBoolean().Should().BeFalse();

        var collected = new
        {
            partnerId = Guid.NewGuid(), currencyId = Guid.NewGuid(), deliveryMode = "SELF_PICKUP",
            lines = new[] { new { variantUuid = Guid.NewGuid(), quantity = 1 } }
        };
        var refusedOrder = await _admin.PostAsJsonAsync("/api/sale-orders", collected);
        refusedOrder.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refusedOrder.Content.ReadAsStringAsync()).Should().Contain("Customer pickup is switched off");

        // With pickup back on, the same order gets past that rule and stops at the next one (no
        // channel availability for an item that does not exist), which shows it was the setting,
        // and only the setting, refusing it the first time.
        await supply.PutAsJsonAsync(Config, Policy(
            autoPoEnabled: false, supplierSelectionMode: "MANUAL", autoPoApprovalMode: "DRAFT_ONLY",
            dropShipEnabled: true, selfPickupEnabled: true, defaultFulfillmentMode: "BACK_TO_BACK",
            reservationTtlHours: 96, partialFulfillmentAllowed: false, emailIntimationEnabled: false,
            shipmentRequiredDefault: true));
        var later = await _admin.PostAsJsonAsync("/api/sale-orders", collected);
        later.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await later.Content.ReadAsStringAsync()).Should().NotContain("Customer pickup is switched off").And.Contain("not available for retail sale");
    }

    [Fact]
    public async Task The_history_comes_in_pages_with_the_totals_the_screen_pages_by()
    {
        var page = await GetResultAsync(_admin, $"{Config}/audit?page=1&pageSize=20");

        page.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Array);
        page.GetProperty("totalRecords").GetInt32().Should().BeGreaterOrEqualTo(0);
        page.GetProperty("page").GetInt32().Should().Be(1);
        page.GetProperty("pageSize").GetInt32().Should().Be(20);
        page.TryGetProperty("totalPages", out _).Should().BeTrue();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static object Policy(
        bool autoPoEnabled = true, string supplierSelectionMode = "BEST_MATCH", string autoPoApprovalMode = "REQUIRE_WORKFLOW",
        bool dropShipEnabled = false, bool selfPickupEnabled = true, string defaultFulfillmentMode = "IN_STOCK",
        int reservationTtlHours = 72, bool partialFulfillmentAllowed = true, bool emailIntimationEnabled = true,
        int? intimationDepartmentId = null, string? intimationCcEmails = null, bool shipmentRequiredDefault = true) => new
    {
        autoPoEnabled, supplierSelectionMode, autoPoApprovalMode, dropShipEnabled, selfPickupEnabled,
        defaultFulfillmentMode, reservationTtlHours, partialFulfillmentAllowed, emailIntimationEnabled,
        intimationDepartmentId, intimationCcEmails, shipmentRequiredDefault
    };

    private static bool IsNullOrAbsent(JsonElement element, string property) =>
        !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null;

    private static async Task<JsonElement> ResultAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(json);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue(json);
        return doc.RootElement.GetProperty("result").Clone();
    }

    private static async Task<JsonElement> GetResultAsync(HttpClient client, string url) =>
        await ResultAsync(await client.GetAsync(url));

    /// <summary>Every history entry, newest first as the server returns them.</summary>
    private async Task<List<JsonElement>> AuditAsync() =>
        (await GetResultAsync(_admin, $"{Config}/audit?page=1&pageSize=200")).GetProperty("data").EnumerateArray().ToList();

    private static (string? Old, string? New) Entry(List<JsonElement> audit, string field, bool newest = false)
    {
        var rows = audit.Where(a => a.GetProperty("fieldChanged").GetString() == field);
        var row = newest ? rows.First() : rows.Last();
        string? Text(string name) => IsNullOrAbsent(row, name) ? null : row.GetProperty(name).GetString();
        return (Text("oldValue"), Text("newValue"));
    }

    private Task<int> InsertDepartmentAsync(string name, string? code, int? headUserId) =>
        _factory.QueryScalarAsync<int>(
            "INSERT INTO auth.Departments (Name, Code, HeadUserId, OrganizationId) VALUES (@n, @c, @h, @o); " +
            "SELECT CAST(SCOPE_IDENTITY() AS int)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@n", name);
                cmd.Parameters.AddWithValue("@c", (object?)code ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@h", (object?)headUserId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@o", TenantDefaults.ScmDemoOrganizationId);
            });

    /// <summary>
    /// The seeded administrator, made a Supply Department Administrator in the throwaway database and
    /// logged in again, since what a user may do travels in the token.
    /// </summary>
    private async Task<HttpClient> SupplyDeptAdminClientAsync()
    {
        await _factory.QueryScalarAsync<int>(
            "UPDATE auth.UserAccounts SET RoleID = @r WHERE UserID = @u; SELECT @@ROWCOUNT",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@r", (int)EnumRole.SupplyDeptAdmin);
                cmd.Parameters.AddWithValue("@u", _factory.AdminUserId);
            });

        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { Email = "admin@sms.local", Password = "Admin@12345" });
        var body = await login.Content.ReadAsStringAsync();
        login.IsSuccessStatusCode.Should().BeTrue(body);
        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("result").GetProperty("accessToken").GetString()!;

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
