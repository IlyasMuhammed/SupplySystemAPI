using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using SMS.Integration.Tests.ProcurementCycle.Infrastructure;
using SMS.Modules.Demand.Models;
using SMS.Modules.Finance.Models;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Warehouse.Models;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using Xunit;

namespace SMS.Integration.Tests.PartnerBackwardCompatibility;

// A29 §17 TC-01 — "GET /api/suppliers returns only is_vendor=1; create PO with an existing vendor;
// procurement unchanged" (task A29-P10-01).
//
// Suppliers were renamed to business partners (§1.1), so the table that used to hold only vendors now also
// holds customers, carriers and service providers. Everything that read "the suppliers" has to keep reading
// vendors and only vendors, and everything a vendor could do before has to work exactly as it did.
//
// Driven over real HTTP against the real Program.cs pipeline and a real, throwaway LocalDB database, as
// ProcurementToIssueCycleTests is: no mocks anywhere in the code under test. The partners are made through
// the new /api/partners surface and the old /api/suppliers one, so the test exercises both sides of the
// rename, and the answers are checked against the rows themselves, not only against each other.
public sealed class VendorBackwardCompatibilityTests : IClassFixture<ProcurementCycleWebApplicationFactory>
{
    private readonly ProcurementCycleWebApplicationFactory _factory;
    private readonly HttpClient _client;

    /// <summary>Every name this test makes starts with this, so its rows can be told from anything else in the database.</summary>
    private readonly string _marker = $"TC01-{Guid.NewGuid():N}"[..13];

    public VendorBackwardCompatibilityTests(ProcurementCycleWebApplicationFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateAdminClient();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GET /api/suppliers returns only is_vendor = 1
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Suppliers_list_returns_only_vendors_whatever_else_the_partner_table_holds()
    {
        // All eight partner types of §1.2, made through the new surface: five are vendors, three are not.
        var vendors = new[]
        {
            await CreatePartnerAsync("Vendor",          vendor: true),
            await CreatePartnerAsync("Vendor Customer", vendor: true, customer: true),
            await CreatePartnerAsync("Vendor Carrier",  vendor: true, carrier: true),
            await CreatePartnerAsync("Vendor Service",  vendor: true, service: true),
            await CreatePartnerAsync("Full Partner",    vendor: true, customer: true, carrier: true, service: true)
        };
        var others = new[]
        {
            await CreatePartnerAsync("Customer",         customer: true),
            await CreatePartnerAsync("Carrier",          carrier: true),
            await CreatePartnerAsync("Service Provider", service: true)
        };
        // And one made the old way, which is what every existing vendor is.
        var legacy = await CreateLegacySupplierAsync("Legacy Vendor");

        var expected = vendors.Select(v => v.Name).Append(legacy.Name).Order().ToList();

        var listed = await GetAsync<PaginatedResponse<SupplierListItemModel>>(
            $"/api/suppliers?search={_marker}&pageSize=100");

        listed.Data.Select(s => s.SupplierName).Order().Should().Equal(expected,
            "a vendor, a vendor that is also something else, and an existing vendor are all suppliers");
        listed.TotalRecords.Should().Be(expected.Count, "the count is of vendors too, not of every partner");
        foreach (var other in others)
            listed.Data.Should().NotContain(s => s.SupplierName == other.Name, $"{other.Label} is not a vendor");

        // The same rows, asked the new way.
        var asPartners = await GetAsync<PaginatedResponse<BusinessPartnerModel>>(
            $"/api/partners?isVendor=true&search={_marker}&pageSize=100");
        asPartners.Data.Select(p => p.CompanyName).Order().Should().Equal(expected,
            "/api/suppliers and /api/partners?isVendor=true are two views of one set of rows");

        // And the rows themselves, not the API's word for them.
        (await CountPartnersAsync("IsVendor = 1")).Should().Be(expected.Count);
        (await CountPartnersAsync("IsVendor = 0")).Should().Be(others.Length,
            "the test is only meaningful because the table really does hold partners that are not vendors");
    }

    [Fact]
    public async Task Nothing_in_the_unfiltered_suppliers_list_is_a_partner_that_is_not_a_vendor()
    {
        await CreatePartnerAsync("Customer", customer: true);
        await CreatePartnerAsync("Carrier", carrier: true);
        await CreatePartnerAsync("Vendor", vendor: true);

        var everything = await GetAsync<PaginatedResponse<SupplierListItemModel>>("/api/suppliers?pageSize=100");

        everything.Data.Should().NotBeEmpty();
        var ids = string.Join(",", everything.Data.Select(s => $"'{s.UUID}'"));
        (await CountPartnersAsync($"UUID IN ({ids}) AND IsVendor = 0")).Should().Be(0,
            "every row /api/suppliers returns is a vendor, without a search to narrow it");
        (await CountPartnersAsync("IsVendor = 0 AND IsDelete = 0")).Should().BePositive(
            "the table does hold partners that are not vendors, so the check above proves something");
    }

    [Fact]
    public async Task A_customer_is_not_found_by_searching_the_suppliers_for_its_name()
    {
        var customer = await CreatePartnerAsync("Customer", customer: true);

        var listed = await GetAsync<PaginatedResponse<SupplierListItemModel>>(
            $"/api/suppliers?search={Uri.EscapeDataString(customer.Name)}&pageSize=100");

        listed.Data.Should().BeEmpty();
        listed.TotalRecords.Should().Be(0);
    }

    [Fact]
    public async Task A_vendor_that_is_deleted_leaves_the_suppliers_list_as_before()
    {
        var vendor = await CreateLegacySupplierAsync("Doomed Vendor");
        (await GetAsync<PaginatedResponse<SupplierListItemModel>>($"/api/suppliers?search={_marker}&pageSize=100"))
            .Data.Should().Contain(s => s.UUID == vendor.Uuid);

        var response = await _client.DeleteAsync($"/api/suppliers/{vendor.Uuid}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        (await GetAsync<PaginatedResponse<SupplierListItemModel>>($"/api/suppliers?search={_marker}&pageSize=100"))
            .Data.Should().NotContain(s => s.UUID == vendor.Uuid);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // An existing vendor is still a vendor
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_vendor_made_through_the_suppliers_api_is_a_vendor_partner_and_keeps_its_workflow()
    {
        var vendor = await CreateLegacySupplierAsync("Existing Vendor");

        // As a partner: a vendor and nothing else, with the code and name it was given.
        var partner = await GetAsync<BusinessPartnerModel>($"/api/partners/{vendor.Uuid}");
        (partner.PartnerType, partner.IsVendor, partner.IsCustomer, partner.IsCarrier, partner.IsServiceProvider)
            .Should().Be(("VENDOR", true, false, false, false));
        partner.CompanyName.Should().Be(vendor.Name);
        partner.PartnerCode.Should().Be(vendor.Code);
        partner.IsActive.Should().BeTrue();
        (await QueryScalarAsync<string>("SELECT PartnerType FROM suppliers.BusinessPartners WHERE UUID = @u", vendor.Uuid))
            .Should().Be("VENDOR");

        // As a supplier: read, edit and approve through the routes that have always been there.
        (await GetAsync<SupplierDetailModel>($"/api/suppliers/{vendor.Uuid}")).SupplierName.Should().Be(vendor.Name);

        var renamed = $"{vendor.Name} Renamed";
        var patch = await _client.PatchAsJsonAsync($"/api/suppliers/{vendor.Uuid}", new PatchSupplierRequest { SupplierName = renamed });
        patch.StatusCode.Should().Be(HttpStatusCode.OK, await patch.Content.ReadAsStringAsync());
        (await GetAsync<BusinessPartnerModel>($"/api/partners/{vendor.Uuid}")).CompanyName.Should().Be(renamed,
            "an edit through the old route is an edit of the partner");

        (await GetAsync<SupplierDetailModel>($"/api/suppliers/{vendor.Uuid}")).Status.Should().Be("PENDING");
        var approve = await _client.PostAsync($"/api/suppliers/{vendor.Uuid}/approve", null);
        approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
        (await GetAsync<SupplierDetailModel>($"/api/suppliers/{vendor.Uuid}")).Status.Should().Be("ACTIVE",
            "approving a pending supplier makes it an active one");

        // And its payables ledger, which lives under the same address, is still there for it.
        (await _client.GetAsync($"/api/suppliers/{vendor.Uuid}/ledger")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync($"/api/suppliers/{vendor.Uuid}/balance")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // A purchase order with an existing vendor, and everything after it
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task An_existing_vendor_takes_a_purchase_order_through_procurement_unchanged()
    {
        await CreateApproversAsync();
        var vendor    = await CreateLegacySupplierAsync("PO Vendor");
        var warehouse = await CreateWarehouseAsync();
        var variant   = await CreateProductAsync("Widget", purchasePrice: 300m);

        // Create: the purchase order names the vendor by the id it has always had.
        var poUuid = await PostAsync<Guid>("/api/purchase-orders", new CreatePoRequest
        {
            SupplierId   = vendor.Uuid,
            SupplierName = vendor.Name,
            Lines = [ PoLine(variant, warehouse, quantity: 10, unitPrice: 300m) ]
        });

        var po = await GetAsync<PoDetailModel>($"/api/purchase-orders/{poUuid}");
        po.SupplierId.Should().Be(vendor.Uuid);
        po.SupplierName.Should().Be(vendor.Name);
        po.TotalAmount.Should().Be(3_000m);
        po.Source.Should().Be("MANUAL");
        po.LinkedSaleOrderUuid.Should().BeNull("a purchase order raised by hand has no sale order behind it (§14)");

        var byVendor = await GetAsync<PaginatedResponse<PoListItemModel>>($"/api/purchase-orders?supplierId={vendor.Uuid}&pageSize=100");
        byVendor.Data.Should().ContainSingle(p => p.UUID == poUuid, "the vendor's purchase orders are found by the vendor's id");

        // Submit, approve and send: the approval workflow, the send, and the status it ends in.
        await SubmitApproveAndSendAsync(poUuid);
        (await GetAsync<PoDetailModel>($"/api/purchase-orders/{poUuid}")).Status.Should().Be("SENT");

        // Receive it: the goods come in against the PO, and the PO says so.
        var grnUuid = await ReceiveAsync(poUuid, warehouse, quantity: 10);
        var stock = await GetAsync<ProductStockSummaryModel>($"/api/products/{variant.ProductId}/stock-summary");
        stock.TotalOnHand.Should().Be(10m);

        var grn = await GetAsync<SMS.Modules.Warehouse.Models.GrnDetailModel>($"/api/grns/{grnUuid}");
        grn.PoUuid.Should().Be(poUuid);

        // Bill it: the vendor's invoice names the vendor, by the same lookup every payables screen uses.
        var invoiceUuid = await PostAsync<Guid>("/api/finance/invoices", new CreateInvoiceRequest
        {
            SupplierInvoiceNo = $"{_marker}-INV",
            SupplierId        = vendor.Uuid,
            PoUuid            = poUuid,
            GrnUuid           = grnUuid,
            InvoiceDate       = DateTime.UtcNow.Date,
            ReceivedDate      = DateTime.UtcNow.Date,
            DueDate           = DateTime.UtcNow.Date.AddDays(30),
            Currency          = "PKR",
            Subtotal          = 3_000m,
            TaxAmount         = 0m
        });
        var invoice = await GetAsync<InvoiceDetailModel>($"/api/finance/invoices/{invoiceUuid}");
        invoice.SupplierId.Should().Be(vendor.Uuid);
        invoice.SupplierName.Should().Be(vendor.Name);
        invoice.PoNumber.Should().Be(po.PoNumber);

        // An invoice with no purchase order behind it has no PO to take the name from, so it asks the
        // shared supplier lookup. The vendor must still be found there.
        var directUuid = await PostAsync<Guid>("/api/finance/invoices", DirectInvoice(vendor, subtotal: 500m));
        (await GetAsync<InvoiceDetailModel>($"/api/finance/invoices/{directUuid}")).SupplierName.Should().Be(vendor.Name);
    }

    [Fact]
    public async Task A_vendor_that_is_also_a_customer_is_still_a_supplier_and_still_takes_purchase_orders()
    {
        await CreateApproversAsync();
        var vendor    = await CreateLegacySupplierAsync("Two-way Vendor");
        var warehouse = await CreateWarehouseAsync();
        var variant   = await CreateProductAsync("Gadget", purchasePrice: 100m);

        // The vendor starts buying from us as well: flags change, nothing else about it does.
        var partner = await GetAsync<BusinessPartnerModel>($"/api/partners/{vendor.Uuid}");
        partner.IsCustomer = true;
        var put = await _client.PutAsJsonAsync($"/api/partners/{vendor.Uuid}", partner);
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        (await GetAsync<BusinessPartnerModel>($"/api/partners/{vendor.Uuid}")).PartnerType.Should().Be("BOTH");
        (await GetAsync<PaginatedResponse<SupplierListItemModel>>($"/api/suppliers?search={_marker}&pageSize=100"))
            .Data.Should().Contain(s => s.UUID == vendor.Uuid, "a vendor that is also a customer is still a vendor");

        var poUuid = await PostAsync<Guid>("/api/purchase-orders", new CreatePoRequest
        {
            SupplierId = vendor.Uuid, SupplierName = vendor.Name,
            Lines = [ PoLine(variant, warehouse, quantity: 5, unitPrice: 100m) ]
        });
        await SubmitApproveAndSendAsync(poUuid);
        (await GetAsync<PoDetailModel>($"/api/purchase-orders/{poUuid}")).Status.Should().Be("SENT");
    }

    [Fact]
    public async Task A_vendor_that_stops_being_one_leaves_the_suppliers_list_and_takes_its_purchase_orders_history_with_it_intact()
    {
        var vendor    = await CreateLegacySupplierAsync("Former Vendor");
        var warehouse = await CreateWarehouseAsync();
        var variant   = await CreateProductAsync("Thing", purchasePrice: 50m);

        var poUuid = await PostAsync<Guid>("/api/purchase-orders", new CreatePoRequest
        {
            SupplierId = vendor.Uuid, SupplierName = vendor.Name,
            Lines = [ PoLine(variant, warehouse, quantity: 2, unitPrice: 50m) ]
        });

        var billed = await PostAsync<Guid>("/api/finance/invoices", DirectInvoice(vendor, subtotal: 100m));

        // It becomes a customer only: nothing is deleted, moved or duplicated (§1.7), it is just not a vendor now.
        var partner = await GetAsync<BusinessPartnerModel>($"/api/partners/{vendor.Uuid}");
        partner.IsVendor = false;
        partner.IsCustomer = true;
        (await _client.PutAsJsonAsync($"/api/partners/{vendor.Uuid}", partner)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetAsync<PaginatedResponse<SupplierListItemModel>>($"/api/suppliers?search={_marker}&pageSize=100"))
            .Data.Should().NotContain(s => s.UUID == vendor.Uuid);

        // What was already raised against it stays readable, by the same ids and names.
        var po = await GetAsync<PoDetailModel>($"/api/purchase-orders/{poUuid}");
        po.SupplierId.Should().Be(vendor.Uuid, "the purchase order still points at the same row");
        po.SupplierName.Should().Be(vendor.Name);
        (await GetAsync<PaginatedResponse<PoListItemModel>>($"/api/purchase-orders?supplierId={vendor.Uuid}&pageSize=100"))
            .Data.Should().ContainSingle(p => p.UUID == poUuid);
        (await GetAsync<InvoiceDetailModel>($"/api/finance/invoices/{billed}")).SupplierName.Should().Be(vendor.Name,
            "an invoice carries the name it was raised with, so it stays readable after the supplier changes");
    }

    // ── Partners ─────────────────────────────────────────────────────────────────

    private sealed record Made(Guid Uuid, string Name, string Code, string Label);

    private int _partners;

    /// <summary>A partner made through the new /api/partners surface, with the flags given.</summary>
    private async Task<Made> CreatePartnerAsync(
        string label, bool vendor = false, bool customer = false, bool carrier = false, bool service = false)
    {
        var name = $"{_marker} {label}";
        var code = $"P{Interlocked.Increment(ref _partners)}{Guid.NewGuid():N}"[..10];

        var uuid = await PostAsync<Guid>("/api/partners", new BusinessPartnerModel
        {
            PartnerCode = code, CompanyName = name,
            IsVendor = vendor, IsCustomer = customer, IsCarrier = carrier, IsServiceProvider = service
        });
        return new Made(uuid, name, code, label);
    }

    /// <summary>A vendor made the way every existing one was: through /api/suppliers.</summary>
    private async Task<Made> CreateLegacySupplierAsync(string label)
    {
        var name = $"{_marker} {label}";
        var code = $"L{Interlocked.Increment(ref _partners)}{Guid.NewGuid():N}"[..10];

        var uuid = await PostAsync<Guid>("/api/suppliers", new CreateSupplierRequest { SupplierName = name, SupplierCode = code });
        return new Made(uuid, name, code, label);
    }

    // ── Procurement ──────────────────────────────────────────────────────────────

    /// <summary>
    /// ROLE-type approval steps resolve who could approve when a document is submitted, and refuse if nobody
    /// holds the role. The seeded admin approves everything through the SystemAdmin override, but each role
    /// still needs someone in it.
    /// </summary>
    private async Task CreateApproversAsync()
    {
        foreach (var (role, tag) in new[]
        {
            ((int)EnumRole.ProcurementManager, "proc"), ((int)EnumRole.InventoryManager, "inv"), ((int)EnumRole.FinanceOfficer, "fin")
        })
        {
            var response = await _client.PostAsJsonAsync("/api/users", new
            {
                FirstName = "Placeholder", LastName = "Approver",
                Email = $"{tag}-{Guid.NewGuid():N}@tc01.test", RoleID = role, SupplierType = "INTERNAL"
            });
            response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        }
    }

    private async Task<WarehouseModel> CreateWarehouseAsync()
    {
        var code = $"W{Guid.NewGuid():N}"[..8];
        await PostAsync<int>("/api/warehouses", new CreateWarehouseRequest { Code = code, Name = $"{_marker} Warehouse" });
        return (await GetAsync<List<WarehouseModel>>("/api/warehouses")).Single(w => w.Code == code);
    }

    private sealed record Variant(int ProductId, Guid Uuid, string Name);

    private async Task<Variant> CreateProductAsync(string name, decimal purchasePrice)
    {
        var fullName = $"{_marker} {name}";
        var created = await PostAsync<System.Text.Json.JsonElement>("/api/products",
            new CreateProductRequest { Name = fullName, PurchasePrice = purchasePrice, SellingPrice = purchasePrice * 1.2m });
        var productId = created.GetProperty("id").GetInt32();
        var detail = await GetAsync<ProductDetailModel>($"/api/products/{productId}");
        return new Variant(productId, detail.Variants.Single().Uuid, fullName);
    }

    private static CreatePoLineRequest PoLine(Variant variant, WarehouseModel warehouse, decimal quantity, decimal unitPrice) => new()
    {
        VariantUuid = variant.Uuid, ItemDescription = variant.Name, Quantity = quantity, UnitPrice = unitPrice,
        WarehouseId = warehouse.Uuid, WarehouseName = warehouse.Name, RequiresInspection = false
    };

    /// <summary>Each tier of the approval chain is approved in turn, however many the PO's value calls for.</summary>
    private async Task SubmitApproveAndSendAsync(Guid poUuid)
    {
        var submit = await _client.PostAsync($"/api/purchase-orders/{poUuid}/submit", null);
        submit.StatusCode.Should().Be(HttpStatusCode.OK, $"PO submit failed: {await submit.Content.ReadAsStringAsync()}");

        for (var i = 0; i < 6; i++)
        {
            var approve = await _client.PostAsync($"/api/purchase-orders/{poUuid}/approve", null);
            approve.StatusCode.Should().Be(HttpStatusCode.OK, $"PO approve failed: {await approve.Content.ReadAsStringAsync()}");
            if ((await GetAsync<PoDetailModel>($"/api/purchase-orders/{poUuid}")).Status == "APPROVED") break;
        }
        (await GetAsync<PoDetailModel>($"/api/purchase-orders/{poUuid}")).Status.Should().Be("APPROVED");

        var send = await _client.PostAsJsonAsync($"/api/purchase-orders/{poUuid}/send", new { SupplierContactMobile = (string?)null });
        send.StatusCode.Should().Be(HttpStatusCode.OK, $"PO send failed: {await send.Content.ReadAsStringAsync()}");
    }

    /// <summary>A payable raised against the supplier alone, with no purchase order behind it.</summary>
    private CreateInvoiceRequest DirectInvoice(Made vendor, decimal subtotal) => new()
    {
        SupplierInvoiceNo = $"{_marker}-{Guid.NewGuid():N}"[..30],
        SupplierId        = vendor.Uuid,
        InvoiceDate       = DateTime.UtcNow.Date,
        ReceivedDate      = DateTime.UtcNow.Date,
        DueDate           = DateTime.UtcNow.Date.AddDays(30),
        Currency          = "PKR",
        Subtotal          = subtotal,
        TaxAmount         = 0m
    };

    private async Task<Guid> ReceiveAsync(Guid poUuid, WarehouseModel warehouse, decimal quantity)
    {
        var po = await GetAsync<PoDetailModel>($"/api/purchase-orders/{poUuid}");
        var grnUuid = await PostAsync<Guid>("/api/grns", new CreateGrnRequest
        {
            PoUuid = poUuid, WarehouseUuid = warehouse.Uuid, ReceivedAt = DateTime.UtcNow,
            Lines = [ new GrnLineReceiveInput { PoLineUuid = po.Lines.Single().UUID, QtyReceived = quantity, QtyAccepted = quantity, QtyRejected = 0 } ]
        });

        var submit = await _client.PostAsync($"/api/grns/{grnUuid}/submit", null);
        submit.StatusCode.Should().Be(HttpStatusCode.OK, $"GRN submit failed: {await submit.Content.ReadAsStringAsync()}");
        var approve = await _client.PostAsJsonAsync($"/api/grns/{grnUuid}/approve", new { Remarks = (string?)null });
        approve.StatusCode.Should().Be(HttpStatusCode.OK, $"GRN approve failed: {await approve.Content.ReadAsStringAsync()}");
        return grnUuid;
    }

    // ── HTTP and SQL ─────────────────────────────────────────────────────────────

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

    /// <summary>Rows in the partner table that this test made and that match the condition.</summary>
    private Task<int> CountPartnersAsync(string condition) =>
        _factory.QueryScalarAsync<int>(
            $"SELECT COUNT(*) FROM suppliers.BusinessPartners WHERE {condition}" +
            (condition.Contains("UUID IN") ? "" : " AND SupplierName LIKE @marker"),
            cmd => cmd.Parameters.AddWithValue("@marker", _marker + "%"));

    private Task<T> QueryScalarAsync<T>(string sql, Guid uuid) =>
        _factory.QueryScalarAsync<T>(sql, cmd => cmd.Parameters.AddWithValue("@u", uuid));
}
