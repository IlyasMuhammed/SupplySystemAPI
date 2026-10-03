using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.SalesPreOrder.PreOrder;

namespace SMS.Integration.Tests.SalesPreOrder;

/// <summary>
/// A32-PF-02 — Path 3 (a sale order made directly) on the real host (LocalDB):
/// <list type="bullet">
/// <item>source MANUAL (other sources refused), the customer's PO reference + date, the duplicate-PO warning (never a
/// block), the PO document uploaded through api/attachments (CUSTOMER_PO) and linked by PUT …/customer-po;</item>
/// <item>manual reservation: none free → NONE_AVAILABLE; short → NEEDS_CONFIRMATION, then partial; full; reserve-all;
/// release (part, all); the computed reservedQty / reservableQty and the delivery indicator RED → YELLOW → BLUE and back;
/// cancel frees every hold and greys the lines;</item>
/// <item>the confirm-time auto-reservation (AvailabilityCheckService) shows up as the same computed reservedQty, and
/// the expiry sweep's release does too;</item>
/// <item>two racing reserves on one line never hold more than the line (the per-order sp_getapplock).</item>
/// </list>
/// Uploads go to a throwaway web root, never src/SMS.API/wwwroot.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~DirectSaleOrderReservationE2ETests</c>.</para>
/// </summary>
public sealed class DirectSaleOrderReservationE2ETests : IClassFixture<SapWebApplicationFactory>, IDisposable
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "sms-a32-qa-" + Guid.NewGuid().ToString("N"), "wwwroot");

    public DirectSaleOrderReservationE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "PF2");
        Directory.CreateDirectory(_webRoot);
        factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath = _webRoot;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_webRoot)!, recursive: true); } catch { /* best effort */ }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private async Task<Guid> UploadAsync(string interfaceCode, Guid documentId, string fileName)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n% A32 QA customer PO\n%%EOF"));
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(interfaceCode), "interfaceCode");
        form.Add(new StringContent(documentId.ToString()), "documentId");
        using var resp = await _k.Admin.PostAsync("/api/attachments/upload", form);
        var raw = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK, $"upload {interfaceCode} — {raw}");
        return JsonDocument.Parse(raw).RootElement.GetProperty("result").GetGuid();
    }

    private Task<Api> Reserve(Guid so, Guid line, object body) => _k.Post($"/api/sale-orders/{so}/lines/{line}/reserve", body);
    private Task<Api> Release(Guid so, Guid line, object body) => _k.Post($"/api/sale-orders/{so}/lines/{line}/release", body);

    private async Task<JsonElement> SoLine(Guid so, Guid variant) =>
        (await _k.GetSaleOrderAsync(so)).A("lines").Single(l => l.G("variantUuid") == variant);

    /// <summary>The ledger itself: ACTIVE SALES_ORDER holds of the order, per line.</summary>
    private async Task<decimal> LedgerHeldAsync(Guid so, Guid? line = null)
    {
        var rows = await _f.QueryAsync(
            "SELECT ISNULL(SUM(ReservedQty), 0) AS Q FROM inventory.StockReservations " +
            "WHERE SourceType = 'SALES_ORDER' AND SourceUuid = @so AND Status = 'ACTIVE' AND (@line IS NULL OR SourceLineUuid = @line)",
            ("@so", so), ("@line", (object?)line ?? DBNull.Value));
        return Convert.ToDecimal(rows[0]["Q"]);
    }

    /// <summary>Free stock of a variant, best single warehouse — through a probe draft order's availability preview.</summary>
    private async Task<decimal> FreeAsync(Partner customer, Guid currency, Product p)
    {
        var probe = await _k.CreateSaleOrderAsync(customer, currency, SapKit.Line(p, 1m));
        var rows = await _k.Ok(_k.Get($"/api/sale-orders/{probe}/availability"), "availability preview");
        return rows.Items().Single().D("availableQty");
    }

    private async Task<(Guid Pkr, Warehouse Wh, Partner Vendor, Partner Customer)> SetupAsync()
    {
        var pkr = await _k.PkrBaseAsync();
        await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await _k.CreateApproverPlaceholdersAsync();
        return (pkr, await _k.CreateWarehouseAsync(), await _k.CreateVendorAsync("Res Vendor"), await _k.CreateCustomerAsync("Res Customer"));
    }

    // ── C3: source, customer PO, duplicate warning, PO document ──────────────────

    [Fact]
    public async Task A_direct_order_is_MANUAL_and_carries_the_customer_PO_its_document_and_a_duplicate_warning()
    {
        var pkr      = await _k.PkrBaseAsync();
        var customer = await _k.CreateCustomerAsync("CPO Customer");
        var item     = await _k.CreateProductAsync("CPO Widget", purchasePrice: 10m, sellingPrice: 20m);
        var poRef    = $"{_k.Marker}-CPO-77";
        var poDate   = Day(Today.AddDays(-2));

        foreach (var source in new[] { "FROM_QUOTATION", "PORTAL", "INTER_TENANT", "NONSENSE" })
        {
            var refused = await _k.Post("/api/sale-orders", new
            {
                PartnerId = customer.Uuid, CurrencyId = pkr, DeliveryMode = "SELF_PICKUP", SourceType = source,
                Lines = new[] { SapKit.Line(item, 1m) }
            });
            refused.Status.Should().Be(HttpStatusCode.BadRequest, $"sourceType {source} is not for a direct order — {refused}");
        }
        (await _k.Post("/api/sale-orders", new
        {
            PartnerId = customer.Uuid, CurrencyId = pkr, DeliveryMode = "SELF_PICKUP", CustomerPoReference = new string('P', 51),
            Lines = new[] { SapKit.Line(item, 1m) }
        })).Status.Should().Be(HttpStatusCode.BadRequest, "BR-C3-04: at most 50 characters");

        var first = await _k.Post("/api/sale-orders", new
        {
            PartnerId = customer.Uuid, CurrencyId = pkr, DeliveryMode = "SELF_PICKUP", SourceType = "MANUAL",
            CustomerPoReference = poRef, CustomerPoDate = poDate, Lines = new[] { SapKit.Line(item, 2m) }
        });
        first.Status.Should().Be(HttpStatusCode.OK, first.ToString());
        first.Message.Should().NotContain("Warning", "the first use of a PO reference warns about nothing");
        var so1 = first.Result.GetGuid();

        var o1 = await _k.GetSaleOrderAsync(so1);
        o1.S("sourceType").Should().Be("MANUAL", "T-C3-01");
        o1.IsNull("sourceQuotation").Should().BeTrue();
        o1.IsNull("sourceInquiry").Should().BeTrue();
        o1.S("customerPoReference").Should().Be(poRef, "T-C3-02");
        o1.S("customerPoDate").Should().StartWith(poDate);
        o1.IsNull("customerPoAttachmentUuid").Should().BeTrue();

        // An order without a sourceType is MANUAL too (BR-C3-01); the same PO in other case and spacing warns, never blocks.
        var second = await _k.Post("/api/sale-orders", new
        {
            PartnerId = customer.Uuid, CurrencyId = pkr, DeliveryMode = "SELF_PICKUP",
            CustomerPoReference = $"  {poRef.ToLowerInvariant()} ", Lines = new[] { SapKit.Line(item, 1m) }
        });
        second.Status.Should().Be(HttpStatusCode.OK, $"T-C3-04 / BR-C3-05: a duplicate PO is a warning — {second}");
        second.Message.Should().Contain("Warning").And.Contain(o1.S("soNumber")!);
        var so2 = second.Result.GetGuid();
        (await _k.GetSaleOrderAsync(so2)).S("sourceType").Should().Be("MANUAL");

        var dupes = await _k.Ok(_k.Get($"/api/sale-orders/customer-po-check?reference={Uri.EscapeDataString(poRef.ToLowerInvariant())}"), "PO check");
        dupes.Items().Select(x => x.G("uuid")).Should().BeEquivalentTo(new[] { so1, so2 });
        (await _k.Ok(_k.Get($"/api/sale-orders/customer-po-check?reference={poRef}&excludeUuid={so2}"), "PO check excluding itself"))
            .Items().Select(x => x.G("uuid")).Should().Equal(so1);
        (await _k.Ok(_k.Get("/api/sale-orders/customer-po-check?reference=%20"), "blank reference")).Items().Should().BeEmpty();

        // T-C3-03: the PO document — uploaded as the order's CUSTOMER_PO file, then linked.
        var file1 = await UploadAsync("CUSTOMER_PO", so1, "customer-po.pdf");
        (await _k.Ok(_k.Get($"/api/attachments?interface=CUSTOMER_PO&documentId={so1}"), "list the order's PO files"))
            .Items().Select(x => x.G("uuid")).Should().Contain(file1);

        var otherOrdersFile = await UploadAsync("CUSTOMER_PO", so2, "other-po.pdf");
        var generalFile     = await UploadAsync("SALE_ORDER", so1, "general.pdf");
        (await _k.Put($"/api/sale-orders/{so1}/customer-po", new { CustomerPoReference = poRef, CustomerPoAttachmentUuid = otherOrdersFile }))
            .Status.Should().Be(HttpStatusCode.BadRequest, "another order's PO file cannot be linked");
        (await _k.Put($"/api/sale-orders/{so1}/customer-po", new { CustomerPoReference = poRef, CustomerPoAttachmentUuid = generalFile }))
            .Status.Should().Be(HttpStatusCode.BadRequest, "a general SALE_ORDER file is not the PO document");
        (await _k.Put($"/api/sale-orders/{so1}/customer-po", new { CustomerPoReference = poRef, CustomerPoAttachmentUuid = Guid.NewGuid() }))
            .Status.Should().Be(HttpStatusCode.BadRequest, "an unknown file");

        await _k.Ok(_k.Put($"/api/sale-orders/{so1}/customer-po", new
        {
            CustomerPoReference = poRef, CustomerPoDate = poDate, CustomerPoAttachmentUuid = file1
        }), "link the PO document");
        var linked = await _k.GetSaleOrderAsync(so1);
        linked.NG("customerPoAttachmentUuid").Should().Be(file1);
        linked.S("customerPoReference").Should().Be(poRef);

        // The PO can still change after confirmation (it often arrives late), but not once cancelled.
        await _k.Ok(_k.Post($"/api/sale-orders/{so2}/cancel", new { Reason = "dup" }), "cancel the duplicate");
        (await _k.Put($"/api/sale-orders/{so2}/customer-po", new { CustomerPoReference = "LATE" }))
            .Status.Should().Be(HttpStatusCode.BadRequest, "a CANCELLED order's PO is frozen");

        (await _k.Ok(_k.Get($"/api/sale-orders?search={poRef}&pageSize=100"), "list search by PO"))
            .A("data").Select(r => r.G("uuid")).Should().BeEquivalentTo(new[] { so1, so2 });
    }

    // ── C4: manual reservation and the indicators ─────────────────────────────────

    [Fact]
    public async Task Reserve_partial_full_all_release_and_cancel_drive_reservedQty_and_the_indicator_from_the_ledger()
    {
        var (pkr, wh, vendor, customer) = await SetupAsync();
        var p1 = await _k.CreateProductAsync("Res Widget", purchasePrice: 60m, sellingPrice: 100m);
        var p2 = await _k.CreateProductAsync("Res Bolt", purchasePrice: 20m, sellingPrice: 50m);

        var so = await _k.CreateSaleOrderAsync(customer, pkr, SapKit.Line(p1, 10m), SapKit.Line(p2, 6m));
        var draftLine = (await SoLine(so, p1.VariantUuid)).G("uuid");
        (await Reserve(so, draftLine, new { })).Status.Should().Be(HttpStatusCode.BadRequest, "a DRAFT order reserves by confirming");

        // Confirmed with nothing in stock: every line RED, nothing held, everything reservable (T-C4-01).
        await _k.Ok(_k.Post($"/api/sale-orders/{so}/confirm"), "confirm with no stock");
        var order = await _k.GetSaleOrderAsync(so);
        order.S("status").Should().Be("CONFIRMED");
        foreach (var l in order.A("lines"))
        {
            l.S("deliveryIndicator").Should().Be("RED");
            l.D("reservedQty").Should().Be(0m);
            l.D("reservableQty").Should().Be(l.D("quantity"));
        }
        var l1 = (await SoLine(so, p1.VariantUuid)).G("uuid");
        var l2 = (await SoLine(so, p2.VariantUuid)).G("uuid");

        var none = await _k.Ok(Reserve(so, l1, new { }), "reserve with nothing free");
        none.S("outcome").Should().Be("NONE_AVAILABLE");
        none.D("changedQty").Should().Be(0m);
        (await LedgerHeldAsync(so)).Should().Be(0m);

        // Stock arrives: 4 of p1, 20 of p2 (an independent PO — nothing links it to the order).
        await _k.StockUpAsync(vendor, wh, (p1, 4m, 60m), (p2, 20m, 20m));
        (await LedgerHeldAsync(so)).Should().Be(0m, "an unlinked GRN reserves nothing for the order");

        // Short: asked first, nothing held …
        var ask = await _k.Ok(Reserve(so, l1, new { AllowPartial = false }), "reserve 10 with 4 free");
        ask.S("outcome").Should().Be("NEEDS_CONFIRMATION");
        ask.D("availableQty").Should().Be(4m);
        ask.D("requestedQty").Should().Be(10m);
        ask.D("changedQty").Should().Be(0m);
        ask.NG("warehouseUuid").Should().Be(wh.Uuid);
        (await SoLine(so, p1.VariantUuid)).D("reservedQty").Should().Be(0m, "NEEDS_CONFIRMATION holds nothing");

        // … then partial (T-C4-03): YELLOW.
        var part = await _k.Ok(Reserve(so, l1, new { Quantity = 10m, AllowPartial = true, WarehouseUuid = wh.Uuid }), "reserve partial");
        part.S("outcome").Should().Be("PARTIAL");
        part.D("changedQty").Should().Be(4m);
        part.D("reservedQty").Should().Be(4m);
        part.D("reservableQty").Should().Be(6m);
        part.S("deliveryIndicator").Should().Be("YELLOW");
        part.S("lineStatus").Should().Be("RESERVED");
        var r1 = await SoLine(so, p1.VariantUuid);
        (r1.D("reservedQty"), r1.D("reservableQty"), r1.S("deliveryIndicator"), r1.S("status")).Should().Be((4m, 6m, "YELLOW", "RESERVED"));
        (await LedgerHeldAsync(so, l1)).Should().Be(4m);

        // Full (T-C4-02): BLUE; nothing left to reserve afterwards.
        var full = await _k.Ok(Reserve(so, l2, new { Quantity = 6m }), "reserve full");
        full.S("outcome").Should().Be("RESERVED");
        full.D("changedQty").Should().Be(6m);
        full.S("deliveryIndicator").Should().Be("BLUE");
        (await Reserve(so, l2, new { })).Status.Should().Be(HttpStatusCode.BadRequest, "nothing left to reserve on the line");
        (await Reserve(so, l1, new { Quantity = 7m, AllowPartial = true })).Status
            .Should().Be(HttpStatusCode.BadRequest, "7 is more than the 6 still reservable");

        // TTL (BR-C4-06): the manual hold expires like confirm's.
        var ttl = await _f.QueryAsync(
            "SELECT MIN(DATEDIFF(MINUTE, ReservedAt, ExpiresAt)) AS M FROM inventory.StockReservations WHERE SourceUuid = @so AND Status = 'ACTIVE'", ("@so", so));
        ttl[0]["M"].Should().NotBeNull("a manual hold carries SaleOrderConfig.ReservationTtlHours");
        Convert.ToInt32(ttl[0]["M"]).Should().BeGreaterThan(0);

        // Release part, then the rest (T-C4-05): BLUE → YELLOW → RED, stock back.
        var freeBefore = await FreeAsync(customer, pkr, p2);
        var rel = await _k.Ok(Release(so, l2, new { Quantity = 2m, Reason = "customer asked to wait" }), "release 2");
        rel.S("outcome").Should().Be("RELEASED");
        rel.D("changedQty").Should().Be(2m);
        (rel.D("reservedQty"), rel.S("deliveryIndicator")).Should().Be((4m, "YELLOW"));
        var relAll = await _k.Ok(Release(so, l2, new { }), "release the rest");
        (relAll.D("changedQty"), relAll.D("reservedQty"), relAll.S("deliveryIndicator"), relAll.S("lineStatus")).Should().Be((4m, 0m, "RED", "OPEN"));
        (await Release(so, l2, new { })).Status.Should().Be(HttpStatusCode.BadRequest, "nothing held to release");
        (await FreeAsync(customer, pkr, p2)).Should().Be(freeBefore + 6m, "released stock is free again");

        // Reserve-all (T-C4-10): more p1 arrives; every line that can take stock does.
        await _k.StockUpAsync(vendor, wh, (p1, 10m, 60m));
        var all = await _k.Ok(_k.Post($"/api/sale-orders/{so}/reserve-all", new { AllowPartial = true }), "reserve all");
        all.I("reservedLineCount").Should().Be(2);
        all.A("lines").Should().OnlyContain(x => x.S("outcome") == "RESERVED" && x.S("deliveryIndicator") == "BLUE");
        all.A("lines").Single(x => x.G("lineUuid") == l1).D("changedQty").Should().Be(6m, "only the 6 still reservable");
        var blue = await _k.GetSaleOrderAsync(so);
        blue.A("lines").Should().OnlyContain(x => x.S("deliveryIndicator") == "BLUE" && x.D("reservableQty") == 0m);
        blue.A("lines").Single(x => x.G("uuid") == l1).D("reservedQty").Should().Be(10m);
        blue.A("lines").Single(x => x.G("uuid") == l2).D("reservedQty").Should().Be(6m);
        (await LedgerHeldAsync(so)).Should().Be(16m, "the computed figures are the ledger's");

        var again = await _k.Ok(_k.Post($"/api/sale-orders/{so}/reserve-all", new { }), "reserve all again");
        again.I("unchangedLineCount").Should().Be(2);
        again.A("lines").Should().OnlyContain(x => x.S("outcome") == "SKIPPED");

        // Cancel (T-C4-06 / BR-C4-05): every hold released, lines GREY with nothing reserved.
        var freeP1 = await FreeAsync(customer, pkr, p1);
        await _k.Ok(_k.Post($"/api/sale-orders/{so}/cancel", new { Reason = "customer withdrew" }), "cancel");
        var cancelled = await _k.GetSaleOrderAsync(so);
        cancelled.S("status").Should().Be("CANCELLED");
        cancelled.A("lines").Should().OnlyContain(x => x.D("reservedQty") == 0m && x.S("deliveryIndicator") == "GREY");
        (await LedgerHeldAsync(so)).Should().Be(0m);
        (await FreeAsync(customer, pkr, p1)).Should().Be(freeP1 + 10m);
        (await Reserve(so, l1, new { })).Status.Should().Be(HttpStatusCode.BadRequest, "a cancelled order holds nothing");
        (await _k.Post($"/api/sale-orders/{so}/reserve-all", new { })).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Confirm_time_auto_reservation_and_the_expiry_sweep_show_in_the_same_computed_reservedQty()
    {
        var (pkr, wh, vendor, customer) = await SetupAsync();
        var p = await _k.CreateProductAsync("Auto Widget", purchasePrice: 30m, sellingPrice: 70m);
        await _k.StockUpAsync(vendor, wh, (p, 10m, 30m));

        // In stock: confirm holds the line in full → BLUE, nothing left to reserve.
        var full = await _k.CreateSaleOrderAsync(customer, pkr, SapKit.Line(p, 4m));
        await _k.ConfirmSaleOrderAsync(full);
        var fl = await SoLine(full, p.VariantUuid);
        (fl.S("status"), fl.D("reservedQty"), fl.D("reservableQty"), fl.S("deliveryIndicator")).Should().Be(("RESERVED", 4m, 0m, "BLUE"));
        (await LedgerHeldAsync(full)).Should().Be(4m);

        // SPLIT: 6 free of 10 → confirm holds 6 → YELLOW, 4 reservable.
        var split = await _k.CreateSaleOrderAsync(customer, pkr, SapKit.Line(p, 10m));
        await _k.Ok(_k.Post($"/api/sale-orders/{split}/confirm"), "confirm short");
        var sl = await SoLine(split, p.VariantUuid);
        (sl.S("fulfillmentMode"), sl.D("reservedQty"), sl.D("reservableQty"), sl.S("deliveryIndicator")).Should().Be(("SPLIT", 6m, 4m, "YELLOW"));
        sl.ND("deficitQty").Should().Be(4m);
        (await LedgerHeldAsync(split)).Should().Be(6m);

        // A manual release frees confirm's hold like its own.
        var rel = await _k.Ok(Release(split, sl.G("uuid"), new { Quantity = 1m }), "release part of confirm's hold");
        (rel.D("reservedQty"), rel.D("reservableQty")).Should().Be((5m, 5m));
        (await SoLine(split, p.VariantUuid)).ND("deficitQty").Should().Be(5m, "deficit follows the ledger");

        // TTL expiry (T-C4-07): the sweep releases the expired hold and the computed figure drops with it.
        await _f.ExecuteAsync(
            "UPDATE inventory.StockReservations SET ExpiresAt = DATEADD(HOUR, -1, SYSUTCDATETIME()) WHERE SourceUuid = @so AND Status = 'ACTIVE'",
            ("@so", full));
        await _f.RunInScopeAsync<SMS.Modules.Demand.Services.ReservationExpirySweepJob>(j => j.RunAsync());
        var expired = await SoLine(full, p.VariantUuid);
        (expired.D("reservedQty"), expired.D("reservableQty"), expired.S("deliveryIndicator"), expired.S("status")).Should().Be((0m, 4m, "RED", "OPEN"));
        (await LedgerHeldAsync(full)).Should().Be(0m);
        (await LedgerHeldAsync(split)).Should().Be(5m, "the sweep touched only the expired hold");

        // And a manual reserve takes it back.
        var back = await _k.Ok(Reserve(full, expired.G("uuid"), new { }), "reserve again after expiry");
        (back.S("outcome"), back.D("reservedQty"), back.S("deliveryIndicator")).Should().Be(("RESERVED", 4m, "BLUE"));
    }

    [Fact]
    public async Task Two_racing_reserves_of_one_line_never_hold_more_than_the_line()
    {
        var (pkr, wh, vendor, customer) = await SetupAsync();
        var p = await _k.CreateProductAsync("Race Widget", purchasePrice: 5m, sellingPrice: 9m);

        var so = await _k.CreateSaleOrderAsync(customer, pkr, SapKit.Line(p, 5m));
        await _k.Ok(_k.Post($"/api/sale-orders/{so}/confirm"), "confirm with no stock");
        await _k.StockUpAsync(vendor, wh, (p, 50m, 5m));
        var line = (await SoLine(so, p.VariantUuid)).G("uuid");

        var calls = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Reserve(so, line, new { })));
        calls.Count(c => c.Status == HttpStatusCode.OK).Should().Be(1,
            $"one reserve holds the 5, the rest find nothing left — {string.Join(" | ", calls.Select(c => c.ToString()))}");
        calls.Where(c => c.Status != HttpStatusCode.OK).Should().OnlyContain(c => c.Status == HttpStatusCode.BadRequest || c.Status == HttpStatusCode.Conflict);
        (await LedgerHeldAsync(so)).Should().Be(5m);
        (await SoLine(so, p.VariantUuid)).D("reservedQty").Should().Be(5m);
    }
}
