using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace SMS.Integration.Tests.MultiTenancy;

// MT-008: full end-to-end (real HTTP pipeline + real dev DB) cross-org isolation tests. Every
// test in this class shares one MultiTenancyIsolationFixture instance (one Org B, one Org C),
// created once and torn down once — see the fixture for why: xUnit runs [Fact]s within a class
// sequentially by default, so a shared fixture is safe as long as each test either doesn't mutate
// state other tests depend on, or restores it (see the feature-toggle test's finally block).
[Collection("MultiTenancyIsolation")]
public class MultiTenancyIsolationTests : IClassFixture<MultiTenancyIsolationFixture>
{
    private readonly MultiTenancyIsolationFixture _fx;

    public MultiTenancyIsolationTests(MultiTenancyIsolationFixture fx) => _fx = fx;

    // ── Product isolation ──────────────────────────────────────────────────────

    [Fact]
    public async Task Product_CreatedInOrgB_IsNotVisibleFromOrgA()
    {
        var productName = $"MT008-OrgB-Product-{Guid.NewGuid():N}";

        var createResp = await _fx.OrgBClient.PostAsJsonAsync("/api/products", new { name = productName });
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, await createResp.Content.ReadAsStringAsync());

        // Confirm Org B itself can see the product it just created.
        var orgBList = await _fx.OrgBClient.GetFromJsonAsync<JsonElement>(
            "/api/products?pageSize=200", MultiTenancyIsolationFixture.Json);
        var orgBWrapped = orgBList.GetProperty("result");
        orgBWrapped.GetProperty("data").EnumerateArray()
            .Should().Contain(p => p.GetProperty("name").GetString() == productName);

        // Org A must never see it.
        var orgAList = await _fx.OrgAClient.GetFromJsonAsync<JsonElement>(
            "/api/products?pageSize=200", MultiTenancyIsolationFixture.Json);
        var orgAWrapped = orgAList.GetProperty("result");
        orgAWrapped.GetProperty("data").EnumerateArray()
            .Should().NotContain(p => p.GetProperty("name").GetString() == productName);
    }

    // ── Purchase Order isolation ──────────────────────────────────────────────

    [Fact]
    public async Task PurchaseOrder_CreatedInOrgA_IsNotVisibleToOrgB_WhichSeesZeroResults()
    {
        var supplierResp = await _fx.OrgAClient.PostAsJsonAsync("/api/suppliers", new
        {
            supplierName = $"MT008-OrgA-Supplier-{Guid.NewGuid():N}",
            supplierCode = $"A{Guid.NewGuid():N}"[..10]
        });
        supplierResp.StatusCode.Should().Be(HttpStatusCode.OK, await supplierResp.Content.ReadAsStringAsync());
        var supplierId = await MultiTenancyIsolationFixture.ReadResultAsync<Guid>(supplierResp);

        var poResp = await _fx.OrgAClient.PostAsJsonAsync("/api/purchase-orders", new
        {
            supplierId,
            supplierName = "MT008 Org A Supplier",
            lines = new[]
            {
                new { itemDescription = "Test line", quantity = 1, unitPrice = 100 }
            }
        });
        poResp.StatusCode.Should().Be(HttpStatusCode.OK, await poResp.Content.ReadAsStringAsync());

        // Org A must see at least this one PO.
        var orgAList = await _fx.OrgAClient.GetFromJsonAsync<JsonElement>(
            "/api/purchase-orders?pageSize=200", MultiTenancyIsolationFixture.Json);
        orgAList.GetProperty("result").GetProperty("data").GetArrayLength().Should().BeGreaterThan(0);

        // Org B — a brand-new org that has never created a PO — must see exactly zero.
        var orgBList = await _fx.OrgBClient.GetFromJsonAsync<JsonElement>(
            "/api/purchase-orders?pageSize=200", MultiTenancyIsolationFixture.Json);
        orgBList.GetProperty("result").GetProperty("data").GetArrayLength().Should().Be(0);
        orgBList.GetProperty("result").GetProperty("totalRecords").GetInt32().Should().Be(0);
    }

    // ── Feature gating ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DisablingModuleFinance_ForOrgB_Returns403OnSupplierPayments()
    {
        // Sanity: Finance is enabled by default (ENTERPRISE plan) — confirm 200 before disabling.
        var before = await _fx.OrgBClient.GetAsync("/api/supplier-payments");
        before.StatusCode.Should().Be(HttpStatusCode.OK);

        var toggleOff = await _fx.SuperAdminClient.PutAsJsonAsync(
            $"/api/system/organizations/{_fx.OrgBId}/features",
            new { features = new[] { new { featureCode = "MODULE_FINANCE", isEnabled = false } } });
        toggleOff.StatusCode.Should().Be(HttpStatusCode.OK, await toggleOff.Content.ReadAsStringAsync());

        try
        {
            var during = await _fx.OrgBClient.GetAsync("/api/supplier-payments");
            during.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            // Restore so no other test in this class is affected by execution order.
            await _fx.SuperAdminClient.PutAsJsonAsync(
                $"/api/system/organizations/{_fx.OrgBId}/features",
                new { features = new[] { new { featureCode = "MODULE_FINANCE", isEnabled = true } } });
        }

        var after = await _fx.OrgBClient.GetAsync("/api/supplier-payments");
        after.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Super-Admin-only endpoint ──────────────────────────────────────────────

    [Fact]
    public async Task OrgAdmin_CannotAccessSystemOrganizations_SuperAdminOnly()
    {
        var response = await _fx.OrgBClient.GetAsync("/api/system/organizations");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SuperAdmin_CanAccessSystemOrganizations()
    {
        var response = await _fx.SuperAdminClient.GetAsync("/api/system/organizations");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Concurrency ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentOperations_InOrgAAndOrgB_DoNotInterfereWithEachOthersData()
    {
        const int countPerOrg = 5;
        var orgATag = $"CA{Guid.NewGuid():N}"[..9];
        var orgBTag = $"CB{Guid.NewGuid():N}"[..9];

        var orgATasks = Enumerable.Range(0, countPerOrg).Select(i =>
            _fx.OrgAClient.PostAsJsonAsync("/api/suppliers", new
            {
                supplierName = $"{orgATag}-{i}",
                supplierCode = $"{orgATag}{i}"
            }));
        var orgBTasks = Enumerable.Range(0, countPerOrg).Select(i =>
            _fx.OrgBClient.PostAsJsonAsync("/api/suppliers", new
            {
                supplierName = $"{orgBTag}-{i}",
                supplierCode = $"{orgBTag}{i}"
            }));

        var allResponses = await Task.WhenAll(orgATasks.Concat(orgBTasks));
        allResponses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);

        var orgASuppliers = await _fx.OrgAClient.GetFromJsonAsync<JsonElement>(
            "/api/suppliers?pageSize=500", MultiTenancyIsolationFixture.Json);
        var orgANames = orgASuppliers.GetProperty("result").GetProperty("data")
            .EnumerateArray().Select(s => s.GetProperty("supplierName").GetString()).ToList();

        var orgBSuppliers = await _fx.OrgBClient.GetFromJsonAsync<JsonElement>(
            "/api/suppliers?pageSize=500", MultiTenancyIsolationFixture.Json);
        var orgBNames = orgBSuppliers.GetProperty("result").GetProperty("data")
            .EnumerateArray().Select(s => s.GetProperty("supplierName").GetString()).ToList();

        // Org A sees exactly its own 5 concurrently-created suppliers, none of Org B's.
        for (var i = 0; i < countPerOrg; i++)
        {
            orgANames.Should().Contain($"{orgATag}-{i}");
            orgANames.Should().NotContain($"{orgBTag}-{i}");
        }

        // And vice versa.
        for (var i = 0; i < countPerOrg; i++)
        {
            orgBNames.Should().Contain($"{orgBTag}-{i}");
            orgBNames.Should().NotContain($"{orgATag}-{i}");
        }
    }

    // ── Org deactivation ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeactivatingOrg_ImmediatelyPreventsItsUsersFromAuthenticating()
    {
        // Org C is dedicated to this one destructive test — never touched by any other test in
        // this class, so deactivating it can't affect anything else running against the fixture.
        var beforeResp = await _fx.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = _fx.OrgCAdminEmail, password = "MtEight@12345" });
        beforeResp.StatusCode.Should().Be(HttpStatusCode.OK, "the org is still active at this point");

        var deactivateResp = await _fx.SuperAdminClient.PatchAsync(
            $"/api/system/organizations/{_fx.OrgCId}/deactivate", content: null);
        deactivateResp.StatusCode.Should().Be(HttpStatusCode.OK, await deactivateResp.Content.ReadAsStringAsync());

        var afterResp = await _fx.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = _fx.OrgCAdminEmail, password = "MtEight@12345" });
        afterResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Role isolation ────────────────────────────────────────────────────────
    //
    // Role is now either global (IsGlobal=true, OrganizationId=null — the shared seeded catalog:
    // System Admin, Procurement Manager, etc., usable/assignable by every organization) or
    // org-owned (IsGlobal=false, OrganizationId set — created by that org's own Org Admin via
    // POST /api/roles, invisible to and unmodifiable by any other organization). This closes the
    // gap the previous version of this test documented ("Org A's roles do not appear in Org B's
    // role list" was not true when Role/RolePermission had no query filter at all).
    [Fact]
    public async Task CustomRole_CreatedInOrgB_IsNotVisibleFromOrgA_ButTheSharedCatalogStaysVisibleToBoth()
    {
        var roleCode = $"MT008ORGBROLE{Guid.NewGuid():N}"[..20];

        var createResp = await _fx.OrgBClient.PostAsJsonAsync("/api/roles", new
        {
            name = "MT-008 Org B Custom Role",
            roleCode
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created, await createResp.Content.ReadAsStringAsync());

        // Org B (who created it) sees its own custom role.
        var orgBRoles = await _fx.OrgBClient.GetFromJsonAsync<JsonElement>("/api/roles", MultiTenancyIsolationFixture.Json);
        orgBRoles.GetProperty("result").EnumerateArray()
            .Should().Contain(r => r.GetProperty("roleCode").GetString() == roleCode);

        // Org A must never see Org B's custom role, but the shared global catalog remains visible.
        var orgARoles = await _fx.OrgAClient.GetFromJsonAsync<JsonElement>("/api/roles", MultiTenancyIsolationFixture.Json);
        var orgARoleCodes = orgARoles.GetProperty("result").EnumerateArray()
            .Select(r => r.GetProperty("roleCode").GetString()).ToList();

        orgARoleCodes.Should().NotContain(roleCode);
        orgARoleCodes.Should().Contain("PROCUREMENT_MANAGER");
    }

    // ── Workflow isolation ──────────────────────────────────────────────────────
    //
    // Approver-resolution isolation itself (a Supervisor step in Org A never resolving to an Org B
    // user) is already thoroughly unit-tested against UserQueryService/OrgChartService directly
    // (SMS.Modules.Auth.Tests.ApproverLookupTenantScopingTests, MT-006). This test covers the
    // remaining, not-yet-covered layer: the real HTTP endpoint for workflow definitions is
    // correctly scoped, and each org's auto-seeded MIR/PR/PO workflow definitions are genuinely
    // separate rows, not shared.
    [Fact]
    public async Task WorkflowDefinitions_AreSeededSeparatelyPerOrg_AndIsolatedThroughTheApi()
    {
        var orgADefs = await _fx.OrgAClient.GetFromJsonAsync<JsonElement>(
            "/api/workflow/definitions?interfaceCode=PR&pageSize=50", MultiTenancyIsolationFixture.Json);
        var orgBDefs = await _fx.OrgBClient.GetFromJsonAsync<JsonElement>(
            "/api/workflow/definitions?interfaceCode=PR&pageSize=50", MultiTenancyIsolationFixture.Json);

        var orgAUuids = orgADefs.GetProperty("result").GetProperty("data")
            .EnumerateArray().Select(d => d.GetProperty("uuid").GetGuid()).ToList();
        var orgBUuids = orgBDefs.GetProperty("result").GetProperty("data")
            .EnumerateArray().Select(d => d.GetProperty("uuid").GetGuid()).ToList();

        orgAUuids.Should().NotBeEmpty("SCM-DEMO has a seeded PR workflow definition");
        orgBUuids.Should().NotBeEmpty("Org B should have its own auto-seeded PR workflow definition (MT-006)");
        orgAUuids.Should().NotIntersectWith(orgBUuids, "each org must have its own distinct workflow definition rows");
    }
}
