using System.Net;
using FluentAssertions;
using Hangfire;
using Hangfire.Storage;
using SMS.Integration.Tests.SapAlignment;
using SMS.Modules.Demand.Services;
using Xunit;
using static SMS.Integration.Tests.SalesPreOrder.PreOrder;

namespace SMS.Integration.Tests.SalesPreOrder;

/// <summary>
/// A32-PF-03 — the two dead ends of a quotation on the real host (LocalDB): the customer rejecting every offered line
/// (SENT → REJECTED, BR-C2-08), and the daily QuotationExpiryJob, run in-process from a fresh scope as Hangfire would,
/// expiring SENT quotations past their valid-to date in every organization (SENT → EXPIRED, BR-C2-10 / T-C2-11). Neither
/// end state can be accepted, answered or converted: no sale order is ever made from them.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~QuotationRejectionExpiryE2ETests</c>.</para>
/// </summary>
public sealed class QuotationRejectionExpiryE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public QuotationRejectionExpiryE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "PF3");
    }

    private async Task<int> OrdersFromAsync(Guid quotation) => Convert.ToInt32((await _f.QueryAsync(
        "SELECT COUNT(*) AS N FROM demand.sale_orders o JOIN demand.sale_quotations q ON q.Id = o.SourceQuotationId WHERE q.UUID = @q",
        ("@q", quotation)))[0]["N"]);

    /// <summary>Nothing moves a terminal quotation on, and nothing makes a sale order from it.</summary>
    private async Task AssertDeadEndAsync(SapKit k, Guid quotation, Guid anyLine, string status)
    {
        var q = await k.Ok(k.Get($"/api/sale-quotations/{quotation}"), "read");
        q.S("status").Should().Be(status);
        q.B("isEditable").Should().BeFalse();
        q.A("allowedActions").Select(x => x.GetString()).Should().Equal("COPY");
        q.IsNull("saleOrder").Should().BeTrue();

        var calls = new (string What, Func<Task<Api>> Call)[]
        {
            ("convert",  () => k.Post($"/api/sale-quotations/{quotation}/convert-to-order", new { DeliveryMode = "SELF_PICKUP" })),
            ("accept",   () => k.Post($"/api/sale-quotations/{quotation}/accept")),
            ("reject",   () => k.Post($"/api/sale-quotations/{quotation}/reject", new { Reason = "again" })),
            ("send",     () => k.Post($"/api/sale-quotations/{quotation}/send")),
            ("response", () => k.Patch($"/api/sale-quotations/{quotation}/lines/{anyLine}/customer-response", new { Response = "ACCEPTED" })),
            ("edit",     () => k.Put($"/api/sale-quotations/{quotation}", new { ValidFrom = Day(Today), ValidTo = Day(Today.AddDays(9)) })),
            ("add line", () => k.Post($"/api/sale-quotations/{quotation}/lines", new { LineType = "NORMAL", VariantUuid = Guid.NewGuid(), Quantity = 1m })),
        };
        foreach (var (what, call) in calls)
        {
            var api = await call();
            api.Status.Should().Be(HttpStatusCode.BadRequest, $"{what} on a {status} quotation — {api}");
        }
        (await OrdersFromAsync(quotation)).Should().Be(0, $"no sale order comes from a {status} quotation");
        (await k.Ok(k.Get($"/api/sale-quotations/{quotation}"), "read again")).S("status").Should().Be(status);
    }

    [Fact]
    public async Task A_customer_rejecting_every_offered_line_ends_the_quotation_REJECTED_with_no_way_to_an_order()
    {
        var pkr      = await _k.PkrBaseAsync();
        var customer = await _k.CreateCustomerAsync("Reject Customer");
        var a        = await _k.CreateProductAsync("Rej Widget", purchasePrice: 10m, sellingPrice: 20m);
        var b        = await _k.CreateProductAsync("Rej Gadget", purchasePrice: 15m, sellingPrice: 30m);
        var alt      = await _k.CreateProductAsync("Rej Alt", purchasePrice: 12m, sellingPrice: 25m);
        var moq      = await _k.ReasonAsync("MOQ");

        var quotation = (await _k.Ok(_k.Post("/api/sale-quotations", new
        {
            PartnerId = customer.Uuid, CurrencyId = pkr, ValidTo = Day(Today.AddDays(10)),
            Lines = new object[]
            {
                new { LineType = "NORMAL", VariantUuid = a.VariantUuid, Quantity = 5m },
                new { LineType = "NORMAL", VariantUuid = b.VariantUuid, Quantity = 2m },
                new { LineType = "REJECTED", ProductDescription = "Below our minimum", Quantity = 1m, RejectionReasonUuid = moq },
                new { LineType = "ALTERNATIVE", VariantUuid = alt.VariantUuid, Quantity = 3m, AlternativeForLineNumber = 3 }
            }
        }), "create quotation")).GetGuid();
        await _k.Ok(_k.Post($"/api/sale-quotations/{quotation}/send"), "send");
        var lines = (await _k.Ok(_k.Get($"/api/sale-quotations/{quotation}"), "read")).A("lines").OrderBy(l => l.I("lineNumber")).ToList();
        string Resp(int i) => $"/api/sale-quotations/{quotation}/lines/{lines[i].G("uuid")}/customer-response";

        // Two of three offered lines rejected, one still open: not yet a rejection (BR-C2-08).
        await _k.Ok(_k.Patch(Resp(0), new { Response = "REJECTED", Notes = "too dear" }), "reject line 1");
        await _k.Ok(_k.Patch(Resp(1), new { Response = "REJECTED" }), "reject line 2");
        var early = await _k.Post($"/api/sale-quotations/{quotation}/reject", new { Reason = "Too expensive" });
        early.Status.Should().Be(HttpStatusCode.BadRequest, $"the alternative is still PENDING — {early}");
        early.Message.Should().Contain("4", "the refusal names the open line");

        // An accepted line also blocks the rejection.
        await _k.Ok(_k.Patch(Resp(3), new { Response = "ACCEPTED" }), "accept the alternative");
        (await _k.Post($"/api/sale-quotations/{quotation}/reject", new { Reason = "Too expensive" }))
            .Status.Should().Be(HttpStatusCode.BadRequest, "an ACCEPTED line is not a rejected one");

        await _k.Ok(_k.Patch(Resp(3), new { Response = "REJECTED" }), "reject the alternative after all");
        await _k.Ok(_k.Post($"/api/sale-quotations/{quotation}/reject", new { Reason = "Too expensive" }), "reject the quotation");

        var rejected = await _k.Ok(_k.Get($"/api/sale-quotations/{quotation}"), "read rejected");
        rejected.S("internalNotes").Should().Contain("Too expensive", "the reason is kept with the internal notes");
        rejected.A("lines").Where(l => l.S("lineType") != "REJECTED").Should().OnlyContain(l => l.S("customerResponse") == "REJECTED");
        rejected.A("lines").Single(l => l.S("lineType") == "REJECTED").S("customerResponse").Should().Be("PENDING");

        await AssertDeadEndAsync(_k, quotation, lines[0].G("uuid"), "REJECTED");
        (await _k.Ok(_k.Get("/api/sale-quotations?status=REJECTED&pageSize=100"), "list REJECTED"))
            .A("data").Select(r => r.G("uuid")).Should().Contain(quotation);

        // COPY is still allowed: a new DRAFT to start over from, not linked to any order.
        var copy = (await _k.Ok(_k.Post($"/api/sale-quotations/{quotation}/copy"), "copy the rejected quotation")).GetGuid();
        var copied = await _k.Ok(_k.Get($"/api/sale-quotations/{copy}"), "read the copy");
        copied.S("status").Should().Be("DRAFT");
        copied.S("quotationNumber").Should().NotBe(rejected.S("quotationNumber"));
        copied.A("lines").Should().HaveCount(4);
        copied.A("lines").Should().OnlyContain(l => l.S("customerResponse") == "PENDING");
        (await _k.Ok(_k.Get($"/api/sale-quotations/{quotation}"), "the original")).S("status").Should().Be("REJECTED");
    }

    [Fact]
    public async Task The_expiry_job_expires_only_SENT_quotations_past_valid_to_in_every_organization_and_nothing_converts_after()
    {
        var pkr      = await _k.PkrBaseAsync();
        var customer = await _k.CreateCustomerAsync("Expiry Customer");
        var item     = await _k.CreateProductAsync("Exp Widget", purchasePrice: 10m, sellingPrice: 20m);

        // The job is registered with Hangfire as the contract says.
        using (var conn = JobStorage.Current.GetConnection())
        {
            var job = conn.GetRecurringJobs().SingleOrDefault(j => j.Id == "sale-quotation-expiry");
            job.Should().NotBeNull("QuotationExpiryJob is a recurring Hangfire job");
            job!.Cron.Should().Be("7 1 * * *");
        }

        async Task<Guid> Quotation(SapKit k, Partner c, Guid cur, Product p, DateTime from, DateTime to) =>
            (await k.Ok(k.Post("/api/sale-quotations", new
            {
                PartnerId = c.Uuid, CurrencyId = cur, ValidFrom = Day(from), ValidTo = Day(to),
                Lines = new object[] { new { LineType = "NORMAL", VariantUuid = p.VariantUuid, Quantity = 2m, UnitPrice = 20m } }
            }), "create quotation")).GetGuid();
        async Task Send(SapKit k, Guid q) => await k.Ok(k.Post($"/api/sale-quotations/{q}/send"), "send");

        var pastSent      = await Quotation(_k, customer, pkr, item, Today.AddDays(-10), Today.AddDays(-1));
        await Send(_k, pastSent);
        var todaySent     = await Quotation(_k, customer, pkr, item, Today.AddDays(-10), Today);
        await Send(_k, todaySent);
        var pastDraft     = await Quotation(_k, customer, pkr, item, Today.AddDays(-10), Today.AddDays(-1));
        var pastAccepted  = await Quotation(_k, customer, pkr, item, Today.AddDays(-10), Today.AddDays(-1));
        await Send(_k, pastAccepted);
        var acceptedLine  = (await _k.Ok(_k.Get($"/api/sale-quotations/{pastAccepted}"), "read")).A("lines").Single().G("uuid");
        await _k.Ok(_k.Patch($"/api/sale-quotations/{pastAccepted}/lines/{acceptedLine}/customer-response", new { Response = "ACCEPTED" }), "accept line");
        await _k.Ok(_k.Post($"/api/sale-quotations/{pastAccepted}/accept"), "accept");

        // Another organization's lapsed quotation: the job has no user, so it must walk every organization itself.
        var (_, other, _) = await _k.SecondOrganizationAsync();
        var otherPkr      = await other.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        var otherCustomer = await other.CreateCustomerAsync("Org2 Expiry Customer");
        var otherItem     = await other.CreateProductAsync("Org2 Exp Widget", purchasePrice: 10m, sellingPrice: 20m);
        var otherPast     = await Quotation(other, otherCustomer, otherPkr, otherItem, Today.AddDays(-5), Today.AddDays(-2));
        await Send(other, otherPast);

        // Run the job in-process, from its own scope, twice (a rerun finds nothing left to do).
        await _f.RunInScopeAsync<QuotationExpiryJob>(j => j.RunAsync());
        await _f.RunInScopeAsync<QuotationExpiryJob>(j => j.RunAsync());

        async Task<string?> Status(SapKit k, Guid q) => (await k.Ok(k.Get($"/api/sale-quotations/{q}"), "read")).S("status");
        (await Status(_k, pastSent)).Should().Be("EXPIRED", "T-C2-11: SENT and past valid-to");
        (await Status(_k, todaySent)).Should().Be("SENT", "valid through the end of its valid-to day");
        (await Status(_k, pastDraft)).Should().Be("DRAFT", "only SENT quotations expire");
        (await Status(_k, pastAccepted)).Should().Be("ACCEPTED", "an accepted quotation is the customer's yes; it does not lapse");
        (await Status(other, otherPast)).Should().Be("EXPIRED", "every organization is swept");

        var rows = await _f.QueryAsync("SELECT ModifiedBy FROM demand.sale_quotations WHERE UUID = @q", ("@q", pastSent));
        rows[0]["ModifiedBy"].Should().Be(0, "raised against the system, not a person");

        var expiredLine = (await _k.Ok(_k.Get($"/api/sale-quotations/{pastSent}"), "read")).A("lines").Single().G("uuid");
        await AssertDeadEndAsync(_k, pastSent, expiredLine, "EXPIRED");
        var otherLine = (await other.Ok(other.Get($"/api/sale-quotations/{otherPast}"), "read")).A("lines").Single().G("uuid");
        await AssertDeadEndAsync(other, otherPast, otherLine, "EXPIRED");

        (await _k.Ok(_k.Get("/api/sale-quotations?status=EXPIRED&pageSize=100"), "list EXPIRED"))
            .A("data").Select(r => r.G("uuid")).Should().Contain(pastSent).And.NotContain(otherPast);

        // The accepted one, though past its validity, still converts — expiry is a SENT-only rule.
        var so = (await _k.Ok(_k.Post($"/api/sale-quotations/{pastAccepted}/convert-to-order", new { DeliveryMode = "SELF_PICKUP" }),
            "convert the accepted quotation")).GetGuid();
        (await _k.GetSaleOrderAsync(so)).P("sourceQuotation").G("uuid").Should().Be(pastAccepted);
    }
}
