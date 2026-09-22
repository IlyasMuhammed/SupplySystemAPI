using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using SMS.Integration.Tests.ProcurementCycle.Infrastructure;
using SMS.Modules.Demand.Models;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Material.Models;
using SMS.Modules.Warehouse.Models;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Integration.Tests.MaterialIssue;

// Material issue vouchers against the shared reservation ledger.
//
// Approving a material issue request holds its stock in the shared ledger (inventory.StockReservations),
// not in the material schema's own table. A voucher has to read that hold when it is created, and consume
// its share of it, in the same transaction as the stock movement, when it is posted. Both once failed for
// every request approved since the move ("no active stock reservation found", then "the connection is
// already in a transaction"), and nothing but the full procurement cycle would have said so.
//
// Real HTTP, real Program.cs pipeline, a throwaway LocalDB database: nothing in the code under test is faked.
public sealed class MaterialIssueVoucherTests : IClassFixture<ProcurementCycleWebApplicationFactory>
{
    private readonly ProcurementCycleWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MaterialIssueVoucherTests(ProcurementCycleWebApplicationFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateAdminClient();
    }

    [Fact]
    public async Task A_request_is_issued_in_parts_and_each_voucher_takes_only_its_share_of_the_hold()
    {
        var stocked = await StockAsync(quantity: 30m);
        var mirUuid = await ApprovedRequestAsync(stocked, quantity: 5m);
        var mirLine = (await GetAsync<MirDetailModel>($"/api/material-issue-requests/{mirUuid}")).Lines.Single().UUID;

        (await StockOfAsync(stocked)).Should().Be((OnHand: 30m, Reserved: 5m));
        (await ActiveHoldsAsync(mirUuid)).Should().Be(5m);

        // First voucher: 2 of the 5. The stock leaves, and only 2 of the hold with it.
        var first = await CreateVoucherAsync(mirUuid, mirLine, quantity: 2m);
        (await GetAsync<MivDetailModel>($"/api/material-issue-vouchers/{first}")).Status.Should().Be("DRAFT");
        (await StockOfAsync(stocked)).Should().Be((OnHand: 30m, Reserved: 5m), "a draft moves nothing");
        await PostVoucherAsync(first);

        (await StockOfAsync(stocked)).Should().Be((OnHand: 28m, Reserved: 3m), "what was not issued is still held for the request");
        (await ActiveHoldsAsync(mirUuid)).Should().Be(3m);
        (await GetAsync<MirDetailModel>($"/api/material-issue-requests/{mirUuid}")).Status.Should().Be("PARTIALLY_ISSUED");

        // More than is still held is refused, and says why.
        var tooMuch = await _client.PostAsJsonAsync("/api/material-issue-vouchers", VoucherRequest(mirUuid, mirLine, 4m));
        tooMuch.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await tooMuch.Content.ReadAsStringAsync()).Should().Contain("exceeds");

        // A draft that is cancelled changes nothing about the stock or the hold.
        var abandoned = await CreateVoucherAsync(mirUuid, mirLine, quantity: 1m);
        var cancel = await _client.PostAsync($"/api/material-issue-vouchers/{abandoned}/cancel", null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK, await cancel.Content.ReadAsStringAsync());
        (await StockOfAsync(stocked)).Should().Be((OnHand: 28m, Reserved: 3m));

        // Second voucher: the rest. The hold is used up, the request is fully issued.
        await PostVoucherAsync(await CreateVoucherAsync(mirUuid, mirLine, quantity: 3m));

        (await StockOfAsync(stocked)).Should().Be((OnHand: 25m, Reserved: 0m));
        (await ActiveHoldsAsync(mirUuid)).Should().Be(0m);
        (await GetAsync<MirDetailModel>($"/api/material-issue-requests/{mirUuid}")).Status.Should().Be("FULLY_ISSUED");

        // Nothing left to issue.
        var more = await _client.PostAsJsonAsync("/api/material-issue-vouchers", VoucherRequest(mirUuid, mirLine, 1m));
        more.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task What_the_ledger_holds_always_equals_what_the_stock_counts_as_reserved()
    {
        // The two are written together or not at all; if a voucher consumed the hold without the counter,
        // or the counter without the hold, the difference is stock that looks free but is promised (or the reverse).
        var stocked = await StockAsync(quantity: 20m);
        var mirUuid = await ApprovedRequestAsync(stocked, quantity: 8m);
        var mirLine = (await GetAsync<MirDetailModel>($"/api/material-issue-requests/{mirUuid}")).Lines.Single().UUID;

        (await CounterAsync(stocked)).Should().Be(await AllActiveHoldsAsync(stocked));

        await PostVoucherAsync(await CreateVoucherAsync(mirUuid, mirLine, quantity: 3m));
        (await CounterAsync(stocked)).Should().Be(await AllActiveHoldsAsync(stocked)).And.Be(5m);

        await PostVoucherAsync(await CreateVoucherAsync(mirUuid, mirLine, quantity: 5m));
        (await CounterAsync(stocked)).Should().Be(await AllActiveHoldsAsync(stocked)).And.Be(0m);
    }

    // ── Set-up ───────────────────────────────────────────────────────────────────

    private sealed record Stocked(int ProductId, int VariantId, Guid VariantUuid, string Name, WarehouseModel Warehouse, Guid ProjectUuid);

    /// <summary>A product with the given quantity on the shelf, received through the ordinary purchase order and goods receipt.</summary>
    private async Task<Stocked> StockAsync(decimal quantity)
    {
        await CreateApproversAsync();

        var tag       = Guid.NewGuid().ToString("N")[..8];
        var warehouse = await CreateWarehouseAsync($"W{tag}");
        var project   = await PostAsync<Guid>("/api/projects", new CreateProjectRequest
            { ProjectCode = $"P{tag}", ProjectName = $"MIV {tag}", ProjectManagerId = _factory.AdminUserId });
        var supplier  = await PostAsync<Guid>("/api/suppliers",
            new SMS.Modules.Suppliers.Models.CreateSupplierRequest { SupplierName = $"MIV Supplier {tag}", SupplierCode = $"S{tag}" });

        var name    = $"MIV Bolt {tag}";
        var created = await PostAsync<System.Text.Json.JsonElement>("/api/products",
            new CreateProductRequest { Name = name, PurchasePrice = 10m, SellingPrice = 12m });
        var productId = created.GetProperty("id").GetInt32();
        var variant   = (await GetAsync<ProductDetailModel>($"/api/products/{productId}")).Variants.Single();

        // Kept under the PO tiering thresholds so a single approval tier applies.
        var poUuid = await PostAsync<Guid>("/api/purchase-orders", new CreatePoRequest
        {
            SupplierId = supplier, SupplierName = $"MIV Supplier {tag}",
            Lines = [ new CreatePoLineRequest
            {
                VariantUuid = variant.Uuid, ItemDescription = name, Quantity = quantity, UnitPrice = 10m,
                WarehouseId = warehouse.Uuid, WarehouseName = warehouse.Name, RequiresInspection = false
            } ]
        });
        await SubmitApproveAndSendAsync(poUuid);

        var po = await GetAsync<PoDetailModel>($"/api/purchase-orders/{poUuid}");
        var grnUuid = await PostAsync<Guid>("/api/grns", new CreateGrnRequest
        {
            PoUuid = poUuid, WarehouseUuid = warehouse.Uuid, ReceivedAt = DateTime.UtcNow,
            Lines = [ new GrnLineReceiveInput { PoLineUuid = po.Lines.Single().UUID, QtyReceived = quantity, QtyAccepted = quantity, QtyRejected = 0 } ]
        });
        (await _client.PostAsync($"/api/grns/{grnUuid}/submit", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.PostAsJsonAsync($"/api/grns/{grnUuid}/approve", new { Remarks = (string?)null })).StatusCode.Should().Be(HttpStatusCode.OK);

        return new Stocked(productId, variant.Id, variant.Uuid, name, warehouse, project);
    }

    /// <summary>A material issue request for the quantity, submitted and approved through the workflow, so its stock is held.</summary>
    private async Task<Guid> ApprovedRequestAsync(Stocked stocked, decimal quantity)
    {
        var mirUuid = await PostAsync<Guid>("/api/material-issue-requests", new CreateMirRequest
        {
            RequestType = "PROJECT", ProjectUuid = stocked.ProjectUuid,
            Lines = [ new CreateMirLineRequest { VariantUuid = stocked.VariantUuid, RequestedQty = quantity, WarehouseId = stocked.Warehouse.Id } ]
        });

        var submit = await _client.PostAsync($"/api/material-issue-requests/{mirUuid}/workflow/submit", null);
        submit.StatusCode.Should().Be(HttpStatusCode.OK, await submit.Content.ReadAsStringAsync());
        var approvalUuid = await _factory.ReadResultAsync<Guid>(submit);

        for (var i = 0; i < 8; i++)
        {
            var approve = await _client.PostAsJsonAsync($"/api/material-issue-requests/{mirUuid}/workflow/approve",
                new MirWorkflowApproveRequest { ApprovalUUID = approvalUuid, LineApprovals = [] });
            approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
            if ((await GetAsync<MirDetailModel>($"/api/material-issue-requests/{mirUuid}")).Status is "APPROVED" or "PARTIALLY_APPROVED") break;
        }
        (await GetAsync<MirDetailModel>($"/api/material-issue-requests/{mirUuid}")).Status.Should().BeOneOf("APPROVED", "PARTIALLY_APPROVED");
        return mirUuid;
    }

    private static CreateMivRequest VoucherRequest(Guid mirUuid, Guid mirLine, decimal quantity) => new()
    {
        MirUuid = mirUuid, IssuedTo = "Site Supervisor",
        Lines = [ new CreateMivLineRequest { MirLineUuid = mirLine, IssuedQty = quantity } ]
    };

    private Task<Guid> CreateVoucherAsync(Guid mirUuid, Guid mirLine, decimal quantity) =>
        PostAsync<Guid>("/api/material-issue-vouchers", VoucherRequest(mirUuid, mirLine, quantity));

    private async Task PostVoucherAsync(Guid mivUuid)
    {
        var post = await _client.PostAsync($"/api/material-issue-vouchers/{mivUuid}/post", null);
        post.StatusCode.Should().Be(HttpStatusCode.OK, $"MIV post failed: {await post.Content.ReadAsStringAsync()}");
        (await GetAsync<MivDetailModel>($"/api/material-issue-vouchers/{mivUuid}")).Status.Should().Be("POSTED");
    }

    // ── What the stock and the ledger say ────────────────────────────────────────

    private async Task<(decimal OnHand, decimal Reserved)> StockOfAsync(Stocked stocked)
    {
        var summary = await GetAsync<ProductStockSummaryModel>($"/api/products/{stocked.ProductId}/stock-summary");
        return (summary.TotalOnHand, summary.TotalReserved);
    }

    /// <summary>What the shared ledger says is still held for a request.</summary>
    private Task<decimal> ActiveHoldsAsync(Guid mirUuid) =>
        _factory.QueryScalarAsync<decimal>(
            "SELECT ISNULL(SUM(ReservedQty), 0) FROM inventory.StockReservations WHERE SourceType = 'MIR' AND SourceUuid = @m AND Status = 'ACTIVE'",
            cmd => cmd.Parameters.AddWithValue("@m", mirUuid));

    /// <summary>Every active hold on the variant, whoever holds it.</summary>
    private Task<decimal> AllActiveHoldsAsync(Stocked stocked) =>
        _factory.QueryScalarAsync<decimal>(
            "SELECT ISNULL(SUM(ReservedQty), 0) FROM inventory.StockReservations WHERE VariantUuid = @v AND Status = 'ACTIVE'",
            cmd => cmd.Parameters.AddWithValue("@v", stocked.VariantUuid));

    /// <summary>What the stock rows themselves count as reserved.</summary>
    private Task<decimal> CounterAsync(Stocked stocked) =>
        _factory.QueryScalarAsync<decimal>(
            "SELECT ISNULL(SUM(QtyReserved), 0) FROM inventory.InventoryItems WHERE VariantId = @v",
            cmd => cmd.Parameters.AddWithValue("@v", stocked.VariantId));

    // ── Plumbing ─────────────────────────────────────────────────────────────────

    private async Task CreateApproversAsync()
    {
        // ROLE-type approval steps refuse to start when nobody holds the role; the seeded admin approves through the override.
        foreach (var (role, tag) in new[]
        {
            ((int)EnumRole.ProcurementManager, "proc"), ((int)EnumRole.InventoryManager, "inv"), ((int)EnumRole.FinanceOfficer, "fin")
        })
        {
            var response = await _client.PostAsJsonAsync("/api/users", new
            {
                FirstName = "Placeholder", LastName = "Approver",
                Email = $"{tag}-{Guid.NewGuid():N}@miv.test", RoleID = role, SupplierType = "INTERNAL"
            });
            response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        }
    }

    private async Task<WarehouseModel> CreateWarehouseAsync(string code)
    {
        await PostAsync<int>("/api/warehouses", new CreateWarehouseRequest { Code = code, Name = $"Warehouse {code}" });
        return (await GetAsync<List<WarehouseModel>>("/api/warehouses")).Single(w => w.Code == code);
    }

    private async Task SubmitApproveAndSendAsync(Guid poUuid)
    {
        (await _client.PostAsync($"/api/purchase-orders/{poUuid}/submit", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        for (var i = 0; i < 6; i++)
        {
            (await _client.PostAsync($"/api/purchase-orders/{poUuid}/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            if ((await GetAsync<PoDetailModel>($"/api/purchase-orders/{poUuid}")).Status == "APPROVED") break;
        }
        (await _client.PostAsJsonAsync($"/api/purchase-orders/{poUuid}/send", new { SupplierContactMobile = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<T> PostAsync<T>(string url, object body)
    {
        var response = await _client.PostAsJsonAsync(url, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"POST {url} failed: {await response.Content.ReadAsStringAsync()}");
        return await _factory.ReadResultAsync<T>(response);
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} failed: {await response.Content.ReadAsStringAsync()}");
        return await _factory.ReadResultAsync<T>(response);
    }
}
