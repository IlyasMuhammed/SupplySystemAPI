using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Integration.Tests.SalesPreOrder;

/// <summary>
/// A32-PC-15 — Sale Quotations through the real host on LocalDB (the SAP-alignment factory: a throwaway database
/// built from the current models, real SQL Server unique indexes and concurrency tokens):
/// <list type="bullet">
/// <item>PC-03: concurrent creates in one organization draw distinct SQ-YYYY-NNNNN numbers;</item>
/// <item>PD-04: the full DRAFT → SENT → ACCEPTED → CONVERTED path over HTTP, and a double convert racing itself
/// makes exactly one sale order — the loser is a 409 (Status concurrency token / unique SourceQuotationId);</item>
/// <item>the contract's permission gates for a role holding no sales code.</item>
/// </list>
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~SaleQuotationConcurrencyE2ETests</c>.</para>
/// </summary>
public sealed class SaleQuotationConcurrencyE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapKit _k;
    private readonly SapWebApplicationFactory _f;

    public SaleQuotationConcurrencyE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "SQ");
    }

    private async Task<Guid> CurrencyAsync()
    {
        var pkr = await _k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        await _k.SetBaseCurrencyAsync(pkr);
        return pkr;
    }

    [Fact]
    public async Task Concurrent_creates_in_one_organization_draw_distinct_SQ_numbers()
    {
        var pkr = await CurrencyAsync();
        var customer = await _k.CreateCustomerAsync("Race Customer");
        var validTo = DateTime.UtcNow.Date.AddDays(30).ToString("yyyy-MM-dd");

        const int n = 12;
        var calls = await Task.WhenAll(Enumerable.Range(0, n).Select(_ =>
            _k.Post("/api/sale-quotations", new { PartnerId = customer.Uuid, CurrencyId = pkr, ValidTo = validTo })));

        calls.Should().OnlyContain(c => c.Status == HttpStatusCode.OK, "every concurrent create succeeds — {0}",
            string.Join(" | ", calls.Where(c => c.Status != HttpStatusCode.OK).Select(c => c.ToString())));
        var uuids = calls.Select(c => c.Result.GetGuid()).ToList();

        var rows = await _f.QueryAsync(
            "SELECT QuotationNumber FROM demand.sale_quotations WHERE OrganizationId = @org AND PartnerId = @p",
            ("@org", _f.OrganizationId), ("@p", customer.Uuid));
        var numbers = rows.Select(r => (string)r["QuotationNumber"]!).ToList();
        numbers.Should().HaveCount(n).And.OnlyHaveUniqueItems();
        numbers.Should().OnlyContain(x => Regex.IsMatch(x, $@"^SQ-{DateTime.UtcNow.Year}-\d{{5}}$"));

        foreach (var uuid in uuids.Take(2))
            (await _k.Ok(_k.Get($"/api/sale-quotations/{uuid}"), "read quotation")).S("status").Should().Be("DRAFT");
    }

    [Fact]
    public async Task An_accepted_quotation_converts_once_and_a_racing_second_convert_is_a_409()
    {
        var pkr = await CurrencyAsync();
        var customer = await _k.CreateCustomerAsync("Convert Customer");
        var a = await _k.CreateProductAsync("Quoted Widget", purchasePrice: 60m, sellingPrice: 100m);
        var b = await _k.CreateProductAsync("Offered Instead", purchasePrice: 40m, sellingPrice: 80m);
        var reasons = await _k.Ok(_k.Get("/api/rejection-reasons"), "list rejection reasons");
        var reason = reasons.Items().First(r => r.S("code") == "OOS").G("uuid");

        // DRAFT with a NORMAL line (waterfall price), a REJECTED line and its ALTERNATIVE (by request line number).
        var uuid = (await _k.Ok(_k.Post("/api/sale-quotations", new
        {
            PartnerId = customer.Uuid, CurrencyId = pkr,
            ValidFrom = DateTime.UtcNow.Date.ToString("yyyy-MM-dd"), ValidTo = DateTime.UtcNow.Date.AddDays(14).ToString("yyyy-MM-dd"),
            Lines = new object[]
            {
                new { LineType = "NORMAL", VariantUuid = a.VariantUuid, Quantity = 3m, DiscountPercent = 10m, TaxPercent = 5m },
                new { LineType = "REJECTED", ProductDescription = "Gadget we do not stock", Quantity = 2m, RejectionReasonUuid = reason },
                new { LineType = "ALTERNATIVE", VariantUuid = b.VariantUuid, Quantity = 2m, AlternativeForLineNumber = 2, UnitPrice = 75m }
            }
        }), "create quotation")).GetGuid();

        var draft = await _k.Ok(_k.Get($"/api/sale-quotations/{uuid}"), "read draft");
        draft.B("isEditable").Should().BeTrue();
        draft.A("allowedActions").Select(x => x.GetString()).Should().Contain("SEND");
        var lines = draft.A("lines");
        var normal = lines.Single(l => l.S("lineType") == "NORMAL");
        normal.D("unitPrice").Should().Be(100m, "the variant's selling price, through the waterfall");
        normal.D("lineTotal").Should().Be(283.5m, "3 × 100, less 10%, plus 5%");
        lines.Single(l => l.S("lineType") == "ALTERNATIVE").I("alternativeForLineNumber").Should().Be(2);
        draft.D("grandTotal").Should().Be(283.5m + 150m);

        await _k.Ok(_k.Post($"/api/sale-quotations/{uuid}/send"), "send");
        (await _k.Put($"/api/sale-quotations/{uuid}", new { ValidFrom = "2026-01-01", ValidTo = "2026-12-31" }))
            .Status.Should().Be(HttpStatusCode.BadRequest, "a SENT quotation is not editable");

        await _k.Ok(_k.Patch($"/api/sale-quotations/{uuid}/lines/{normal.G("uuid")}/customer-response", new { Response = "ACCEPTED" }), "accept line");
        var alt = lines.Single(l => l.S("lineType") == "ALTERNATIVE").G("uuid");
        await _k.Ok(_k.Patch($"/api/sale-quotations/{uuid}/lines/{alt}/customer-response", new { Response = "COUNTER", CounterPrice = 70m }), "counter");
        await _k.Ok(_k.Post($"/api/sale-quotations/{uuid}/accept"), "accept quotation");

        // Two conversions at once: exactly one order.
        var convertBody = new { DeliveryMode = "SELF_PICKUP", CustomerPoReference = $"{_k.Marker}-PO", CustomerPoDate = DateTime.UtcNow.Date.ToString("yyyy-MM-dd") };
        var both = await Task.WhenAll(
            _k.Post($"/api/sale-quotations/{uuid}/convert-to-order", convertBody),
            _k.Post($"/api/sale-quotations/{uuid}/convert-to-order", convertBody));

        both.Select(c => c.Status).Should().BeEquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict },
            $"one conversion wins, the other loses the race — {string.Join(" | ", both.Select(c => c.ToString()))}");
        var soUuid = both.Single(c => c.Status == HttpStatusCode.OK).Result.GetGuid();

        (await _k.Post($"/api/sale-quotations/{uuid}/convert-to-order", convertBody)).Status
            .Should().Be(HttpStatusCode.Conflict, "a CONVERTED quotation cannot convert again");

        var orders = await _f.QueryAsync(
            "SELECT o.UUID, o.SourceType, o.CustomerPoReference FROM demand.sale_orders o " +
            "JOIN demand.sale_quotations q ON q.Id = o.SourceQuotationId WHERE q.UUID = @q", ("@q", uuid));
        orders.Should().ContainSingle();
        orders[0]["UUID"].Should().Be(soUuid);
        orders[0]["SourceType"].Should().Be("FROM_QUOTATION");
        orders[0]["CustomerPoReference"].Should().Be($"{_k.Marker}-PO");

        var so = await _k.GetSaleOrderAsync(soUuid);
        so.A("lines").Should().ContainSingle("only the ACCEPTED line converts; the unresolved COUNTER stays out (T-C2-13)")
            .Which.G("variantUuid").Should().Be(a.VariantUuid);
        so.A("lines")[0].D("unitPrice").Should().Be(100m, "the quoted price, not re-resolved");

        var converted = await _k.Ok(_k.Get($"/api/sale-quotations/{uuid}"), "read converted");
        converted.S("status").Should().Be("CONVERTED");
        converted.P("saleOrder").G("uuid").Should().Be(soUuid);
    }

    [Fact]
    public async Task T_C2_02_two_racing_create_quotation_calls_on_one_reviewed_inquiry_make_one_quotation_and_a_409()
    {
        var pkr = await CurrencyAsync();
        var customer = await _k.CreateCustomerAsync("Inquiry Customer");
        var a = await _k.CreateProductAsync("Asked Widget", purchasePrice: 10m, sellingPrice: 25m);
        var b = await _k.CreateProductAsync("Substitute Widget", purchasePrice: 12m, sellingPrice: 30m);
        var reasons = await _k.Ok(_k.Get("/api/rejection-reasons"), "list rejection reasons");
        var oos = reasons.Items().First(r => r.S("code") == "OOS").G("uuid");
        var eta = DateTime.UtcNow.Date.AddDays(10).ToString("yyyy-MM-dd");

        var inquiry = (await _k.Ok(_k.Post("/api/sale-inquiries", new
        {
            PartnerId = customer.Uuid, CustomerReference = $"{_k.Marker}-RFQ",
            Lines = new object[]
            {
                new { VariantUuid = a.VariantUuid, ProductDescription = "Widget as asked", RequestedQuantity = 5m },
                new { ProductDescription = "A gadget nobody stocks", RequestedQuantity = 3m }
            }
        }), "create inquiry")).GetGuid();
        var inqLines = (await _k.Ok(_k.Get($"/api/sale-inquiries/{inquiry}"), "read inquiry")).A("lines").OrderBy(l => l.I("lineNumber")).ToList();

        await _k.Ok(_k.Patch($"/api/sale-inquiries/{inquiry}/status", new { Status = "UNDER_REVIEW" }), "start review");
        await _k.Ok(_k.Put($"/api/sale-inquiries/{inquiry}/lines/{inqLines[0].G("uuid")}", new
        {
            VariantUuid = a.VariantUuid, ProductDescription = "Widget as asked", RequestedQuantity = 5m,
            LineStatus = "CAN_SUPPLY", EstimatedDeliveryDate = eta
        }), "evaluate line 1");
        await _k.Ok(_k.Put($"/api/sale-inquiries/{inquiry}/lines/{inqLines[1].G("uuid")}", new
        {
            ProductDescription = "A gadget nobody stocks", RequestedQuantity = 3m, LineStatus = "CANNOT_SUPPLY",
            RejectionReasonUuid = oos, AlternativeVariantUuid = b.VariantUuid, AlternativeNotes = "Fits the same slot"
        }), "evaluate line 2");
        await _k.Ok(_k.Patch($"/api/sale-inquiries/{inquiry}/status", new { Status = "REVIEW_COMPLETE" }), "complete review");

        var body = new { CurrencyId = pkr, ValidTo = DateTime.UtcNow.Date.AddDays(20).ToString("yyyy-MM-dd") };
        var both = await Task.WhenAll(
            _k.Post($"/api/sale-inquiries/{inquiry}/create-quotation", body),
            _k.Post($"/api/sale-inquiries/{inquiry}/create-quotation", body));

        both.Select(c => c.Status).Should().BeEquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict },
            $"the inquiry's Status concurrency token lets one through — {string.Join(" | ", both.Select(c => c.ToString()))}");
        var quotationUuid = both.Single(c => c.Status == HttpStatusCode.OK).Result.GetGuid();

        (await _k.Ok(_k.Get($"/api/sale-inquiries/{inquiry}"), "read quoted inquiry")).S("status").Should().Be("QUOTED");
        var count = await _f.QueryAsync(
            "SELECT COUNT(*) AS N FROM demand.sale_quotations q JOIN demand.sale_inquiries i ON i.Id = q.SourceInquiryId WHERE i.UUID = @i",
            ("@i", inquiry));
        count[0]["N"].Should().Be(1, "the losing call's quotation rolled back with its inquiry change");

        var q = await _k.Ok(_k.Get($"/api/sale-quotations/{quotationUuid}"), "read quotation");
        q.S("status").Should().Be("DRAFT");
        q.S("customerReference").Should().Be($"{_k.Marker}-RFQ");
        q.P("sourceInquiry").G("uuid").Should().Be(inquiry);
        var lines = q.A("lines").OrderBy(l => l.I("lineNumber")).ToList();
        lines.Select(l => l.S("lineType")).Should().Equal("NORMAL", "REJECTED", "ALTERNATIVE");
        lines[0].D("quantity").Should().Be(5m);
        lines[0].D("unitPrice").Should().Be(25m);
        lines[0].G("sourceInquiryLineUuid").Should().Be(inqLines[0].G("uuid"));
        lines[1].IsNull("variantUuid").Should().BeTrue();
        lines[1].S("rejectionReasonCode").Should().Be("OOS");
        lines[2].G("variantUuid").Should().Be(b.VariantUuid);
        lines[2].G("alternativeForLineUuid").Should().Be(lines[1].G("uuid"));
        lines[2].D("unitPrice").Should().Be(30m);
    }

    [Fact]
    public async Task A_role_without_sales_codes_is_refused_every_quotation_action()
    {
        var client = await _k.LoginAsNewUserAsync((int)EnumRole.WarehouseOperator, "sq-noperm");
        var any = Guid.NewGuid();

        (await _k.Get("/api/sale-quotations", client)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await _k.Get($"/api/sale-quotations/{any}", client)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await _k.Post("/api/sale-quotations", new { }, client)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await _k.Post($"/api/sale-quotations/{any}/send", null, client)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await _k.Post($"/api/sale-quotations/{any}/accept", null, client)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await _k.Post($"/api/sale-quotations/{any}/convert-to-order", new { }, client)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await _k.Post($"/api/sale-quotations/{any}/copy", null, client)).Status.Should().Be(HttpStatusCode.Forbidden);

        (await _k.Get($"/api/sale-quotations/{any}")).Status.Should().Be(HttpStatusCode.NotFound, "the admin passes the gate; the uuid is unknown");
    }
}
