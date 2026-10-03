using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.SalesPreOrder.PreOrder;

namespace SMS.Integration.Tests.SalesPreOrder;

/// <summary>
/// A32-PF-01 — the whole pre-order chain through the API on the real host (LocalDB): inquiry → line evaluation
/// (CAN_SUPPLY / PARTIAL / CANNOT_SUPPLY with a reason and an alternative) → review complete → quotation (the §4.1
/// mapping) → a second alternative added by hand → send → per-line customer responses, a counter settled → accept →
/// convert with the customer's PO → the sale order linked back to both documents, carrying only the accepted lines and
/// their tax-code snapshot; a second conversion is a 409. Path 2 (quotation without an inquiry) leaves the inquiry
/// link empty.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~SalesPipelineFullChainE2ETests</c>.</para>
/// </summary>
public sealed class SalesPipelineFullChainE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public SalesPipelineFullChainE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "PF1");
    }

    [Fact]
    public async Task Inquiry_to_quotation_to_sale_order_keeps_the_chain_and_carries_only_what_the_customer_accepted()
    {
        var pkr      = await _k.PkrBaseAsync();
        var gst17    = await _k.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        var customer = await _k.CreateCustomerAsync("Chain Customer");
        var a        = await _k.CreateProductAsync("Chain Widget", purchasePrice: 60m, sellingPrice: 100m);
        var b        = await _k.CreateProductAsync("Chain Bolt", purchasePrice: 20m, sellingPrice: 50m);
        var d        = await _k.CreateProductAsync("Chain Alt One", purchasePrice: 40m, sellingPrice: 80m);
        var e        = await _k.CreateProductAsync("Chain Alt Two", purchasePrice: 45m, sellingPrice: 90m);
        var oos      = await _k.ReasonAsync("OOS");
        var eta1     = Day(Today.AddDays(7));
        var eta2     = Day(Today.AddDays(21));

        // ── 1. Inquiry with three lines: a catalog item, a big catalog order, a free-text item we cannot supply.
        var inquiry = (await _k.Ok(_k.Post("/api/sale-inquiries", new
        {
            PartnerId = customer.Uuid, CustomerReference = $"{_k.Marker}-RFQ", ReceivedDate = Day(Today),
            Lines = new object[]
            {
                new { VariantUuid = a.VariantUuid, ProductDescription = "Widget as drawn", RequestedQuantity = 10m },
                new { VariantUuid = b.VariantUuid, ProductDescription = "Bolts, M8", RequestedQuantity = 1000m },
                new { ProductDescription = "Gizmo X (customer drawing)", RequestedQuantity = 4m }
            }
        }), "create inquiry")).GetGuid();
        var inq = await _k.Ok(_k.Get($"/api/sale-inquiries/{inquiry}"), "read inquiry");
        inq.S("status").Should().Be("RECEIVED");
        var inqNumber = inq.S("inquiryNumber")!;
        var il = inq.A("lines").OrderBy(l => l.I("lineNumber")).Select(l => l.G("uuid")).ToList();
        il.Should().HaveCount(3);

        await _k.Ok(_k.Patch($"/api/sale-inquiries/{inquiry}/status", new { Status = "UNDER_REVIEW" }), "start review");

        // ── 2. Evaluate. The rules refuse a reasonless CANNOT_SUPPLY and a PARTIAL that is not partial.
        (await _k.Put($"/api/sale-inquiries/{inquiry}/lines/{il[2]}", new
        {
            ProductDescription = "Gizmo X (customer drawing)", RequestedQuantity = 4m, LineStatus = "CANNOT_SUPPLY"
        })).Status.Should().Be(HttpStatusCode.BadRequest, "BR-C1-04: CANNOT_SUPPLY needs a rejection reason");
        (await _k.Put($"/api/sale-inquiries/{inquiry}/lines/{il[1]}", new
        {
            VariantUuid = b.VariantUuid, ProductDescription = "Bolts, M8", RequestedQuantity = 1000m,
            LineStatus = "PARTIAL", CanSupplyQuantity = 1000m, EstimatedDeliveryDate = eta2
        })).Status.Should().Be(HttpStatusCode.BadRequest, "BR-C1-05: PARTIAL needs 0 < can-supply < requested");

        await _k.Ok(_k.Put($"/api/sale-inquiries/{inquiry}/lines/{il[0]}", new
        {
            VariantUuid = a.VariantUuid, ProductDescription = "Widget as drawn", RequestedQuantity = 10m,
            LineStatus = "CAN_SUPPLY", EstimatedDeliveryDate = eta1
        }), "line 1 CAN_SUPPLY");
        await _k.Ok(_k.Put($"/api/sale-inquiries/{inquiry}/lines/{il[1]}", new
        {
            VariantUuid = b.VariantUuid, ProductDescription = "Bolts, M8", RequestedQuantity = 1000m,
            LineStatus = "PARTIAL", CanSupplyQuantity = 600m, EstimatedDeliveryDate = eta2
        }), "line 2 PARTIAL 600 of 1000");
        await _k.Ok(_k.Put($"/api/sale-inquiries/{inquiry}/lines/{il[2]}", new
        {
            ProductDescription = "Gizmo X (customer drawing)", RequestedQuantity = 4m, LineStatus = "CANNOT_SUPPLY",
            RejectionReasonUuid = oos, RejectionNotes = "Supplier discontinued it",
            AlternativeVariantUuid = d.VariantUuid, AlternativeNotes = "Same footprint"
        }), "line 3 CANNOT_SUPPLY + alternative");

        var reviewed = await _k.Ok(_k.Patch($"/api/sale-inquiries/{inquiry}/status", new { Status = "REVIEW_COMPLETE" }), "complete review");
        reviewed.S("status").Should().Be("REVIEW_COMPLETE");
        reviewed.A("allowedNextStatuses").Select(x => x.GetString()).Should().NotContain("QUOTED", "QUOTED is reached only by creating a quotation");

        // ── 3. Quotation from the inquiry: the §4.1 mapping.
        var quotation = (await _k.Ok(_k.Post($"/api/sale-inquiries/{inquiry}/create-quotation", new
        {
            CurrencyId = pkr, ValidFrom = Day(Today), ValidTo = Day(Today.AddDays(30)), PaymentTerms = "30 days"
        }), "create quotation from the inquiry")).GetGuid();

        var quoted = await _k.Ok(_k.Get($"/api/sale-inquiries/{inquiry}"), "read the quoted inquiry");
        quoted.S("status").Should().Be("QUOTED", "BR-C1-07");
        quoted.B("isEditable").Should().BeFalse("BR-C1-06");
        quoted.A("quotations").Select(x => x.G("uuid")).Should().Equal(quotation);

        var draft = await _k.Ok(_k.Get($"/api/sale-quotations/{quotation}"), "read the draft quotation");
        draft.S("status").Should().Be("DRAFT");
        draft.P("sourceInquiry").G("uuid").Should().Be(inquiry);
        draft.P("sourceInquiry").S("number").Should().Be(inqNumber);
        draft.S("customerReference").Should().Be($"{_k.Marker}-RFQ");
        var sqNumber = draft.S("quotationNumber")!;
        sqNumber.Should().MatchRegex($@"^SQ-{Today.Year}-\d{{5}}$");

        var ql = draft.A("lines").OrderBy(l => l.I("lineNumber")).ToList();
        ql.Select(l => l.S("lineType")).Should().Equal("NORMAL", "NORMAL", "REJECTED", "ALTERNATIVE");

        ql[0].G("variantUuid").Should().Be(a.VariantUuid);
        ql[0].D("quantity").Should().Be(10m, "CAN_SUPPLY quotes the requested quantity");
        ql[0].D("unitPrice").Should().Be(100m, "the sale-price waterfall");
        ql[0].G("sourceInquiryLineUuid").Should().Be(il[0]);
        ql[0].S("promisedDeliveryDate").Should().StartWith(eta1);
        ql[0].NG("taxCodeUuid").Should().Be(gst17, "a generated line gets the org's default sales tax code");
        ql[0].S("taxCode").Should().Be("GST17");
        ql[0].D("taxPercent").Should().Be(17m);

        ql[1].G("variantUuid").Should().Be(b.VariantUuid);
        ql[1].D("quantity").Should().Be(600m, "PARTIAL quotes the can-supply quantity");
        ql[1].D("unitPrice").Should().Be(50m);
        ql[1].G("sourceInquiryLineUuid").Should().Be(il[1]);
        ql[1].S("promisedDeliveryDate").Should().StartWith(eta2);

        var rejectedLine = ql[2].G("uuid");
        ql[2].IsNull("variantUuid").Should().BeTrue("the free-text line had no catalog item");
        ql[2].S("rejectionReasonCode").Should().Be("OOS");
        ql[2].S("rejectionNotes").Should().Be("Supplier discontinued it");
        ql[2].D("quantity").Should().Be(4m);
        ql[2].D("unitPrice").Should().Be(0m, "a REJECTED line carries no price");
        ql[2].G("sourceInquiryLineUuid").Should().Be(il[2]);

        ql[3].G("variantUuid").Should().Be(d.VariantUuid);
        ql[3].D("quantity").Should().Be(4m, "the alternative is offered for the requested quantity");
        ql[3].D("unitPrice").Should().Be(80m);
        ql[3].G("alternativeForLineUuid").Should().Be(rejectedLine);
        ql[3].S("alternativeNotes").Should().Be("Same footprint");
        ql[3].G("sourceInquiryLineUuid").Should().Be(il[2]);

        // ── 4. A second alternative for the rejected line, by hand; an alternative for a NORMAL line is refused.
        (await _k.Post($"/api/sale-quotations/{quotation}/lines", new
        {
            LineType = "ALTERNATIVE", VariantUuid = e.VariantUuid, Quantity = 4m, AlternativeForLineUuid = ql[0].G("uuid")
        })).Status.Should().Be(HttpStatusCode.BadRequest, "BR-C2-06: an alternative must point to a REJECTED line");

        var altTwo = (await _k.Ok(_k.Post($"/api/sale-quotations/{quotation}/lines", new
        {
            LineType = "ALTERNATIVE", VariantUuid = e.VariantUuid, Quantity = 4m, AlternativeForLineUuid = rejectedLine,
            AlternativeNotes = "Premium option", TaxCodeUuid = gst17
        }), "add a second alternative")).GetGuid();
        var withTwo = await _k.Ok(_k.Get($"/api/sale-quotations/{quotation}"), "read quotation");
        withTwo.A("lines").Should().HaveCount(5);
        var alt2 = withTwo.Line(altTwo);
        alt2.I("lineNumber").Should().Be(5);
        alt2.G("alternativeForLineUuid").Should().Be(rejectedLine);
        alt2.D("unitPrice").Should().Be(90m);
        withTwo.A("lines").Count(l => l.S("lineType") == "ALTERNATIVE" && l.NG("alternativeForLineUuid") == rejectedLine)
            .Should().Be(2, "one rejected line, two alternatives offered");

        // ── 5. Send.
        await _k.Ok(_k.Post($"/api/sale-quotations/{quotation}/send"), "send");
        var sent = await _k.Ok(_k.Get($"/api/sale-quotations/{quotation}"), "read sent quotation");
        sent.S("status").Should().Be("SENT");
        sent.IsNull("sentAt").Should().BeFalse("BR-C2-04 stamps sentAt");
        sent.B("isEditable").Should().BeFalse();
        sent.A("allowedActions").Select(x => x.GetString()).Should().Contain(new[] { "RECORD_RESPONSE", "ACCEPT", "REJECT" });
        (await _k.Post($"/api/sale-quotations/{quotation}/lines", new { LineType = "NORMAL", VariantUuid = a.VariantUuid, Quantity = 1m }))
            .Status.Should().Be(HttpStatusCode.BadRequest, "BR-C2-05: no line changes once SENT");

        // ── 6. The customer answers line by line.
        string Resp(Guid line) => $"/api/sale-quotations/{quotation}/lines/{line}/customer-response";
        (await _k.Patch(Resp(rejectedLine), new { Response = "ACCEPTED" }))
            .Status.Should().Be(HttpStatusCode.BadRequest, "the seller's REJECTED line takes no customer response");
        (await _k.Patch(Resp(ql[1].G("uuid")), new { Response = "COUNTER" }))
            .Status.Should().Be(HttpStatusCode.BadRequest, "BR-C2-09: COUNTER needs a counter price");

        await _k.Ok(_k.Patch(Resp(ql[0].G("uuid")), new { Response = "ACCEPTED", Notes = "ok" }), "line 1 ACCEPTED");
        await _k.Ok(_k.Patch(Resp(ql[1].G("uuid")), new { Response = "COUNTER", CounterPrice = 45m, Notes = "at 45 we buy" }), "line 2 COUNTER");
        await _k.Ok(_k.Patch(Resp(ql[3].G("uuid")), new { Response = "REJECTED" }), "first alternative REJECTED");
        await _k.Ok(_k.Patch(Resp(altTwo), new { Response = "ACCEPTED" }), "second alternative ACCEPTED");

        var countered = (await _k.Ok(_k.Get($"/api/sale-quotations/{quotation}"), "read")).Line(ql[1].G("uuid"));
        countered.S("customerResponse").Should().Be("COUNTER");
        countered.ND("customerCounterPrice").Should().Be(45m);
        countered.D("unitPrice").Should().Be(50m, "a counter does not change the quoted price by itself");

        // Settle the counter: the seller takes the customer's price.
        await _k.Ok(_k.Patch(Resp(ql[1].G("uuid")), new { Response = "ACCEPTED", AcceptCounterPrice = true }), "accept the counter price");
        var settled = await _k.Ok(_k.Get($"/api/sale-quotations/{quotation}"), "read");
        var l2 = settled.Line(ql[1].G("uuid"));
        l2.S("customerResponse").Should().Be("ACCEPTED");
        l2.D("unitPrice").Should().Be(45m);
        l2.D("lineTotal").Should().Be(31590m, "600 × 45 + 17%");
        settled.Line(ql[0].G("uuid")).S("customerResponseDate").Should().StartWith(Day(Today));

        // ── 7. Accept and convert with the customer's PO.
        await _k.Ok(_k.Post($"/api/sale-quotations/{quotation}/accept"), "accept the quotation");
        (await _k.Ok(_k.Get($"/api/sale-quotations/{quotation}"), "read")).S("status").Should().Be("ACCEPTED");

        var poRef = $"{_k.Marker}-CPO-1";
        var convertBody = new
        {
            DeliveryMode = "SELF_PICKUP", CustomerPoReference = poRef, CustomerPoDate = Day(Today.AddDays(-1)),
            ExpectedDeliveryDate = Day(Today.AddDays(21)), Notes = "from the quotation"
        };
        var soUuid = (await _k.Ok(_k.Post($"/api/sale-quotations/{quotation}/convert-to-order", convertBody), "convert")).GetGuid();

        var so = await _k.GetSaleOrderAsync(soUuid);
        so.S("status").Should().Be("DRAFT");
        so.S("sourceType").Should().Be("FROM_QUOTATION");
        so.P("sourceQuotation").G("uuid").Should().Be(quotation);
        so.P("sourceQuotation").S("number").Should().Be(sqNumber);
        so.P("sourceInquiry").G("uuid").Should().Be(inquiry, "BR-C3-03: the inquiry is chained from the quotation");
        so.P("sourceInquiry").S("number").Should().Be(inqNumber);
        so.G("partnerId").Should().Be(customer.Uuid);
        so.G("currencyId").Should().Be(pkr);
        so.S("customerPoReference").Should().Be(poRef);
        so.S("customerPoDate").Should().StartWith(Day(Today.AddDays(-1)));

        var soLines = so.A("lines");
        soLines.Select(l => l.G("variantUuid")).Should().BeEquivalentTo(new[] { a.VariantUuid, b.VariantUuid, e.VariantUuid },
            "only ACCEPTED lines convert: not the seller's REJECTED line, not the alternative the customer turned down");
        var soA = soLines.Single(l => l.G("variantUuid") == a.VariantUuid);
        soA.D("quantity").Should().Be(10m);
        soA.D("unitPrice").Should().Be(100m);
        soA.NG("taxCodeUuid").Should().Be(gst17, "the tax-code snapshot is copied");
        soA.S("taxCode").Should().Be("GST17");
        soA.D("taxPercent").Should().Be(17m);
        soA.D("lineTotal").Should().Be(1170m);
        var soB = soLines.Single(l => l.G("variantUuid") == b.VariantUuid);
        soB.D("quantity").Should().Be(600m);
        soB.D("unitPrice").Should().Be(45m, "the settled counter price, not re-resolved");
        soB.NG("taxCodeUuid").Should().Be(gst17);
        var soE = soLines.Single(l => l.G("variantUuid") == e.VariantUuid);
        soE.D("quantity").Should().Be(4m);
        soE.D("unitPrice").Should().Be(90m);
        soE.NG("taxCodeUuid").Should().Be(gst17);
        so.D("grandTotal").Should().Be(33181.2m, "28,360 + 17%");

        var rows = await _f.QueryAsync(
            "SELECT o.SourceType, q.UUID AS Q, i.UUID AS I FROM demand.sale_orders o " +
            "JOIN demand.sale_quotations q ON q.Id = o.SourceQuotationId JOIN demand.sale_inquiries i ON i.Id = o.SourceInquiryId " +
            "WHERE o.UUID = @so", ("@so", soUuid));
        rows.Should().ContainSingle();
        rows[0]["Q"].Should().Be(quotation);
        rows[0]["I"].Should().Be(inquiry);

        var converted = await _k.Ok(_k.Get($"/api/sale-quotations/{quotation}"), "read the converted quotation");
        converted.S("status").Should().Be("CONVERTED");
        converted.P("saleOrder").G("uuid").Should().Be(soUuid);
        converted.A("allowedActions").Select(x => x.GetString()).Should().NotContain("CONVERT");

        // ── 8. A second conversion is a conflict, and makes nothing.
        var again = await _k.Post($"/api/sale-quotations/{quotation}/convert-to-order", convertBody);
        again.Status.Should().Be(HttpStatusCode.Conflict, $"BR-C2-11: one quotation, one order — {again}");
        (await _f.QueryAsync("SELECT COUNT(*) AS N FROM demand.sale_orders o JOIN demand.sale_quotations q ON q.Id = o.SourceQuotationId WHERE q.UUID = @q",
            ("@q", quotation)))[0]["N"].Should().Be(1);

        // The lists find the order by its source and by the customer's PO; the quotation list by its inquiry.
        (await _k.Ok(_k.Get("/api/sale-orders?sourceType=FROM_QUOTATION&pageSize=100"), "SO list by source"))
            .A("data").Select(r => r.G("uuid")).Should().Contain(soUuid);
        (await _k.Ok(_k.Get("/api/sale-orders?sourceType=MANUAL&pageSize=100"), "SO list, manual only"))
            .A("data").Select(r => r.G("uuid")).Should().NotContain(soUuid);
        (await _k.Ok(_k.Get($"/api/sale-orders?search={poRef}"), "SO list by customer PO"))
            .A("data").Select(r => r.G("uuid")).Should().Equal(soUuid);
        (await _k.Ok(_k.Get($"/api/sale-quotations?sourceInquiryUuid={inquiry}"), "quotation list by inquiry"))
            .A("data").Select(r => r.G("uuid")).Should().Equal(quotation);
    }

    [Fact]
    public async Task Path_2_a_quotation_without_an_inquiry_converts_to_an_order_with_no_inquiry_link()
    {
        var pkr      = await _k.PkrBaseAsync();
        var customer = await _k.CreateCustomerAsync("Path2 Customer");
        var item     = await _k.CreateProductAsync("Path2 Widget", purchasePrice: 10m, sellingPrice: 25m);

        var quotation = await _k.AcceptedQuotationAsync(customer, pkr, item, 3m);
        var q = await _k.Ok(_k.Get($"/api/sale-quotations/{quotation}"), "read");
        q.IsNull("sourceInquiry").Should().BeTrue("T-C2-01: an independent quotation");

        var so = (await _k.Ok(_k.Post($"/api/sale-quotations/{quotation}/convert-to-order", new { DeliveryMode = "SELF_PICKUP" }), "convert")).GetGuid();
        var order = await _k.GetSaleOrderAsync(so);
        order.S("sourceType").Should().Be("FROM_QUOTATION");
        order.P("sourceQuotation").G("uuid").Should().Be(quotation);
        order.IsNull("sourceInquiry").Should().BeTrue();
        order.IsNull("customerPoReference").Should().BeTrue();
        order.A("lines").Should().ContainSingle().Which.D("unitPrice").Should().Be(25m);
    }
}
