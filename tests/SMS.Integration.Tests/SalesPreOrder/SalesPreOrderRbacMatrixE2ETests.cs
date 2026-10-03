using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;
using Xunit;
using static SMS.Integration.Tests.SalesPreOrder.PreOrder;

namespace SMS.Integration.Tests.SalesPreOrder;

/// <summary>
/// A32-PF-04 — the permission matrix of every A32 endpoint on the real host (LocalDB), derived from
/// docs/sales-preorder/API-CONTRACT.md §2/§3/§4–§7 rather than from the controllers:
/// <list type="bullet">
/// <item>a user holding no code is refused everything (403); a user holding exactly ONE code passes exactly the gates
/// the contract lists for that code (any-of), and no other;</item>
/// <item>view-only (the three *_VIEW codes): every read is 200, every write 403 and changes nothing;</item>
/// <item>a user holding every A32 code drives every endpoint to 200 on real documents;</item>
/// <item>reserve and release are independent (spec §6.5), the seeded Inventory Manager holds both (and nothing else of
/// sales but SALE_ORDER_VIEW), Supply Dept Admin neither; no other built-in role holds a sales code.</item>
/// </list>
/// Uploads go to a throwaway web root.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~SalesPreOrderRbacMatrixE2ETests</c>.</para>
/// </summary>
public sealed class SalesPreOrderRbacMatrixE2ETests : IClassFixture<SapWebApplicationFactory>, IDisposable
{
    private const string IV = "SALE_INQUIRY_VIEW", IC = "SALE_INQUIRY_CREATE", IE = "SALE_INQUIRY_EDIT";
    private const string QV = "SALE_QUOTATION_VIEW", QC = "SALE_QUOTATION_CREATE", QE = "SALE_QUOTATION_EDIT", QS = "SALE_QUOTATION_SEND";
    private const string SV = "SALE_ORDER_VIEW", SC = "SALE_ORDER_CREATE", SE = "SALE_ORDER_EDIT", SF = "SALE_ORDER_CONFIRM", SX = "SALE_ORDER_CANCEL";
    private const string RES = "SALE_ORDER_RESERVE", REL = "SALE_ORDER_RELEASE_RESERVATION", RRM = "SALE_REJECTION_REASON_MANAGE";

    private static readonly string[] AllCodes = [IV, IC, IE, QV, QC, QE, QS, SV, SC, SE, SF, SX, RES, REL, RRM];

    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "sms-a32-qa-" + Guid.NewGuid().ToString("N"), "wwwroot");

    public SalesPreOrderRbacMatrixE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "PF4");
        Directory.CreateDirectory(_webRoot);
        factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath = _webRoot;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_webRoot)!, recursive: true); } catch { /* best effort */ }
    }

    // ── The endpoint table (contract) ────────────────────────────────────────────

    private sealed record Endpoint(string Name, string[] Codes, Func<HttpClient, Task<Api>> Call);

    private async Task<Api> UploadAs(HttpClient client, string interfaceCode, Guid documentId)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF"));
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");
        form.Add(file, "file", "rbac.pdf");
        form.Add(new StringContent(interfaceCode), "interfaceCode");
        form.Add(new StringContent(documentId.ToString()), "documentId");
        using var resp = await client.PostAsync("/api/attachments/upload", form);
        var raw = await resp.Content.ReadAsStringAsync();
        return new Api(resp.StatusCode, System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw).RootElement.Clone(), raw);
    }

    /// <summary>Every A32 endpoint, with the codes that open it (any of) — on unknown ids, so a passed gate is a 404/400, never a change.</summary>
    private List<Endpoint> Endpoints()
    {
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();
        var body = new { };
        return
        [
            new("inquiry list",            [IV],  c => _k.Get("/api/sale-inquiries", c)),
            new("inquiry create",          [IC],  c => _k.Post("/api/sale-inquiries", new { PartnerId = Guid.NewGuid() }, c)),
            new("inquiry read",            [IV],  c => _k.Get($"/api/sale-inquiries/{x}", c)),
            new("inquiry update",          [IE],  c => _k.Put($"/api/sale-inquiries/{x}", new { ReceivedDate = "2026-10-01" }, c)),
            new("inquiry add line",        [IE],  c => _k.Post($"/api/sale-inquiries/{x}/lines", new { ProductDescription = "x", RequestedQuantity = 1 }, c)),
            new("inquiry update line",     [IE],  c => _k.Put($"/api/sale-inquiries/{x}/lines/{y}", new { ProductDescription = "x", RequestedQuantity = 1 }, c)),
            new("inquiry delete line",     [IE],  c => _k.Delete($"/api/sale-inquiries/{x}/lines/{y}", c)),
            new("inquiry status",          [IE],  c => _k.Patch($"/api/sale-inquiries/{x}/status", new { Status = "UNDER_REVIEW" }, c)),
            new("inquiry create-quotation",[QC],  c => _k.Post($"/api/sale-inquiries/{x}/create-quotation", new { ValidTo = "2026-12-01" }, c)),

            new("quotation list",          [QV],  c => _k.Get("/api/sale-quotations", c)),
            new("quotation create",        [QC],  c => _k.Post("/api/sale-quotations", new { PartnerId = Guid.NewGuid(), ValidTo = "2026-12-01" }, c)),
            new("quotation read",          [QV],  c => _k.Get($"/api/sale-quotations/{x}", c)),
            new("quotation update",        [QE],  c => _k.Put($"/api/sale-quotations/{x}", new { ValidFrom = "2026-10-01", ValidTo = "2026-12-01" }, c)),
            new("quotation add line",      [QE],  c => _k.Post($"/api/sale-quotations/{x}/lines", new { LineType = "NORMAL", Quantity = 1 }, c)),
            new("quotation update line",   [QE],  c => _k.Put($"/api/sale-quotations/{x}/lines/{y}", new { LineType = "NORMAL", Quantity = 1 }, c)),
            new("quotation delete line",   [QE],  c => _k.Delete($"/api/sale-quotations/{x}/lines/{y}", c)),
            new("quotation send",          [QS],  c => _k.Post($"/api/sale-quotations/{x}/send", null, c)),
            new("quotation response",      [QE],  c => _k.Patch($"/api/sale-quotations/{x}/lines/{y}/customer-response", new { Response = "ACCEPTED" }, c)),
            new("quotation accept",        [QE],  c => _k.Post($"/api/sale-quotations/{x}/accept", null, c)),
            new("quotation reject",        [QE],  c => _k.Post($"/api/sale-quotations/{x}/reject", new { Reason = "no" }, c)),
            new("quotation convert",       [SC],  c => _k.Post($"/api/sale-quotations/{x}/convert-to-order", body, c)),
            new("quotation copy",          [QC],  c => _k.Post($"/api/sale-quotations/{x}/copy", null, c)),

            new("reasons list",            [IV, IE, QV, QE, RRM], c => _k.Get("/api/rejection-reasons", c)),
            new("reason create",           [RRM], c => _k.Post("/api/rejection-reasons", new { Code = "", Description = "" }, c)),
            new("reason update",           [RRM], c => _k.Put($"/api/rejection-reasons/{x}", new { Description = "d", DisplayOrder = 1 }, c)),
            new("reason deactivate",       [RRM], c => _k.Patch($"/api/rejection-reasons/{x}/deactivate", null, c)),
            new("reason activate",         [RRM], c => _k.Patch($"/api/rejection-reasons/{x}/activate", null, c)),
            new("reason delete",           [RRM], c => _k.Delete($"/api/rejection-reasons/{x}", c)),

            new("SO customer-po",          [SE],  c => _k.Put($"/api/sale-orders/{x}/customer-po", new { CustomerPoReference = "R" }, c)),
            new("SO customer-po-check",    [SV, SC, SE], c => _k.Get("/api/sale-orders/customer-po-check?reference=R", c)),
            new("SO reserve line",         [RES], c => _k.Post($"/api/sale-orders/{x}/lines/{y}/reserve", body, c)),
            new("SO release line",         [REL], c => _k.Post($"/api/sale-orders/{x}/lines/{y}/release", body, c)),
            new("SO reserve-all",          [RES], c => _k.Post($"/api/sale-orders/{x}/reserve-all", body, c)),

            new("files SALE_INQUIRY view",   [IV, IC, IE],     c => _k.Get($"/api/attachments?interface=SALE_INQUIRY&documentId={x}", c)),
            new("files SALE_QUOTATION view", [QV, QC, QE, QS], c => _k.Get($"/api/attachments?interface=SALE_QUOTATION&documentId={x}", c)),
            new("files CUSTOMER_PO view",    [SV, SC, SE, SF], c => _k.Get($"/api/attachments?interface=CUSTOMER_PO&documentId={x}", c)),
            new("files SALE_ORDER view",     [SV, SC, SE, SF], c => _k.Get($"/api/attachments?interface=SALE_ORDER&documentId={x}", c)),
            new("files SALE_INQUIRY upload",   [IC, IE], c => UploadAs(c, "SALE_INQUIRY", x)),
            new("files SALE_QUOTATION upload", [QC, QE], c => UploadAs(c, "SALE_QUOTATION", x)),
            new("files CUSTOMER_PO upload",    [SC, SE], c => UploadAs(c, "CUSTOMER_PO", x)),
            new("files SALE_ORDER upload",     [SC, SE], c => UploadAs(c, "SALE_ORDER", x)),
        ];
    }

    [Fact]
    public async Task No_code_is_refused_everything_and_each_single_code_opens_exactly_its_contract_gates()
    {
        var failures = new List<string>();

        var nobody = await _k.LoginWithPermissionsAsync("none");
        foreach (var e in Endpoints())
        {
            var api = await e.Call(nobody);
            if (api.Status != HttpStatusCode.Forbidden) failures.Add($"[no code] {e.Name}: expected 403, got {api}");
        }

        foreach (var code in AllCodes)
        {
            var client = await _k.LoginWithPermissionsAsync($"one{Array.IndexOf(AllCodes, code)}", code);
            foreach (var e in Endpoints())
            {
                var api = await e.Call(client);
                var opens = e.Codes.Contains(code);
                if (opens && api.Status == HttpStatusCode.Forbidden)
                    failures.Add($"[{code}] {e.Name}: the contract opens it, got 403 — {api}");
                if (!opens && api.Status != HttpStatusCode.Forbidden)
                    failures.Add($"[{code}] {e.Name}: the contract does not open it, expected 403, got {api}");
                if (opens && (int)api.Status >= 500)
                    failures.Add($"[{code}] {e.Name}: server error on an unknown id — {api}");
            }
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public async Task View_only_reads_every_document_and_is_refused_every_write_which_changes_nothing()
    {
        var pkr      = await _k.PkrBaseAsync();
        var customer = await _k.CreateCustomerAsync("RBAC Customer");
        var item     = await _k.CreateProductAsync("RBAC Widget", purchasePrice: 10m, sellingPrice: 20m);
        var reason   = await _k.ReasonAsync("PRC");

        var inquiry   = await _k.ReviewedInquiryAsync(customer, item, 3m);
        var inqLine   = (await _k.Ok(_k.Get($"/api/sale-inquiries/{inquiry}"), "read")).A("lines").Single().G("uuid");
        var draft     = await _k.DraftQuotationAsync(customer, pkr, item, 2m);
        var draftLine = (await _k.Ok(_k.Get($"/api/sale-quotations/{draft}"), "read")).A("lines").Single().G("uuid");
        var accepted  = await _k.AcceptedQuotationAsync(customer, pkr, item, 2m);
        var so        = await _k.CreateSaleOrderAsync(customer, pkr, SapKit.Line(item, 1m));
        var soLine    = (await _k.GetSaleOrderAsync(so)).A("lines").Single().G("uuid");

        var viewer = await _k.LoginWithPermissionsAsync("viewer", IV, QV, SV);

        // Reads: 200 with the document.
        (await _k.Ok(_k.Get("/api/sale-inquiries?pageSize=100", viewer), "list inquiries")).A("data").Select(r => r.G("uuid")).Should().Contain(inquiry);
        (await _k.Ok(_k.Get($"/api/sale-inquiries/{inquiry}", viewer), "read inquiry")).G("uuid").Should().Be(inquiry);
        (await _k.Ok(_k.Get("/api/sale-quotations?pageSize=100", viewer), "list quotations")).A("data").Select(r => r.G("uuid")).Should().Contain(draft);
        (await _k.Ok(_k.Get($"/api/sale-quotations/{draft}", viewer), "read quotation")).G("uuid").Should().Be(draft);
        (await _k.Ok(_k.Get("/api/rejection-reasons", viewer), "reasons")).Items().Should().HaveCountGreaterThanOrEqualTo(10);
        (await _k.Ok(_k.Get($"/api/sale-orders/{so}", viewer), "read SO")).G("uuid").Should().Be(so);
        await _k.Ok(_k.Get("/api/sale-orders/customer-po-check?reference=ANY", viewer), "PO check");
        await _k.Ok(_k.Get($"/api/attachments?interface=SALE_INQUIRY&documentId={inquiry}", viewer), "inquiry files");
        await _k.Ok(_k.Get($"/api/attachments?interface=SALE_QUOTATION&documentId={draft}", viewer), "quotation files");
        await _k.Ok(_k.Get($"/api/attachments?interface=CUSTOMER_PO&documentId={so}", viewer), "PO files");

        // Writes: 403 on the real documents.
        var writes = new (string What, Func<Task<Api>> Call)[]
        {
            ("inquiry create",      () => _k.Post("/api/sale-inquiries", new { PartnerId = customer.Uuid }, viewer)),
            ("inquiry update",      () => _k.Put($"/api/sale-inquiries/{inquiry}", new { ReceivedDate = Day(Today), Notes = "viewer was here" }, viewer)),
            ("inquiry add line",    () => _k.Post($"/api/sale-inquiries/{inquiry}/lines", new { ProductDescription = "x", RequestedQuantity = 1 }, viewer)),
            ("inquiry edit line",   () => _k.Put($"/api/sale-inquiries/{inquiry}/lines/{inqLine}", new { ProductDescription = "x", RequestedQuantity = 1, LineStatus = "PENDING" }, viewer)),
            ("inquiry delete line", () => _k.Delete($"/api/sale-inquiries/{inquiry}/lines/{inqLine}", viewer)),
            ("inquiry status",      () => _k.Patch($"/api/sale-inquiries/{inquiry}/status", new { Status = "DECLINED", Reason = "viewer" }, viewer)),
            ("create quotation",    () => _k.Post($"/api/sale-inquiries/{inquiry}/create-quotation", new { CurrencyId = pkr, ValidTo = Day(Today.AddDays(9)) }, viewer)),
            ("quotation create",    () => _k.Post("/api/sale-quotations", new { PartnerId = customer.Uuid, CurrencyId = pkr, ValidTo = Day(Today.AddDays(9)) }, viewer)),
            ("quotation update",    () => _k.Put($"/api/sale-quotations/{draft}", new { ValidFrom = Day(Today), ValidTo = Day(Today.AddDays(9)), Notes = "viewer" }, viewer)),
            ("quotation add line",  () => _k.Post($"/api/sale-quotations/{draft}/lines", new { LineType = "NORMAL", VariantUuid = item.VariantUuid, Quantity = 1 }, viewer)),
            ("quotation edit line", () => _k.Put($"/api/sale-quotations/{draft}/lines/{draftLine}", new { LineType = "NORMAL", VariantUuid = item.VariantUuid, Quantity = 9 }, viewer)),
            ("quotation del line",  () => _k.Delete($"/api/sale-quotations/{draft}/lines/{draftLine}", viewer)),
            ("quotation send",      () => _k.Post($"/api/sale-quotations/{draft}/send", null, viewer)),
            ("quotation copy",      () => _k.Post($"/api/sale-quotations/{draft}/copy", null, viewer)),
            ("quotation convert",   () => _k.Post($"/api/sale-quotations/{accepted}/convert-to-order", new { DeliveryMode = "SELF_PICKUP" }, viewer)),
            ("reason create",       () => _k.Post("/api/rejection-reasons", new { Code = "VIEWR", Description = "viewer" }, viewer)),
            ("reason deactivate",   () => _k.Patch($"/api/rejection-reasons/{reason}/deactivate", null, viewer)),
            ("SO customer-po",      () => _k.Put($"/api/sale-orders/{so}/customer-po", new { CustomerPoReference = "VIEWER" }, viewer)),
            ("SO reserve",          () => _k.Post($"/api/sale-orders/{so}/lines/{soLine}/reserve", new { }, viewer)),
            ("SO release",          () => _k.Post($"/api/sale-orders/{so}/lines/{soLine}/release", new { }, viewer)),
            ("SO reserve-all",      () => _k.Post($"/api/sale-orders/{so}/reserve-all", new { }, viewer)),
            ("upload inquiry file", () => UploadAs(viewer, "SALE_INQUIRY", inquiry)),
            ("upload PO file",      () => UploadAs(viewer, "CUSTOMER_PO", so)),
        };
        foreach (var (what, call) in writes)
        {
            var api = await call();
            api.Status.Should().Be(HttpStatusCode.Forbidden, $"view-only {what} — {api}");
        }

        // Nothing changed.
        var inq = await _k.Ok(_k.Get($"/api/sale-inquiries/{inquiry}"), "re-read inquiry");
        (inq.S("status"), inq.IsNull("notes"), inq.A("lines").Count).Should().Be(("REVIEW_COMPLETE", true, 1));
        var q = await _k.Ok(_k.Get($"/api/sale-quotations/{draft}"), "re-read quotation");
        (q.S("status"), q.A("lines").Single().D("quantity")).Should().Be(("DRAFT", 2m));
        (await _k.Ok(_k.Get($"/api/sale-quotations/{accepted}"), "re-read accepted")).S("status").Should().Be("ACCEPTED");
        (await _k.Ok(_k.Get("/api/rejection-reasons"), "reasons")).Items().Single(r => r.G("uuid") == reason).B("isActive").Should().BeTrue();
        (await _k.GetSaleOrderAsync(so)).IsNull("customerPoReference").Should().BeTrue();
    }

    [Fact]
    public async Task A_user_holding_every_A32_code_drives_every_endpoint_to_200()
    {
        var pkr = await _k.PkrBaseAsync();
        await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await _k.CreateApproverPlaceholdersAsync();
        var wh       = await _k.CreateWarehouseAsync();
        var vendor   = await _k.CreateVendorAsync("Full Vendor");
        var customer = await _k.CreateCustomerAsync("Full Customer");
        var item     = await _k.CreateProductAsync("Full Widget", purchasePrice: 10m, sellingPrice: 20m);
        await _k.StockUpAsync(vendor, wh, (item, 50m, 10m));

        var u = await _k.LoginWithPermissionsAsync("full", AllCodes);
        var k = new SapKit(_f, "PF4U", u);

        // Rejection reasons.
        var code = $"Q{Guid.NewGuid():N}"[..6].ToUpperInvariant();
        var custom = (await k.Ok(k.Post("/api/rejection-reasons", new { Code = code, Description = "QA custom", DisplayOrder = 500 }), "reason create")).G("uuid");
        await k.Ok(k.Put($"/api/rejection-reasons/{custom}", new { Description = "QA custom (edited)", DisplayOrder = 510 }), "reason update");
        await k.Ok(k.Patch($"/api/rejection-reasons/{custom}/deactivate", null), "reason deactivate");
        (await k.Ok(k.Get("/api/rejection-reasons"), "active reasons")).Items().Select(r => r.G("uuid")).Should().NotContain(custom);
        (await k.Ok(k.Get("/api/rejection-reasons?includeInactive=true"), "all reasons")).Items().Select(r => r.G("uuid")).Should().Contain(custom);
        await k.Ok(k.Patch($"/api/rejection-reasons/{custom}/activate", null), "reason activate");
        await k.Ok(k.Delete($"/api/rejection-reasons/{custom}"), "reason delete");
        var oos = await k.ReasonAsync("OOS");

        // Inquiry.
        var inquiry = (await k.Ok(k.Post("/api/sale-inquiries", new
        {
            PartnerId = customer.Uuid,
            Lines = new object[]
            {
                new { VariantUuid = item.VariantUuid, ProductDescription = "Full widget", RequestedQuantity = 4m },
                new { ProductDescription = "Spare line", RequestedQuantity = 1m }
            }
        }), "inquiry create")).GetGuid();
        await k.Ok(k.Get("/api/sale-inquiries"), "inquiry list");
        var inq = await k.Ok(k.Get($"/api/sale-inquiries/{inquiry}"), "inquiry read");
        var lines = inq.A("lines").OrderBy(l => l.I("lineNumber")).Select(l => l.G("uuid")).ToList();
        await k.Ok(k.Put($"/api/sale-inquiries/{inquiry}", new { ReceivedDate = Day(Today), Notes = "full user" }), "inquiry update");
        var added = (await k.Ok(k.Post($"/api/sale-inquiries/{inquiry}/lines", new { ProductDescription = "Gadget", RequestedQuantity = 2m }), "inquiry add line")).GetGuid();
        await k.Ok(k.Delete($"/api/sale-inquiries/{inquiry}/lines/{lines[1]}"), "inquiry delete line");
        await k.Ok(k.Patch($"/api/sale-inquiries/{inquiry}/status", new { Status = "UNDER_REVIEW" }), "inquiry status");
        await k.Ok(k.Put($"/api/sale-inquiries/{inquiry}/lines/{lines[0]}", new
        {
            VariantUuid = item.VariantUuid, ProductDescription = "Full widget", RequestedQuantity = 4m, LineStatus = "CAN_SUPPLY", EstimatedDeliveryDate = Day(Today.AddDays(3))
        }), "inquiry evaluate line");
        await k.Ok(k.Put($"/api/sale-inquiries/{inquiry}/lines/{added}", new
        {
            ProductDescription = "Gadget", RequestedQuantity = 2m, LineStatus = "CANNOT_SUPPLY", RejectionReasonUuid = oos
        }), "inquiry evaluate line 2");
        await k.Ok(k.Patch($"/api/sale-inquiries/{inquiry}/status", new { Status = "REVIEW_COMPLETE" }), "inquiry complete");
        var quotation = (await k.Ok(k.Post($"/api/sale-inquiries/{inquiry}/create-quotation", new { CurrencyId = pkr, ValidTo = Day(Today.AddDays(9)) }), "create-quotation")).GetGuid();
        (await UploadAs(u, "SALE_INQUIRY", inquiry)).Status.Should().Be(HttpStatusCode.OK);
        await k.Ok(k.Get($"/api/attachments?interface=SALE_INQUIRY&documentId={inquiry}"), "inquiry files");

        // Quotation.
        await k.Ok(k.Get("/api/sale-quotations"), "quotation list");
        await k.Ok(k.Put($"/api/sale-quotations/{quotation}", new { CurrencyId = pkr, ValidFrom = Day(Today), ValidTo = Day(Today.AddDays(10)), Notes = "full" }), "quotation update");
        var ql = (await k.Ok(k.Post($"/api/sale-quotations/{quotation}/lines", new { LineType = "NORMAL", VariantUuid = item.VariantUuid, Quantity = 1m }), "quotation add line")).GetGuid();
        await k.Ok(k.Put($"/api/sale-quotations/{quotation}/lines/{ql}", new { LineType = "NORMAL", VariantUuid = item.VariantUuid, Quantity = 2m }), "quotation edit line");
        await k.Ok(k.Delete($"/api/sale-quotations/{quotation}/lines/{ql}"), "quotation delete line");
        (await UploadAs(u, "SALE_QUOTATION", quotation)).Status.Should().Be(HttpStatusCode.OK);
        await k.Ok(k.Get($"/api/attachments?interface=SALE_QUOTATION&documentId={quotation}"), "quotation files");
        await k.Ok(k.Post($"/api/sale-quotations/{quotation}/send"), "quotation send");
        var q = await k.Ok(k.Get($"/api/sale-quotations/{quotation}"), "quotation read");
        var offered = q.A("lines").Single(l => l.S("lineType") == "NORMAL").G("uuid");
        await k.Ok(k.Patch($"/api/sale-quotations/{quotation}/lines/{offered}/customer-response", new { Response = "ACCEPTED" }), "quotation response");
        await k.Ok(k.Post($"/api/sale-quotations/{quotation}/accept"), "quotation accept");
        var so = (await k.Ok(k.Post($"/api/sale-quotations/{quotation}/convert-to-order", new { DeliveryMode = "SELF_PICKUP" }), "quotation convert")).GetGuid();
        await k.Ok(k.Post($"/api/sale-quotations/{quotation}/copy"), "quotation copy");

        var toReject = await k.DraftQuotationAsync(customer, pkr, item, 1m);
        await k.Ok(k.Post($"/api/sale-quotations/{toReject}/send"), "send");
        var rl = (await k.Ok(k.Get($"/api/sale-quotations/{toReject}"), "read")).A("lines").Single().G("uuid");
        await k.Ok(k.Patch($"/api/sale-quotations/{toReject}/lines/{rl}/customer-response", new { Response = "REJECTED" }), "response REJECTED");
        await k.Ok(k.Post($"/api/sale-quotations/{toReject}/reject", new { Reason = "no" }), "quotation reject");

        // Sale order: customer PO + file, confirm (auto-holds), release, reserve, reserve-all.
        (await UploadAs(u, "CUSTOMER_PO", so)).Status.Should().Be(HttpStatusCode.OK);
        var file = (await k.Ok(k.Get($"/api/attachments?interface=CUSTOMER_PO&documentId={so}"), "PO files")).Items().Single().G("uuid");
        await k.Ok(k.Put($"/api/sale-orders/{so}/customer-po", new { CustomerPoReference = $"{k.Marker}-PO", CustomerPoAttachmentUuid = file }), "SO customer-po");
        await k.Ok(k.Get($"/api/sale-orders/customer-po-check?reference={k.Marker}-PO"), "SO customer-po-check");
        await k.Ok(k.Post($"/api/sale-orders/{so}/confirm"), "SO confirm");
        var line = (await k.GetSaleOrderAsync(so)).A("lines").Single().G("uuid");
        await k.Ok(k.Post($"/api/sale-orders/{so}/lines/{line}/release", new { }), "SO release");
        await k.Ok(k.Post($"/api/sale-orders/{so}/lines/{line}/reserve", new { Quantity = 1m }), "SO reserve");
        await k.Ok(k.Post($"/api/sale-orders/{so}/reserve-all", new { }), "SO reserve-all");
        (await k.GetSaleOrderAsync(so)).A("lines").Single().S("deliveryIndicator").Should().Be("BLUE");
    }

    [Fact]
    public async Task Reserve_and_release_are_independent_and_the_seeded_roles_hold_exactly_the_contract_codes()
    {
        var pkr = await _k.PkrBaseAsync();
        await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await _k.CreateApproverPlaceholdersAsync();
        var wh       = await _k.CreateWarehouseAsync();
        var vendor   = await _k.CreateVendorAsync("Ind Vendor");
        var customer = await _k.CreateCustomerAsync("Ind Customer");
        var item     = await _k.CreateProductAsync("Ind Widget", purchasePrice: 10m, sellingPrice: 20m);
        await _k.StockUpAsync(vendor, wh, (item, 100m, 10m));

        var so = await _k.CreateSaleOrderAsync(customer, pkr, SapKit.Line(item, 10m));
        await _k.ConfirmSaleOrderAsync(so);   // confirm holds all 10
        var line = (await _k.GetSaleOrderAsync(so)).A("lines").Single().G("uuid");

        Task<Api> Reserve(HttpClient c) => _k.Post($"/api/sale-orders/{so}/lines/{line}/reserve", new { Quantity = 1m }, c);
        Task<Api> ReserveAll(HttpClient c) => _k.Post($"/api/sale-orders/{so}/reserve-all", new { }, c);
        Task<Api> Release(HttpClient c) => _k.Post($"/api/sale-orders/{so}/lines/{line}/release", new { Quantity = 1m }, c);

        var neither     = await _k.LoginWithPermissionsAsync("neither", SV);
        var reserveOnly = await _k.LoginWithPermissionsAsync("resonly", SV, RES);
        var releaseOnly = await _k.LoginWithPermissionsAsync("relonly", SV, REL);
        var both        = await _k.LoginWithPermissionsAsync("both", SV, RES, REL);
        var invManager  = await _k.LoginAsNewUserAsync((int)EnumRole.InventoryManager, "a32-im");
        var supplyAdmin = await _k.LoginAsNewUserAsync((int)EnumRole.SupplyDeptAdmin, "a32-sda");

        // Neither: both refused.
        (await Reserve(neither)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await ReserveAll(neither)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await Release(neither)).Status.Should().Be(HttpStatusCode.Forbidden);

        // Release only: may free, may not hold.
        (await _k.Ok(Release(releaseOnly), "release-only releases")).D("reservedQty").Should().Be(9m);
        (await Reserve(releaseOnly)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await ReserveAll(releaseOnly)).Status.Should().Be(HttpStatusCode.Forbidden);

        // Reserve only: may hold, may not free.
        (await _k.Ok(Reserve(reserveOnly), "reserve-only reserves")).D("reservedQty").Should().Be(10m);
        (await Release(reserveOnly)).Status.Should().Be(HttpStatusCode.Forbidden);
        await _k.Ok(Release(both), "both releases");
        await _k.Ok(ReserveAll(reserveOnly), "reserve-only reserves all");
        (await _k.GetSaleOrderAsync(so)).A("lines").Single().D("reservedQty").Should().Be(10m);

        // Both.
        await _k.Ok(Release(both), "both releases again");
        await _k.Ok(Reserve(both), "both reserves");

        // Inventory Manager (the spec's WAREHOUSE_MANAGER): reads the order, reserves and releases — and nothing else of sales.
        await _k.Ok(_k.Get($"/api/sale-orders/{so}", invManager), "IM reads the order");
        await _k.Ok(Release(invManager), "IM releases");
        await _k.Ok(Reserve(invManager), "IM reserves");
        await _k.Ok(ReserveAll(invManager), "IM reserve-all");
        foreach (var (what, api) in new[]
                 {
                     ("inquiries", await _k.Get("/api/sale-inquiries", invManager)),
                     ("quotations", await _k.Get("/api/sale-quotations", invManager)),
                     ("customer-po", await _k.Put($"/api/sale-orders/{so}/customer-po", new { CustomerPoReference = "IM" }, invManager)),
                     ("convert", await _k.Post($"/api/sale-quotations/{Guid.NewGuid()}/convert-to-order", new { }, invManager)),
                     ("reasons", await _k.Post("/api/rejection-reasons", new { Code = "IMX", Description = "x" }, invManager)),
                 })
            api.Status.Should().Be(HttpStatusCode.Forbidden, $"Inventory Manager {what} — {api}");

        // Supply Dept Admin stays config read/write only.
        (await Reserve(supplyAdmin)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await Release(supplyAdmin)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await _k.Get("/api/sale-inquiries", supplyAdmin)).Status.Should().Be(HttpStatusCode.Forbidden);

        // The seeding, as stored: which built-in roles hold which SALE_* codes in this organization.
        var rows = await _f.QueryAsync(
            "SELECT rp.RoleID, p.Code FROM auth.RolePermissions rp JOIN auth.Permissions p ON p.PermissionID = rp.PermissionID " +
            "JOIN auth.Roles r ON r.RoleID = rp.RoleID " +
            "WHERE rp.IsAllowed = 1 AND p.Code LIKE 'SALE[_]%' AND r.IsGlobal = 1 AND rp.OrganizationId = @org",
            ("@org", _f.OrganizationId));
        // Built-in roles only: the custom roles this class creates (as the super admin, so global) are not the seeder's.
        var builtIn = Enum.GetValues<EnumRole>().Select(r => (int)r).ToHashSet();
        var byRole = rows.Where(r => builtIn.Contains(Convert.ToInt32(r["RoleID"])))
            .GroupBy(r => Convert.ToInt32(r["RoleID"])).ToDictionary(g => g.Key, g => g.Select(r => (string)r["Code"]!).ToHashSet());

        byRole.Keys.Except(new[] { (int)EnumRole.SystemAdmin, (int)EnumRole.OrgAdmin, (int)EnumRole.InventoryManager, (int)EnumRole.SupplyDeptAdmin })
            .Should().BeEmpty("no other built-in role holds a sales code");
        byRole.Should().ContainKey((int)EnumRole.InventoryManager);
        byRole[(int)EnumRole.InventoryManager].Should().BeEquivalentTo(new[] { SV, RES, REL });
        if (byRole.TryGetValue((int)EnumRole.SupplyDeptAdmin, out var sda))
            sda.Should().OnlyContain(c => c.StartsWith("SALE_ORDER_CONFIG_"), "Supply Dept Admin is config read/write only");
        foreach (var admin in new[] { (int)EnumRole.SystemAdmin, (int)EnumRole.OrgAdmin })
            if (byRole.TryGetValue(admin, out var codes))
                codes.Should().Contain(new[] { IV, IC, IE, QV, QC, QE, QS, RES, REL, RRM }, $"role {admin} gets every new code");
    }
}
