using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Integration.Tests.SalesPreOrder;

/// <summary>
/// A32 PB-12 — api/sale-inquiries on the real host (LocalDB): the review → quotation journey through the API
/// (T-C1-01/05/06/09/10/11/12 and BR-C1-01), the permission gates answering 403, and another organization's
/// inquiry answering 404 to the platform super admin (who bypasses the EF tenant filter).
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~SaleInquiryHttpTests</c>.</para>
/// </summary>
public sealed class SaleInquiryHttpTests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public SaleInquiryHttpTests(SapWebApplicationFactory factory)
    {
        _f    = factory;
        _root = new SapKit(factory, "INQ");
    }

    private static string Day(DateTime d) => d.ToString("yyyy-MM-dd");

    [Fact]
    public async Task An_inquiry_is_reviewed_and_quoted_through_the_api_and_is_read_only_afterwards()
    {
        var pkr      = await _root.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        var customer = await _root.CreateCustomerAsync("Inq Customer");
        var vendor   = await _root.CreateVendorAsync("Inq Vendor");
        var item     = await _root.CreateProductAsync("Inq Widget", purchasePrice: 60m, sellingPrice: 100m);
        var today    = DateTime.UtcNow.Date;

        var refused = await _root.Post("/api/sale-inquiries", new { PartnerId = vendor.Uuid });
        refused.Status.Should().Be(HttpStatusCode.BadRequest, $"BR-C1-01: a vendor is not a customer — {refused}");

        var inquiry = (await _root.Ok(_root.Post("/api/sale-inquiries", new
        {
            PartnerId = customer.Uuid, CustomerReference = "RFQ-HTTP", ReceivedDate = Day(today),
            Lines = new object[] { new { VariantUuid = item.VariantUuid, ProductDescription = "Widget as drawn", RequestedQuantity = 10m } }
        }), "create inquiry")).GetGuid();

        var created = await _root.Ok(_root.Get($"/api/sale-inquiries/{inquiry}"), "read inquiry");
        created.S("status").Should().Be("RECEIVED");
        created.S("inquiryNumber").Should().StartWith($"INQ-{today.Year}-");
        created.S("receivedDate").Should().StartWith(Day(today));
        var line = created.A("lines").Single();
        line.G("productUuid").Should().Be(item.ProductUuid);
        line.S("lineStatus").Should().Be("PENDING");
        var lineUuid = line.G("uuid");

        (await _root.Ok(_root.Get($"/api/sale-inquiries?search={created.S("inquiryNumber")}"), "list"))
            .A("data").Select(r => r.G("uuid")).Should().Equal(inquiry);

        await _root.Ok(_root.Patch($"/api/sale-inquiries/{inquiry}/status", new { Status = "UNDER_REVIEW" }), "begin review");
        var early = await _root.Patch($"/api/sale-inquiries/{inquiry}/status", new { Status = "REVIEW_COMPLETE" });
        early.Status.Should().Be(HttpStatusCode.BadRequest, $"T-C1-09: the line is still PENDING — {early}");

        await _root.Ok(_root.Put($"/api/sale-inquiries/{inquiry}/lines/{lineUuid}", new
        {
            VariantUuid = item.VariantUuid, ProductDescription = "Widget as drawn", RequestedQuantity = 10m,
            LineStatus = "PARTIAL", CanSupplyQuantity = 6m, EstimatedDeliveryDate = Day(today.AddDays(14))
        }), "evaluate the line as PARTIAL");
        var complete = await _root.Ok(_root.Patch($"/api/sale-inquiries/{inquiry}/status", new { Status = "REVIEW_COMPLETE" }), "complete review");
        complete.S("status").Should().Be("REVIEW_COMPLETE");
        complete.A("lines").Single().S("estimatedDeliveryDate").Should().StartWith(Day(today.AddDays(14)));

        var quotation = (await _root.Ok(_root.Post($"/api/sale-inquiries/{inquiry}/create-quotation", new
        {
            CurrencyId = pkr, ValidTo = Day(today.AddDays(30))
        }), "T-C1-11: create a quotation from the reviewed inquiry")).GetGuid();

        var quoted = await _root.Ok(_root.Get($"/api/sale-inquiries/{inquiry}"), "read the quoted inquiry");
        quoted.S("status").Should().Be("QUOTED");
        quoted.A("quotations").Single().G("uuid").Should().Be(quotation);
        var rows = await _f.QueryAsync(
            "SELECT q.Status, ql.Quantity FROM demand.sale_quotations q JOIN demand.sale_inquiries i ON i.Id = q.SourceInquiryId " +
            "JOIN demand.sale_quotation_lines ql ON ql.SaleQuotationId = q.Id WHERE q.UUID = @q AND i.UUID = @i",
            ("@q", quotation), ("@i", inquiry));
        rows.Should().ContainSingle().Which["Quantity"].Should().Be(6m, "the PARTIAL line is quoted at its can-supply quantity");

        var again = await _root.Post($"/api/sale-inquiries/{inquiry}/create-quotation", new { CurrencyId = pkr, ValidTo = Day(today.AddDays(30)) });
        again.Status.Should().Be(HttpStatusCode.Conflict, $"the inquiry is already quoted — {again}");
        var edit = await _root.Put($"/api/sale-inquiries/{inquiry}/lines/{lineUuid}", new
        {
            VariantUuid = item.VariantUuid, ProductDescription = "Widget as drawn", RequestedQuantity = 10m,
            LineStatus = "CAN_SUPPLY", EstimatedDeliveryDate = Day(today.AddDays(7))
        });
        edit.Status.Should().Be(HttpStatusCode.BadRequest, $"T-C1-12: a QUOTED inquiry is read-only — {edit}");
        var decline = await _root.Patch($"/api/sale-inquiries/{inquiry}/status", new { Status = "DECLINED", Reason = "late" });
        decline.Status.Should().Be(HttpStatusCode.BadRequest, $"QUOTED is terminal — {decline}");
    }

    [Fact]
    public async Task Every_inquiry_endpoint_answers_403_to_a_role_without_the_sales_codes()
    {
        var warehouse = await _root.LoginAsNewUserAsync((int)EnumRole.WarehouseOperator, "inqwh");
        var id   = Guid.NewGuid();
        var line = Guid.NewGuid();

        var calls = new (string What, Func<Task<Api>> Call)[]
        {
            ("list",             () => _root.Get("/api/sale-inquiries", warehouse)),
            ("create",           () => _root.Post("/api/sale-inquiries", new { PartnerId = Guid.NewGuid() }, warehouse)),
            ("read",             () => _root.Get($"/api/sale-inquiries/{id}", warehouse)),
            ("update",           () => _root.Put($"/api/sale-inquiries/{id}", new { ReceivedDate = "2026-10-01" }, warehouse)),
            ("add line",         () => _root.Post($"/api/sale-inquiries/{id}/lines", new { ProductDescription = "x", RequestedQuantity = 1 }, warehouse)),
            ("update line",      () => _root.Put($"/api/sale-inquiries/{id}/lines/{line}", new { ProductDescription = "x", RequestedQuantity = 1 }, warehouse)),
            ("delete line",      () => _root.Delete($"/api/sale-inquiries/{id}/lines/{line}", warehouse)),
            ("status",           () => _root.Patch($"/api/sale-inquiries/{id}/status", new { Status = "UNDER_REVIEW" }, warehouse)),
            ("create quotation", () => _root.Post($"/api/sale-inquiries/{id}/create-quotation", new { ValidTo = "2026-12-01" }, warehouse)),
        };

        foreach (var (what, call) in calls)
        {
            var api = await call();
            api.Status.Should().Be(HttpStatusCode.Forbidden, $"{what} without SALE_INQUIRY_*/SALE_QUOTATION_CREATE — {api}");
        }
    }

    [Fact]
    public async Task Another_organizations_inquiry_is_404_to_the_super_admin_and_absent_from_its_list()
    {
        var (_, other) = await SecondOrganizationAsync();
        var customer = await other.CreateCustomerAsync("Org2 Inq Customer");
        var inquiry = (await other.Ok(other.Post("/api/sale-inquiries", new
        {
            PartnerId = customer.Uuid,
            Lines = new object[] { new { ProductDescription = "Org 2 item", RequestedQuantity = 5m } }
        }), "org 2 creates an inquiry")).GetGuid();
        var line = (await other.Ok(other.Get($"/api/sale-inquiries/{inquiry}"), "org 2 reads it")).A("lines").Single().G("uuid");

        var calls = new (string What, Func<Task<Api>> Call)[]
        {
            ("read",             () => _root.Get($"/api/sale-inquiries/{inquiry}")),
            ("update",           () => _root.Put($"/api/sale-inquiries/{inquiry}", new { ReceivedDate = "2026-10-01", Notes = "mine now" })),
            ("add line",         () => _root.Post($"/api/sale-inquiries/{inquiry}/lines", new { ProductDescription = "x", RequestedQuantity = 1 })),
            ("update line",      () => _root.Put($"/api/sale-inquiries/{inquiry}/lines/{line}", new { ProductDescription = "x", RequestedQuantity = 1, LineStatus = "UNDER_REVIEW" })),
            ("delete line",      () => _root.Delete($"/api/sale-inquiries/{inquiry}/lines/{line}")),
            ("status",           () => _root.Patch($"/api/sale-inquiries/{inquiry}/status", new { Status = "UNDER_REVIEW" })),
            ("create quotation", () => _root.Post($"/api/sale-inquiries/{inquiry}/create-quotation", new { ValidTo = "2026-12-01" })),
        };
        foreach (var (what, call) in calls)
        {
            var api = await call();
            api.Status.Should().Be(HttpStatusCode.NotFound, $"super admin {what} of another organization's inquiry — {api}");
        }

        (await _root.Ok(_root.Get("/api/sale-inquiries?pageSize=100"), "super admin list"))
            .A("data").Select(r => r.G("uuid")).Should().NotContain(inquiry);

        var after = await other.Ok(other.Get($"/api/sale-inquiries/{inquiry}"), "org 2 reads it again");
        after.S("status").Should().Be("RECEIVED");
        after.IsNull("notes").Should().BeTrue();
        after.A("lines").Should().ContainSingle().Which.S("lineStatus").Should().Be("PENDING");
    }

    /// <summary>A second organization whose admin has accepted the invitation, and a kit acting as that admin (CrossOrganizationReversalE2ETests).</summary>
    private async Task<(Guid OrgId, SapKit Kit)> SecondOrganizationAsync()
    {
        var email = $"org2-{Guid.NewGuid():N}@sap-e2e.test";
        var created = await _root.Ok(_root.Post("/api/system/organizations", new
        {
            OrgCode = $"X{Guid.NewGuid():N}"[..10].ToUpperInvariant(), OrgName = $"{_root.Marker} Org {Guid.NewGuid():N}"[..30], Plan = "ENTERPRISE",
            AdminFirstName = "Other", AdminLastName = "Admin", AdminEmail = email
        }), "create a second organization");
        var orgId = created.G("organizationId");
        orgId.Should().NotBe(_f.OrganizationId);

        await _f.SetPasswordAsync(email, "Org2@12345!");
        await _f.ExecuteAsync("UPDATE auth.UserAccounts SET IsActive = 1 WHERE Email = @e", ("@e", email));
        var client = _f.CreateBearerClient(await _f.LoginAsync(email, "Org2@12345!"));
        return (orgId, new SapKit(_f, "O2", client));
    }
}
