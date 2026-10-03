using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.SalesPreOrder.PreOrder;

namespace SMS.Integration.Tests.SalesPreOrder;

/// <summary>
/// A32-PF-05 — two organizations side by side on the real host (LocalDB), each with an inquiry, quotations (draft,
/// sent, accepted, converted), sale orders (converted and direct, confirmed), a customer PO file and its rejection
/// reasons. Neither can read or change the other's records through any A32 endpoint — the platform super admin
/// included, who bypasses the EF tenant filter: every by-uuid call is a 404, lists leave the other out, and the other
/// organization's customers, items, reasons and files cannot be borrowed. Rejection-reason codes and INQ/SQ numbering
/// are per organization.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~SalesPreOrderTenantIsolationE2ETests</c>.</para>
/// </summary>
public sealed class SalesPreOrderTenantIsolationE2ETests : IClassFixture<SapWebApplicationFactory>, IDisposable
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "sms-a32-qa-" + Guid.NewGuid().ToString("N"), "wwwroot");

    public SalesPreOrderTenantIsolationE2ETests(SapWebApplicationFactory factory)
    {
        _f    = factory;
        _root = new SapKit(factory, "PF5");
        Directory.CreateDirectory(_webRoot);
        factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath = _webRoot;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_webRoot)!, recursive: true); } catch { /* best effort */ }
    }

    private sealed record OrgDocs(
        Guid OrgId, Partner Customer, Product Item, Guid Pkr, Guid Reason,
        Guid Inquiry, Guid InquiryLine, Guid Draft, Guid DraftLine, Guid Sent, Guid SentLine, Guid Accepted,
        Guid Converted, Guid ConvertedSo, Guid SoLine, Guid DirectSo, Guid DirectLine, string PoRef, Guid PoFile, Guid LegacySo);

    private async Task<Guid> UploadAsync(SapKit k, string interfaceCode, Guid documentId)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF"));
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");
        form.Add(file, "file", "po.pdf");
        form.Add(new StringContent(interfaceCode), "interfaceCode");
        form.Add(new StringContent(documentId.ToString()), "documentId");
        using var resp = await k.Admin.PostAsync("/api/attachments/upload", form);
        var raw = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK, raw);
        return JsonDocument.Parse(raw).RootElement.GetProperty("result").GetGuid();
    }

    /// <summary>One organization's full set of A32 documents, made by its own admin.</summary>
    private async Task<OrgDocs> BuildAsync(SapKit k, Guid orgId, Guid pkr)
    {
        var customer = await k.CreateCustomerAsync("Iso Customer");
        var item     = await k.CreateProductAsync("Iso Widget", purchasePrice: 10m, sellingPrice: 20m);
        var reason   = await k.ReasonAsync("OOS");

        var inquiry = (await k.Ok(k.Post("/api/sale-inquiries", new
        {
            PartnerId = customer.Uuid, Lines = new object[] { new { VariantUuid = item.VariantUuid, ProductDescription = "Iso", RequestedQuantity = 5m } }
        }), "inquiry")).GetGuid();
        var inquiryLine = (await k.Ok(k.Get($"/api/sale-inquiries/{inquiry}"), "read")).A("lines").Single().G("uuid");

        var draft = await k.DraftQuotationAsync(customer, pkr, item, 2m);
        var draftLine = (await k.Ok(k.Get($"/api/sale-quotations/{draft}"), "read")).A("lines").Single().G("uuid");
        var sent = await k.DraftQuotationAsync(customer, pkr, item, 3m);
        await k.Ok(k.Post($"/api/sale-quotations/{sent}/send"), "send");
        var sentLine = (await k.Ok(k.Get($"/api/sale-quotations/{sent}"), "read")).A("lines").Single().G("uuid");
        var accepted = await k.AcceptedQuotationAsync(customer, pkr, item, 4m);

        var reviewed  = await k.ReviewedInquiryAsync(customer, item, 6m);
        var converted = (await k.Ok(k.Post($"/api/sale-inquiries/{reviewed}/create-quotation", new { CurrencyId = pkr, ValidTo = Day(Today.AddDays(9)) }), "quote")).GetGuid();
        await k.Ok(k.Post($"/api/sale-quotations/{converted}/send"), "send");
        var cl = (await k.Ok(k.Get($"/api/sale-quotations/{converted}"), "read")).A("lines").Single().G("uuid");
        await k.Ok(k.Patch($"/api/sale-quotations/{converted}/lines/{cl}/customer-response", new { Response = "ACCEPTED" }), "accept line");
        await k.Ok(k.Post($"/api/sale-quotations/{converted}/accept"), "accept");
        var poRef = $"{k.Marker}-PO";
        var convertedSo = (await k.Ok(k.Post($"/api/sale-quotations/{converted}/convert-to-order", new { DeliveryMode = "SELF_PICKUP", CustomerPoReference = poRef }), "convert")).GetGuid();
        var soLine = (await k.GetSaleOrderAsync(convertedSo)).A("lines").Single().G("uuid");
        var poFile = await UploadAsync(k, "CUSTOMER_PO", convertedSo);
        await k.Ok(k.Put($"/api/sale-orders/{convertedSo}/customer-po", new { CustomerPoReference = poRef, CustomerPoAttachmentUuid = poFile }), "link PO file");

        var direct = await k.CreateSaleOrderAsync(customer, pkr, SapKit.Line(item, 2m));
        await k.Ok(k.Post($"/api/sale-orders/{direct}/confirm"), "confirm (no stock)");
        var directLine = (await k.GetSaleOrderAsync(direct)).A("lines").Single().G("uuid");
        (await k.Ok(k.Post($"/api/sale-orders/{direct}/lines/{directLine}/reserve", new { }), "own reserve passes"))
            .S("outcome").Should().Be("NONE_AVAILABLE");

        var legacy = await k.CreateSaleOrderAsync(customer, pkr, SapKit.Line(item, 1m));

        return new OrgDocs(orgId, customer, item, pkr, reason, inquiry, inquiryLine, draft, draftLine, sent, sentLine, accepted,
            converted, convertedSo, soLine, direct, directLine, poRef, poFile, legacy);
    }

    /// <summary>Every by-uuid A32 call an <paramref name="actor"/> could aim at <paramref name="v"/>'s records.</summary>
    private static (string What, Func<Task<Api>> Call)[] CrossCalls(SapKit actor, OrgDocs v) =>
    [
        ("inquiry read",             () => actor.Get($"/api/sale-inquiries/{v.Inquiry}")),
        ("inquiry update",           () => actor.Put($"/api/sale-inquiries/{v.Inquiry}", new { ReceivedDate = Day(Today), Notes = "mine" })),
        ("inquiry add line",         () => actor.Post($"/api/sale-inquiries/{v.Inquiry}/lines", new { ProductDescription = "x", RequestedQuantity = 1 })),
        ("inquiry update line",      () => actor.Put($"/api/sale-inquiries/{v.Inquiry}/lines/{v.InquiryLine}", new { ProductDescription = "x", RequestedQuantity = 1, LineStatus = "UNDER_REVIEW" })),
        ("inquiry delete line",      () => actor.Delete($"/api/sale-inquiries/{v.Inquiry}/lines/{v.InquiryLine}")),
        ("inquiry status",           () => actor.Patch($"/api/sale-inquiries/{v.Inquiry}/status", new { Status = "UNDER_REVIEW" })),
        ("inquiry create-quotation", () => actor.Post($"/api/sale-inquiries/{v.Inquiry}/create-quotation", new { ValidTo = Day(Today.AddDays(5)) })),
        ("quotation read",           () => actor.Get($"/api/sale-quotations/{v.Draft}")),
        ("quotation update",         () => actor.Put($"/api/sale-quotations/{v.Draft}", new { ValidFrom = Day(Today), ValidTo = Day(Today.AddDays(5)), Notes = "mine" })),
        ("quotation add line",       () => actor.Post($"/api/sale-quotations/{v.Draft}/lines", new { LineType = "NORMAL", VariantUuid = v.Item.VariantUuid, Quantity = 1m, UnitPrice = 1m })),
        ("quotation update line",    () => actor.Put($"/api/sale-quotations/{v.Draft}/lines/{v.DraftLine}", new { LineType = "NORMAL", VariantUuid = v.Item.VariantUuid, Quantity = 9m, UnitPrice = 1m })),
        ("quotation delete line",    () => actor.Delete($"/api/sale-quotations/{v.Draft}/lines/{v.DraftLine}")),
        ("quotation send",           () => actor.Post($"/api/sale-quotations/{v.Draft}/send")),
        ("quotation copy",           () => actor.Post($"/api/sale-quotations/{v.Draft}/copy")),
        ("quotation response",       () => actor.Patch($"/api/sale-quotations/{v.Sent}/lines/{v.SentLine}/customer-response", new { Response = "ACCEPTED" })),
        ("quotation accept",         () => actor.Post($"/api/sale-quotations/{v.Sent}/accept")),
        ("quotation reject",         () => actor.Post($"/api/sale-quotations/{v.Sent}/reject", new { Reason = "mine" })),
        ("quotation convert",        () => actor.Post($"/api/sale-quotations/{v.Accepted}/convert-to-order", new { DeliveryMode = "SELF_PICKUP" })),
        ("converted read",           () => actor.Get($"/api/sale-quotations/{v.Converted}")),
        ("reason update",            () => actor.Put($"/api/rejection-reasons/{v.Reason}", new { Description = "mine", DisplayOrder = 1 })),
        ("reason deactivate",        () => actor.Patch($"/api/rejection-reasons/{v.Reason}/deactivate", null)),
        ("reason activate",          () => actor.Patch($"/api/rejection-reasons/{v.Reason}/activate", null)),
        ("reason delete",            () => actor.Delete($"/api/rejection-reasons/{v.Reason}")),
        ("SO read",                  () => actor.Get($"/api/sale-orders/{v.ConvertedSo}")),
        ("SO customer-po",           () => actor.Put($"/api/sale-orders/{v.ConvertedSo}/customer-po", new { CustomerPoReference = "MINE" })),
        ("SO reserve",               () => actor.Post($"/api/sale-orders/{v.DirectSo}/lines/{v.DirectLine}/reserve", new { })),
        ("SO release",               () => actor.Post($"/api/sale-orders/{v.DirectSo}/lines/{v.DirectLine}/release", new { })),
        ("SO reserve-all",           () => actor.Post($"/api/sale-orders/{v.DirectSo}/reserve-all", new { })),
        // The sale order endpoints A32 extended but did not add (contract §7: GET/PUT/cancel "existing").
        ("SO timeline",              () => actor.Get($"/api/sale-orders/{v.LegacySo}/timeline")),
        ("SO availability",          () => actor.Get($"/api/sale-orders/{v.LegacySo}/availability")),
        ("SO update (draft)",        () => actor.Put($"/api/sale-orders/{v.LegacySo}", new { DeliveryMode = "SELF_PICKUP", CurrencyId = v.Pkr, Notes = "mine", CustomerPoReference = "MINE", Lines = new[] { SapKit.Line(v.Item, 9m) } })),
        ("SO confirm",               () => actor.Post($"/api/sale-orders/{v.LegacySo}/confirm")),
        ("SO cancel",                () => actor.Post($"/api/sale-orders/{v.LegacySo}/cancel", new { Reason = "mine" })),
    ];

    private async Task AssertUntouchedAsync(SapKit owner, OrgDocs d)
    {
        var inq = await owner.Ok(owner.Get($"/api/sale-inquiries/{d.Inquiry}"), "owner reads inquiry");
        (inq.S("status"), inq.IsNull("notes"), inq.A("lines").Count, inq.A("lines")[0].S("lineStatus")).Should().Be(("RECEIVED", true, 1, "PENDING"));
        var draft = await owner.Ok(owner.Get($"/api/sale-quotations/{d.Draft}"), "owner reads draft");
        (draft.S("status"), draft.IsNull("notes"), draft.A("lines").Count, draft.A("lines")[0].D("quantity")).Should().Be(("DRAFT", true, 1, 2m));
        var sent = await owner.Ok(owner.Get($"/api/sale-quotations/{d.Sent}"), "owner reads sent");
        (sent.S("status"), sent.A("lines")[0].S("customerResponse")).Should().Be(("SENT", "PENDING"));
        (await owner.Ok(owner.Get($"/api/sale-quotations/{d.Accepted}"), "owner reads accepted")).S("status").Should().Be("ACCEPTED");
        var reason = (await owner.Ok(owner.Get("/api/rejection-reasons"), "owner reasons")).Items().Single(r => r.G("uuid") == d.Reason);
        reason.S("description").Should().NotBe("mine");
        reason.B("isActive").Should().BeTrue();
        var so = await owner.GetSaleOrderAsync(d.ConvertedSo);
        (so.S("customerPoReference"), so.NG("customerPoAttachmentUuid")).Should().Be((d.PoRef, (Guid?)d.PoFile));
        (await owner.GetSaleOrderAsync(d.DirectSo)).A("lines").Single().D("reservedQty").Should().Be(0m);
        var legacy = await owner.GetSaleOrderAsync(d.LegacySo);
        (legacy.S("status"), legacy.IsNull("notes"), legacy.A("lines").Single().D("quantity")).Should().Be(("DRAFT", true, 1m));
        (await _f.QueryAsync("SELECT COUNT(*) AS N FROM demand.sale_orders o JOIN demand.sale_quotations q ON q.Id = o.SourceQuotationId WHERE q.UUID = @q",
            ("@q", d.Accepted)))[0]["N"].Should().Be(0, "nobody converted the accepted quotation");
    }

    private static async Task AssertListsExcludeAsync(SapKit actor, OrgDocs v, string who)
    {
        (await actor.Ok(actor.Get("/api/sale-inquiries?pageSize=100"), "inquiries")).A("data").Select(r => r.G("uuid"))
            .Should().NotContain(v.Inquiry, who);
        (await actor.Ok(actor.Get("/api/sale-quotations?pageSize=100"), "quotations")).A("data").Select(r => r.G("uuid"))
            .Should().NotContain(new[] { v.Draft, v.Sent, v.Accepted, v.Converted }, who);
        (await actor.Ok(actor.Get("/api/sale-orders?pageSize=100"), "orders")).A("data").Select(r => r.G("uuid"))
            .Should().NotContain(new[] { v.ConvertedSo, v.DirectSo }, who);
        (await actor.Ok(actor.Get($"/api/sale-orders?search={v.PoRef}"), "orders by the other's PO")).A("data").Should().BeEmpty(who);
        (await actor.Ok(actor.Get($"/api/sale-orders/customer-po-check?reference={v.PoRef}"), "PO check")).Items()
            .Should().BeEmpty($"{who}: another organization's PO numbers do not leak through the duplicate warning");
        (await actor.Ok(actor.Get("/api/rejection-reasons?includeInactive=true"), "reasons")).Items().Select(r => r.G("uuid"))
            .Should().NotContain(v.Reason, who);
    }

    [Fact]
    public async Task Neither_organization_reads_or_changes_the_others_pre_order_records_super_admin_included()
    {
        var pkr = await _root.PkrBaseAsync();
        var (org2Id, org2, _) = await _root.SecondOrganizationAsync();
        await _root.SetOrgBaseCurrencyAsync(org2Id, pkr);

        var mine   = await BuildAsync(_root, _f.OrganizationId, pkr);
        var theirs = await BuildAsync(org2, org2Id, pkr);

        // The records really are in two organizations.
        var orgs = await _f.QueryAsync("SELECT UUID, OrganizationId FROM demand.sale_orders WHERE UUID IN (@a, @b)",
            ("@a", mine.ConvertedSo), ("@b", theirs.ConvertedSo));
        orgs.Single(r => (Guid)r["UUID"]! == mine.ConvertedSo)["OrganizationId"].Should().Be(_f.OrganizationId);
        orgs.Single(r => (Guid)r["UUID"]! == theirs.ConvertedSo)["OrganizationId"].Should().Be(org2Id);

        // Every leak is collected before failing, so one run names them all.
        var leaks = new List<string>();
        foreach (var (actor, victim, who) in new[] { (_root, theirs, "super admin → org 2"), (org2, mine, "org 2 admin → org 1") })
        {
            foreach (var (what, call) in CrossCalls(actor, victim))
            {
                var api = await call();
                if (api.Status != HttpStatusCode.NotFound) leaks.Add($"{who}: {what} — {api.ToString()[..Math.Min(220, api.ToString().Length)]}");
            }
            try { await AssertListsExcludeAsync(actor, victim, who); }
            catch (Exception ex) { leaks.Add($"{who}: lists — {ex.Message[..Math.Min(300, ex.Message.Length)]}"); }

            var files = await actor.Get($"/api/attachments?interface=CUSTOMER_PO&documentId={victim.ConvertedSo}");
            if (files.Status == HttpStatusCode.OK && files.Result.Items().Any(x => x.G("uuid") == victim.PoFile))
                leaks.Add($"{who}: the other's CUSTOMER_PO file is listed by api/attachments");
        }
        leaks.Should().BeEmpty("no cross-organization read or write may succeed. LEAKS:" + Environment.NewLine + string.Join(Environment.NewLine, leaks));
        await AssertUntouchedAsync(_root, mine);
        await AssertUntouchedAsync(org2, theirs);

        // Nor can the other's customers, items, reasons or files be borrowed for one's own records.
        foreach (var (actor, own, other, who) in new[] { (_root, mine, theirs, "super admin"), (org2, theirs, mine, "org 2 admin") })
        {
            (await actor.Post("/api/sale-inquiries", new { PartnerId = other.Customer.Uuid }))
                .Status.Should().Be(HttpStatusCode.BadRequest, $"{who}: another organization's customer is not a customer here");
            (await actor.Post("/api/sale-quotations", new { PartnerId = other.Customer.Uuid, CurrencyId = pkr, ValidTo = Day(Today.AddDays(5)) }))
                .Status.Should().Be(HttpStatusCode.BadRequest, $"{who}: quotation for the other's customer");
            await actor.Ok(actor.Patch($"/api/sale-inquiries/{own.Inquiry}/status", new { Status = "UNDER_REVIEW" }), "own review");
            var foreignReason = await actor.Put($"/api/sale-inquiries/{own.Inquiry}/lines/{own.InquiryLine}", new
            {
                VariantUuid = own.Item.VariantUuid, ProductDescription = "Iso", RequestedQuantity = 5m, LineStatus = "CANNOT_SUPPLY", RejectionReasonUuid = other.Reason
            });
            foreignReason.Status.Should().Be(HttpStatusCode.BadRequest, $"{who}: another organization's rejection reason — {foreignReason}");

            var foreignFile = await actor.Put($"/api/sale-orders/{own.DirectSo}/customer-po", new { CustomerPoReference = "X", CustomerPoAttachmentUuid = other.PoFile });
            foreignFile.Status.Should().Be(HttpStatusCode.BadRequest, $"{who}: another organization's PO file — {foreignFile}");
        }
    }

    /// <summary>
    /// Another organization's catalog item cannot go on one's own quotation, inquiry evaluation or sale order — the
    /// super admin included (the shared variant lookup must not lean on the EF filter the super admin bypasses).
    /// </summary>
    [Fact]
    public async Task Another_organizations_item_cannot_be_put_on_ones_own_quotation_or_order_super_admin_included()
    {
        var pkr = await _root.PkrBaseAsync();
        var (org2Id, org2, _) = await _root.SecondOrganizationAsync();
        await _root.SetOrgBaseCurrencyAsync(org2Id, pkr);

        var mineCustomer   = await _root.CreateCustomerAsync("Item Customer");
        var mineItem       = await _root.CreateProductAsync("Item Mine", purchasePrice: 10m, sellingPrice: 20m);
        var theirsCustomer = await org2.CreateCustomerAsync("Item Customer 2");
        var theirsItem     = await org2.CreateProductAsync("Item Theirs", purchasePrice: 10m, sellingPrice: 20m);

        var failures = new List<string>();
        foreach (var (actor, customer, own, foreign, who) in new[]
                 {
                     (_root, mineCustomer, mineItem, theirsItem, "super admin"),
                     (org2, theirsCustomer, theirsItem, mineItem, "org 2 admin")
                 })
        {
            var draft = await actor.DraftQuotationAsync(customer, pkr, own, 1m);
            var calls = new (string What, Api Result)[]
            {
                ("quotation line", await actor.Post($"/api/sale-quotations/{draft}/lines", new { LineType = "NORMAL", VariantUuid = foreign.VariantUuid, Quantity = 1m, UnitPrice = 5m })),
                ("quotation create", await actor.Post("/api/sale-quotations", new
                {
                    PartnerId = customer.Uuid, CurrencyId = pkr, ValidTo = Day(Today.AddDays(5)),
                    Lines = new object[] { new { LineType = "NORMAL", VariantUuid = foreign.VariantUuid, Quantity = 1m, UnitPrice = 5m } }
                })),
                ("direct sale order", await actor.TryCreateSaleOrderAsync(customer, pkr, SapKit.Line(foreign, 1m))),
            };
            foreach (var (what, api) in calls)
                if (api.Status != HttpStatusCode.BadRequest)
                    failures.Add($"{who}: another organization's item on a {what} — {api.ToString()[..Math.Min(200, api.ToString().Length)]}");
        }
        failures.Should().BeEmpty("an item belongs to its organization. ACCEPTED:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task Rejection_reason_codes_belong_to_each_organization()
    {
        var (_, org2, _) = await _root.SecondOrganizationAsync();
        var seeded = new[] { "NIP", "OOS", "DIS", "MOQ", "GEO", "REG", "CAP", "CRD", "PRC", "OTH" };

        var rootList = await _root.Ok(_root.Get("/api/rejection-reasons?includeInactive=true"), "root reasons");
        var org2List = await org2.Ok(org2.Get("/api/rejection-reasons?includeInactive=true"), "org 2 reasons");
        rootList.Items().Select(r => r.S("code")).Should().Contain(seeded);
        org2List.Items().Select(r => r.S("code")).Should().Contain(seeded, "a new organization is provisioned with the seed (BR-C5-02)");
        rootList.Items().Select(r => r.G("uuid")).Should().NotIntersectWith(org2List.Items().Select(r => r.G("uuid")), "each organization has its own rows");
        org2List.Items().Where(r => seeded.Contains(r.S("code"))).Select(r => r.I("displayOrder")).Should().BeInAscendingOrder();

        // The same custom code in both organizations; a duplicate only within one.
        var code = $"Z{Guid.NewGuid():N}"[..7].ToUpperInvariant();
        var theirs = (await org2.Ok(org2.Post("/api/rejection-reasons", new { Code = code, Description = "Org 2 only" }), "org 2 custom")).G("uuid");
        (await _root.Ok(_root.Get("/api/rejection-reasons?includeInactive=true"), "root")).Items().Select(r => r.S("code")).Should().NotContain(code);
        var ours = (await _root.Ok(_root.Post("/api/rejection-reasons", new { Code = code.ToLowerInvariant(), Description = "Org 1 too" }), "root same code")).G("uuid");
        ours.Should().NotBe(theirs);
        (await org2.Post("/api/rejection-reasons", new { Code = code, Description = "again" }))
            .Status.Should().Be(HttpStatusCode.Conflict, "BR-C5-01: unique per organization");

        // Deactivating a seeded reason in one organization leaves the other's alone.
        var rootCap = rootList.Items().Single(r => r.S("code") == "CAP").G("uuid");
        await _root.Ok(_root.Patch($"/api/rejection-reasons/{rootCap}/deactivate", null), "root deactivates CAP");
        (await _root.Ok(_root.Get("/api/rejection-reasons"), "root active")).Items().Select(r => r.S("code")).Should().NotContain("CAP");
        (await org2.Ok(org2.Get("/api/rejection-reasons"), "org 2 active")).Items().Select(r => r.S("code")).Should().Contain("CAP");

        // Cross-organization changes are 404; a seeded reason cannot be deleted even by its owner.
        (await _root.Delete($"/api/rejection-reasons/{theirs}")).Status.Should().Be(HttpStatusCode.NotFound);
        (await org2.Patch($"/api/rejection-reasons/{rootCap}/activate", null)).Status.Should().Be(HttpStatusCode.NotFound);
        var org2Oos = org2List.Items().Single(r => r.S("code") == "OOS").G("uuid");
        (await org2.Delete($"/api/rejection-reasons/{org2Oos}")).Status.Should().Be(HttpStatusCode.Conflict, "seeded: deactivate, never delete");
        await org2.Ok(org2.Delete($"/api/rejection-reasons/{theirs}"), "org 2 deletes its own unused custom reason");
        (await _root.Ok(_root.Get("/api/rejection-reasons"), "root")).Items().Select(r => r.G("uuid")).Should().Contain(ours);
    }

    [Fact]
    public async Task INQ_and_SQ_numbers_run_per_organization()
    {
        var pkr = await _root.PkrBaseAsync();
        var (org3Id, org3, _) = await _root.SecondOrganizationAsync("O3");
        await _root.SetOrgBaseCurrencyAsync(org3Id, pkr);
        var c1 = await _root.CreateCustomerAsync("Num Customer");
        var c3 = await org3.CreateCustomerAsync("Num Customer 3");

        async Task<string> Inq(SapKit k, Partner c)
        {
            var uuid = (await k.Ok(k.Post("/api/sale-inquiries", new { PartnerId = c.Uuid }), "inquiry")).GetGuid();
            return (await k.Ok(k.Get($"/api/sale-inquiries/{uuid}"), "read")).S("inquiryNumber")!;
        }
        async Task<string> Sq(SapKit k, Partner c)
        {
            var uuid = (await k.Ok(k.Post("/api/sale-quotations", new { PartnerId = c.Uuid, CurrencyId = pkr, ValidTo = Day(Today.AddDays(9)) }), "quotation")).GetGuid();
            return (await k.Ok(k.Get($"/api/sale-quotations/{uuid}"), "read")).S("quotationNumber")!;
        }
        static int N(string number) => int.Parse(Regex.Match(number, @"-(\d{5})$").Groups[1].Value);

        // Interleaved: org 1, org 1, org 3, org 1, org 3.
        var i1 = new List<string> { await Inq(_root, c1), await Inq(_root, c1) };
        var i3 = new List<string> { await Inq(org3, c3) };
        i1.Add(await Inq(_root, c1));
        i3.Add(await Inq(org3, c3));
        var q1 = new List<string> { await Sq(_root, c1), await Sq(_root, c1) };
        var q3 = new List<string> { await Sq(org3, c3) };
        q1.Add(await Sq(_root, c1));
        q3.Add(await Sq(org3, c3));

        var year = Today.Year;
        i3.Should().Equal($"INQ-{year}-00001", $"INQ-{year}-00002");
        q3.Should().Equal($"SQ-{year}-00001", $"SQ-{year}-00002");
        i1.Select(N).Should().Equal(Enumerable.Range(N(i1[0]), 3), "org 1's sequence is not interrupted by org 3's");
        q1.Select(N).Should().Equal(Enumerable.Range(N(q1[0]), 3));
        i1.Should().OnlyContain(n => Regex.IsMatch(n, $@"^INQ-{year}-\d{{5}}$"));

        // The same number may exist in both organizations — unique per (OrganizationId, Number) only.
        var same = await _f.QueryAsync("SELECT OrganizationId FROM demand.sale_inquiries WHERE InquiryNumber = @n", ("@n", $"INQ-{year}-00001"));
        same.Select(r => (Guid)r["OrganizationId"]!).Should().Contain(org3Id);
    }
}
