using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using SMS.Modules.Integration.Core.Providers;
using SMS.Shared.Integration.QuickBooks;
using Xunit;
using static SMS.Integration.Tests.FinancialPosting.Fp;

namespace SMS.Integration.Tests.FinancialPosting;

/// <summary>
/// A44 (QA, D-25 / D-3) — Suppliers and Inventory now publish masters through <c>IEntitySyncGateway</c> → CORE's router →
/// QBO's QuickBooks entity target. In the real host (not the Null gateway) a customer save, a supplier save, a variant save and a
/// variant hard delete must still reach QuickBooks (fake Intuit) as before, and the payload JSON + fingerprint the new path stores
/// must be byte-identical to what the pre-A44 path (the <c>IQuickBooksSource</c> pull-back behind "Push now", which still calls
/// <c>IQuickBooksGateway</c> directly) stores — so nothing already in QuickBooks is re-sent.
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~EntitySyncE2ETests</c>.</para>
/// </summary>
public sealed class EntitySyncE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public EntitySyncE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A44");
    }

    private sealed record Stored(string State, string? RemoteId, string? Payload, string? Fingerprint, string? LastPushed);

    private async Task<Stored> StoredAsync(Guid org, Guid externalId, string kind)
    {
        var r = (await _f.QueryAsync(
            "SELECT m.State, m.RemoteId, m.PayloadJson, m.PayloadFingerprint, m.LastPushedFingerprint FROM integration.EntityMaps m " +
            "JOIN integration.Connections c ON c.Id = m.ConnectionId WHERE c.OrganizationId = @o AND m.ExternalId = @e AND m.Kind = @k",
            ("@o", org), ("@e", externalId.ToString()), ("@k", kind))).Should().ContainSingle($"one {kind} map for {externalId}").Subject;
        return new Stored((string)r["State"]!, r["RemoteId"] as string, r["PayloadJson"] as string, r["PayloadFingerprint"] as string,
            r["LastPushedFingerprint"] as string);
    }

    [Fact]
    public async Task Partner_and_variant_saves_and_a_hard_delete_reach_QuickBooks_with_pre_A44_payloads()
    {
        var o = await (await _root.NewOrgAsync("ENT")).ConnectLiveAsync();
        var k = o.K;
        // masters go when saved, not only when referenced
        await k.Ok(k.Put($"{Qb}/settings", new
        {
            DefaultIncomeAccountId = "1", DefaultExpenseAccountId = "3", FreightExpenseAccountId = "4", DiscountAccountId = "2",
            DefaultPurchaseTaxCodeId = "NON", ItemTypeDefault = "NonInventory", PartnerScope = "AllActive"
        }), "partner scope AllActive");

        // ── creates through SCM's own screens ────────────────────────────────────────
        var customer = await k.CreateCustomerAsync("ENT Customer");
        var vendor = await k.CreateVendorAsync("ENT Vendor");
        var created = await k.Ok(k.Post("/api/products", new
        {
            Name = k.Next("ENT Product"), UomCode = "PCS",
            Variants = new[]
            {
                new { VariantName = "Red", PurchasePrice = 10m, SellingPrice = (decimal?)15m, IsDefault = true, IsAvailableForRetail = true },
                new { VariantName = "Blue", PurchasePrice = 10m, SellingPrice = (decimal?)16m, IsDefault = false, IsAvailableForRetail = true }
            }
        }), "product with two variants");
        var variants = (await k.Ok(k.Get($"/api/products/{created.I("id")}"), "read product")).A("variants");
        var red = variants.Single(v => v.S("variantName") == "Red").G("uuid");
        var blue = variants.Single(v => v.S("variantName") == "Blue").G("uuid");

        _f.Company.ResetCalls();
        await _f.RunJobsAsync(3);
        var cMap = await StoredAsync(o.OrgId, customer.Uuid, "Customer");
        var vMap = await StoredAsync(o.OrgId, vendor.Uuid, "Vendor");
        var rMap = await StoredAsync(o.OrgId, red, "Item");
        var bMap = await StoredAsync(o.OrgId, blue, "Item");
        foreach (var (m, what) in new[] { (cMap, "customer"), (vMap, "supplier"), (rMap, "variant Red"), (bMap, "variant Blue") })
        {
            m.State.Should().Be("Synced", $"the {what} save reached QuickBooks through IEntitySyncGateway (not the Null gateway)");
            m.RemoteId.Should().NotBeNullOrEmpty(what);
        }
        _f.Company.Writes.Where(w => w.Operation == "Create").Select(w => w.Kind)
            .Should().BeEquivalentTo([SyncKind.Customer, SyncKind.Vendor, SyncKind.Item, SyncKind.Item]);

        // ── the pre-A44 path (IQuickBooksSource pull-back → IQuickBooksGateway) builds the same payload: nothing re-sent ──
        async Task PushNowAsync(string kind, params Guid[] ids) =>
            await k.Ok(k.Post($"{Qb}/sync/push", new { Kind = kind, ExternalIds = ids.Select(i => i.ToString()).ToArray() }), $"push now {kind}");
        _f.Company.ResetCalls();
        await PushNowAsync("Customer", customer.Uuid);
        await PushNowAsync("Vendor", vendor.Uuid);
        await PushNowAsync("Item", red, blue);
        await _f.RunJobsAsync(2);
        await _f.RunReconciliationAsync();
        await _f.RunJobsAsync(2);
        _f.Company.Writes.Should().BeEmpty("the new path's payload and fingerprint equal the pre-A44 path's — nothing is re-sent");
        foreach (var (before, id, kind) in new[] { (cMap, customer.Uuid, "Customer"), (vMap, vendor.Uuid, "Vendor"), (rMap, red, "Item"), (bMap, blue, "Item") })
        {
            var after = await StoredAsync(o.OrgId, id, kind);
            after.Payload.Should().Be(before.Payload, $"{kind} {id}: payload JSON byte-identical between the two paths");
            after.Fingerprint.Should().Be(before.Fingerprint, $"{kind} {id}: fingerprint unchanged");
            after.LastPushed.Should().Be(before.LastPushed);
        }

        // ── edits through SCM's screens: one sparse update each ───────────────────────
        _f.Company.ResetCalls();
        var partner = await k.Ok(k.Get($"/api/partners/{customer.Uuid}"), "read customer");
        var put = await k.Put($"/api/partners/{customer.Uuid}", MergeName(partner, "companyName", $"{customer.Name} Renamed"));
        put.Status.Should().Be(HttpStatusCode.OK, put.ToString());
        await k.Ok(k.Patch($"/api/suppliers/{vendor.Uuid}", new { SupplierName = $"{vendor.Name} Renamed" }), "rename supplier");
        await k.Ok(k.Patch($"/api/variants/{red}", new { VariantName = "Crimson", PurchasePrice = 10m, SellingPrice = (decimal?)17m, IsDefault = true,
            IsAvailableForRetail = true }), "edit variant");
        await _f.RunJobsAsync(3);
        var updates = _f.Company.Writes.Where(w => w.Operation == "Update").ToList();
        updates.Should().Contain(w => w.Kind == SyncKind.Customer && w.Name == $"{customer.Name} Renamed");
        updates.Should().Contain(w => w.Kind == SyncKind.Vendor && w.Name == $"{vendor.Name} Renamed");
        updates.Should().Contain(w => w.Kind == SyncKind.Item && w.Entity is RemoteItem && ((RemoteItem)w.Entity).UnitPrice == 17m);
        _f.Company.Writes.Should().NotContain(w => w.Operation == "Create", "edits update the existing records");

        // the edited maps also equal what the pre-A44 path builds
        var cEdited = await StoredAsync(o.OrgId, customer.Uuid, "Customer");
        _f.Company.ResetCalls();
        await PushNowAsync("Customer", customer.Uuid);
        await PushNowAsync("Item", red);
        await _f.RunJobsAsync(2);
        _f.Company.Writes.Should().BeEmpty();
        (await StoredAsync(o.OrgId, customer.Uuid, "Customer")).Fingerprint.Should().Be(cEdited.Fingerprint);

        // ── a variant hard delete retires the item in QuickBooks ──────────────────────
        _f.Company.ResetCalls();
        var deleted = await k.Ok(k.Delete($"/api/variants/{blue}"), "delete the untransacted variant");
        deleted.B("softDeleted").Should().BeFalse("never transacted → hard delete");
        await _f.RunJobsAsync(3);
        _f.Company.Writes.Should().Contain(w => w.Operation == "Update" && w.Kind == SyncKind.Item && w.Entity is RemoteItem
                                                && !((RemoteItem)w.Entity).Active, "the hard-deleted variant's item is made inactive");
        var retired = _f.Company.Writes.Last(w => w.Kind == SyncKind.Item && w.Entity is RemoteItem { Active: false });
        _f.Company.Writes.Where(w => w.Kind == SyncKind.Item && w.Entity is RemoteItem { Active: false })
            .Should().HaveCount(1, "only the deleted variant is retired; the remaining one stays active");
        (await StoredAsync(o.OrgId, blue, "Item")).RemoteId.Should().Be(bMap.RemoteId);
        _ = retired;
    }

    /// <summary>The partner as read, with one property replaced (PUT rewrites the whole partner).</summary>
    private static Dictionary<string, object?> MergeName(System.Text.Json.JsonElement partner, string property, string value)
    {
        var d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(partner.GetRawText())!;
        var key = d.Keys.Single(x => string.Equals(x, property, StringComparison.OrdinalIgnoreCase));
        d[key] = value;
        return d;
    }
}
