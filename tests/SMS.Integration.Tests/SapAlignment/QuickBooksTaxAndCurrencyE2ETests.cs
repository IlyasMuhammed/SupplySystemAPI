using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.QuickBooks;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Jobs;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Integration.Tests.SapAlignment;

/// <summary>
/// SAP alignment scenarios 4 and 5 — what QuickBooks receives, through the real host with only Intuit faked:
/// a line's tax code is mapped by code before its rate (S-11), a line from before tax codes still issues,
/// prints and maps by its rate, a cancelled invoice is voided (S-7), and with QuickBooks' multicurrency on a
/// foreign invoice is sent with SMS's rate for its date (S-10). A35 D-5 (supersedes S-5): a document with no rate on file
/// is refused at its lock (SO confirm), so SMS no longer issues a foreign invoice without a rate; rates come from
/// api/currency-rates (A35 D-3) and a historical correction never changes an issued invoice.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~QuickBooksTaxAndCurrencyE2ETests</c>.</para>
/// </summary>
public sealed class QuickBooksTaxAndCurrencyE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private const string Qb    = "/api/integrations/quickbooks";
    private const string Realm = "9130000000000555";

    private readonly SapKit _k;
    private readonly SapWebApplicationFactory _f;

    public QuickBooksTaxAndCurrencyE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "QB");
    }

    [Fact]
    public async Task Coded_lines_map_by_code_legacy_lines_by_rate_cancellations_void_and_foreign_invoices_carry_the_rate()
    {
        var today = DateTime.UtcNow.Date;

        // ── SCM side ───────────────────────────────────────────────────────────────
        var pkr = await _k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        var usd = await _k.EnsureCurrencyAsync("USD", "US Dollar", "$");
        await _k.SetBaseCurrencyAsync(pkr);
        var gst17 = await _k.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        var pgst17 = await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: false);
        var gst5   = await _k.EnsureTaxCodeAsync("GST5", 5m, "SALES", isDefault: false);    // no code row: falls back to the 5% row
        var gst12  = await _k.EnsureTaxCodeAsync("GST12", 12m, "SALES", isDefault: false);  // neither a code row nor a 12% row
        await _k.CreateApproverPlaceholdersAsync();
        await _k.CreateRateAsync("USD", "PKR", 280m, today.AddDays(-30));

        var wh          = await _k.CreateWarehouseAsync();
        var vendor      = await _k.CreateVendorAsync("QB Stock Vendor");
        var pkrCustomer = await _k.CreateCustomerAsync("QB Home Customer");
        var usdCustomer = await _k.CreateCustomerAsync("QB Dollar Customer");
        await SetPreferredCurrencyAsync(usdCustomer, usd);

        var coded  = await _k.CreateProductAsync("QB Coded Item", purchasePrice: 60m, sellingPrice: 100m);
        var legacy = await _k.CreateProductAsync("QB Legacy Item", purchasePrice: 30m, sellingPrice: 50m);
        var dollar = await _k.CreateProductAsync("QB Dollar Item", purchasePrice: 1000m, sellingPrice: 999m);
        await _k.StockUpAsync(vendor, wh, (coded, 20m, 60m), (legacy, 20m, 30m), (dollar, 20m, 1000m));
        await _k.Ok(_k.Post("/api/pricing-rules", new
        {
            VariantUuid = dollar.VariantUuid, PriceType = "SELLING", UnitPrice = 10m, CurrencyId = usd, EffectiveFrom = today.AddDays(-60)
        }), "USD price rule");

        // ── QuickBooks side: multicurrency on, USD active, a second 17% tax code ─────
        _f.Company.MultiCurrencyEnabled = true;
        _f.Company.ExtraCurrencies.Add(new RemoteCurrency("USD", "US Dollar"));
        _f.Company.ExtraTaxCodes.Add(new RemoteTaxCode("STD2", "GST 17% by rate", null, true, 17m, true));
        _f.Company.ExtraTaxCodes.Add(new RemoteTaxCode("R5", "Reduced 5%", null, true, 5m, true));

        await ConnectAsync();
        (await _k.Ok(_k.Get($"{Qb}/connection"), "connection")).B("multiCurrencyEnabled").Should().BeTrue("captured from QuickBooks' preferences");

        await _k.Ok(_k.Put($"{Qb}/settings", new
        {
            DefaultIncomeAccountId = "1", DefaultExpenseAccountId = "3", FreightExpenseAccountId = "4", DiscountAccountId = "2",
            DefaultPurchaseTaxCodeId = "NON", ItemTypeDefault = "NonInventory", PartnerScope = "OnlyWhenReferenced"
        }), "settings");

        var mappings = await _k.Ok(_k.Put($"{Qb}/tax-mappings", new
        {
            Mappings = new object[]
            {
                new { SourceTaxCode = "GST17", TaxPercent = 17m, QboTaxCodeId = "STD" },
                new { SourceTaxCode = "PGST17", TaxPercent = 17m, QboTaxCodeId = "STD" },
                new { SourceTaxCode = (string?)null, TaxPercent = 17m, QboTaxCodeId = "STD2" },
                new { SourceTaxCode = (string?)null, TaxPercent = 0m, QboTaxCodeId = "NON" },
                new { SourceTaxCode = (string?)null, TaxPercent = 5m, QboTaxCodeId = "R5" }
            }
        }), "tax mappings by code and by rate");
        mappings.Items().Should().Contain(m => m.S("sourceTaxCode") == "GST17" && m.S("qboTaxCodeId") == "STD", J.Short(mappings));
        mappings.Items().Should().Contain(m => m.S("sourceTaxCode") == null && m.D("taxPercent") == 17m && m.S("qboTaxCodeId") == "STD2");

        var badMapping = await _k.Put($"{Qb}/tax-mappings", new
        {
            Mappings = new[] { new { SourceTaxCode = "GST17", TaxPercent = 17m, QboTaxCodeId = "NOPE" } }
        });
        badMapping.Status.Should().Be(HttpStatusCode.BadRequest, $"a QuickBooks tax code that does not exist is refused — {badMapping}");

        await _k.Ok(_k.Post($"{Qb}/matching/complete", new { Confirmed = true }), "matching complete");
        var preflight = await _k.Ok(_k.Get($"{Qb}/preflight"), "preflight");
        preflight.B("canGoLive").Should().BeTrue(J.Short(preflight));
        (await _k.Ok(_k.Post($"{Qb}/mode", new { Mode = "Live" }), "go live")).S("mode").Should().Be("Live");

        // ═══ 1. A coded line maps by code, a legacy line (no code) by its rate ════════
        var (_, _, homeInvoice) = await _k.SellAsync(pkrCustomer, pkr,
            SapKit.Line(coded, 1m, taxCode: gst17),
            SapKit.Line(legacy, 1m, taxPercent: 17m));
        var home = await _k.GetInvoiceAsync(homeInvoice);
        home.A("lines").Select(l => l.S("taxCode")).Should().BeEquivalentTo(new[] { "GST17", null });

        var pdf = await _k.Admin.GetAsync($"/api/sales-invoices/{homeInvoice}/pdf");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK, "an invoice with a line from before tax codes still prints");

        await RunJobsAsync(rounds: 4);
        (await MapAsync(homeInvoice)).Should().Match<MapRow>(m => m.State == "Synced", "the home-currency invoice is sent");

        var homeQbo = CreatedInvoice(home.S("invoiceNumber")!);
        homeQbo.Lines.Single(l => l.UnitPrice == 100m).TaxCodeId.Should().Be("STD", "GST17 is mapped by its code");
        homeQbo.Lines.Single(l => l.UnitPrice == 50m).TaxCodeId.Should().Be("STD2", "the legacy 17% line is mapped by its rate");
        homeQbo.ExchangeRate.Should().BeNull("a home-currency document carries no exchange rate");

        var payload = await PayloadAsync(homeInvoice);
        var payloadLines = payload.A("lines");
        payloadLines.Single(l => l.D("unitPrice") == 100m).S("taxCode").Should().Be("GST17");
        payloadLines.Single(l => l.D("unitPrice") == 50m).Has("taxCode")
            .Should().BeFalse("a null tax code is omitted from the stored JSON, so old fingerprints hold (S-11)");

        // ═══ 1b. A coded line needs its CODE mapped — a mapped rate is not enough (two codes can share a rate);
        //         saving the code's mapping re-checks the blocked invoice and sends it ═══════════════════════
        var (_, _, gst5Invoice)  = await _k.SellAsync(pkrCustomer, pkr, SapKit.Line(coded, 1m, taxCode: gst5));
        var (_, _, gst12Invoice) = await _k.SellAsync(pkrCustomer, pkr, SapKit.Line(coded, 1m, taxCode: gst12));
        var gst5No  = (await _k.GetInvoiceAsync(gst5Invoice)).S("invoiceNumber")!;
        var gst12No = (await _k.GetInvoiceAsync(gst12Invoice)).S("invoiceNumber")!;
        await RunJobsAsync(rounds: 3);
        foreach (var (uuid, code) in new[] { (gst5Invoice, "GST5"), (gst12Invoice, "GST12") })
        {
            var map = await MapAsync(uuid);
            map.State.Should().Be("Blocked", $"{code} has no code row — {map.LastErrorCode}: {map.LastError}");
            map.LastErrorCode.Should().Be("TAX_UNMAPPED");
            map.LastError.Should().Contain(code, "the message names the SMS code to map");
        }
        _f.Company.Records(SyncKind.SalesInvoice).Should().NotContain(r => r.Name == gst5No || r.Name == gst12No);

        await _k.Ok(_k.Put($"{Qb}/tax-mappings", new
        {
            Mappings = new object[]
            {
                new { SourceTaxCode = "GST17", TaxPercent = 17m, QboTaxCodeId = "STD" },
                new { SourceTaxCode = "PGST17", TaxPercent = 17m, QboTaxCodeId = "STD" },
                new { SourceTaxCode = "GST5", TaxPercent = 5m, QboTaxCodeId = "R5" },
                new { SourceTaxCode = (string?)null, TaxPercent = 17m, QboTaxCodeId = "STD2" },
                new { SourceTaxCode = (string?)null, TaxPercent = 0m, QboTaxCodeId = "NON" },
                new { SourceTaxCode = (string?)null, TaxPercent = 5m, QboTaxCodeId = "R5" }
            }
        }), "map GST5 by code");
        await RunJobsAsync(rounds: 3);
        (await MapAsync(gst5Invoice)).State.Should().Be("Synced", "saving the mapping checks the blocked record again and sends it");
        CreatedInvoice(gst5No).Lines.Single().TaxCodeId.Should().Be("R5");
        (await MapAsync(gst12Invoice)).State.Should().Be("Blocked", "GST12 is still unmapped");

        // ═══ 2. Cancelling the issued invoice voids it in QuickBooks ═══════════════════
        await _k.Ok(_k.Post($"/api/sales-invoices/{homeInvoice}/cancel", new { reason = "Raised against the wrong customer" }), "cancel");
        _f.Company.ResetCalls();
        await RunJobsAsync(rounds: 2);
        var remoteId = (await MapAsync(homeInvoice)).RemoteId;
        _f.Company.Writes.Should().ContainSingle(w => w.Operation == "Void" && w.Name == remoteId, "a cancelled invoice is voided, once");
        _f.Company.Records(SyncKind.SalesInvoice).Single(r => r.Id == remoteId).Active.Should().BeFalse();

        // ═══ 3. A USD invoice goes in USD at SMS's rate for its date ═══════════════════
        var (usdSo, _, usdInvoice) = await _k.SellAsync(usdCustomer, usd, SapKit.Line(dollar, 1m, taxCode: gst17));
        (await _k.GetSaleOrderAsync(usdSo)).A("lines").Single().D("unitPrice").Should().Be(10m);
        var usdDoc = await _k.GetInvoiceAsync(usdInvoice);
        usdDoc.ND("exchangeRate").Should().Be(280m);

        _f.Company.ResetCalls();
        await RunJobsAsync(rounds: 4);
        var usdMap = await MapAsync(usdInvoice);
        usdMap.State.Should().Be("Synced", $"multicurrency is on, USD is active and a rate is on file — {usdMap.LastErrorCode}: {usdMap.LastError}");
        var usdQbo = CreatedInvoice(usdDoc.S("invoiceNumber")!);
        usdQbo.CurrencyCode.Should().Be("USD");
        usdQbo.ExchangeRate.Should().Be(280m, "QuickBooks records the foreign invoice at SMS's rate for its date");
        usdQbo.Lines.Single().TaxCodeId.Should().Be("STD");
        var qboCustomer = _f.Company.Records(SyncKind.Customer).Single(r => r.Id == usdQbo.CustomerId).Entity
            .Should().BeAssignableTo<RemoteParty>().Subject;
        qboCustomer.CurrencyCode.Should().Be("USD", "QuickBooks keeps the customer in the invoice's currency");

        // ═══ 4. A35 D-5 (supersedes S-5): no rate on file refuses the LOCK itself — the EUR order cannot be confirmed, so no
        //        issued invoice can lack its rate (QuickBooks' EXCHANGE_RATE_MISSING is now reachable only by pre-A35 data) ══
        var eur = await _k.EnsureCurrencyAsync("EUR", "Euro", "€");
        await _k.Ok(_k.Post("/api/pricing-rules", new
        {
            VariantUuid = coded.VariantUuid, PriceType = "SELLING", UnitPrice = 20m, CurrencyId = eur, EffectiveFrom = today.AddDays(-60)
        }), "EUR price rule");
        var eurSo = await _k.CreateSaleOrderAsync(pkrCustomer, eur, SapKit.Line(coded, 1m, taxCode: gst17));
        var noRate = await _k.Post($"/api/sale-orders/{eurSo}/confirm");
        noRate.Status.Should().Be(HttpStatusCode.BadRequest, $"D-5: a EUR order in a PKR org needs a EUR rate to lock — {noRate}");
        noRate.Message.Should().Be($"No exchange rate for EUR on {today:yyyy-MM-dd}. Add one under Settings → Exchange Rates.");
        (await _k.GetSaleOrderAsync(eurSo)).S("status").Should().Be("DRAFT");

        // The USD rate moves to 281.5 from yesterday (the 280 row is closed the day before).
        var lastRate = await _k.CreateRateAsync("USD", "PKR", 281.5m, today.AddDays(-1));
        // ═══ 5. Bills: a purchase code maps by code; tax keyed as an amount takes the default purchase code ══
        var billVendor = await _k.CreateVendorAsync("QB Bill Vendor");
        var codedBillNo  = $"{_k.Marker}-B1";
        var legacyBillNo = $"{_k.Marker}-B2";
        var codedBill  = await ApprovedBillAsync(billVendor, codedBillNo, 1000m, taxAmount: 0m, taxCode: pgst17);
        var legacyBill = await ApprovedBillAsync(billVendor, legacyBillNo, 400m, taxAmount: 40m, taxCode: null);

        _f.Company.ResetCalls();
        await RunJobsAsync(rounds: 4);
        (await MapAsync(codedBill)).State.Should().Be("Synced");
        (await MapAsync(legacyBill)).State.Should().Be("Synced");
        var codedQboBill = CreatedBill(codedBillNo);
        codedQboBill.Lines.Should().ContainSingle().Which.Should().Match<RemoteBillLine>(l =>
            l.Amount == 1000m && l.TaxCodeId == "STD" && l.AccountId == "3", "a header-only bill is one line, taxed by its code");
        CreatedBill(legacyBillNo).Lines.Should().OnlyContain(l => l.TaxCodeId == "NON",
            "a bill with no code and no rate takes the default purchase tax code (D-10)");
        var billPayload = await PayloadAsync(codedBill);
        billPayload.A("lines").Single().S("taxCode").Should().Be("PGST17");

        // ═══ 6. QuickBooks gets the rate the invoice was issued at, not a rate changed afterwards ══

        var (_, _, lateInvoice) = await _k.SellAsync(usdCustomer, usd, SapKit.Line(dollar, 1m, taxCode: gst17));
        var lateDoc = await _k.GetInvoiceAsync(lateInvoice);
        lateDoc.ND("exchangeRate").Should().Be(281.5m, "snapshotted at issue");
        await _k.CorrectRateAsync(lastRate, 295m); // a historical correction after the invoice was issued
        (await _k.GetInvoiceAsync(lateInvoice)).ND("exchangeRate").Should().Be(281.5m, "a later rate change never changes an issued document (S-5)");

        _f.Company.ResetCalls();
        await RunJobsAsync(rounds: 3);
        (await MapAsync(lateInvoice)).State.Should().Be("Synced");
        CreatedInvoice(lateDoc.S("invoiceNumber")!).ExchangeRate.Should().Be(281.5m,
            "QuickBooks should record the issued invoice at the rate SMS snapshotted for it (S-5/S-10); otherwise SMS's BaseGrandTotal and QuickBooks' home amount disagree");

        // ═══ 7. A bill already in QuickBooks is reversed in SCM: it must be flagged for the accountant ══
        await _k.Ok(_k.Post($"/api/finance/invoices/{codedBill}/reverse", new { Reason = "Duplicate of another bill" }), "reverse synced bill");
        _f.Company.ResetCalls();
        await RunJobsAsync(rounds: 2);
        _f.Company.Writes.Should().NotContain(w => w.Kind == SyncKind.Bill, "the gateway cannot void a bill and must not re-send a reversed one");
        var reversedMap = await MapAsync(codedBill);
        Console.WriteLine($"PROBE reversed bill map: State={reversedMap.State} Code={reversedMap.LastErrorCode} Error={reversedMap.LastError}");
        var flagged = (await _f.QueryAsync("SELECT Warning FROM integration.EntityMaps WHERE ExternalId = @id", ("@id", codedBill.ToString())))
            .Single()["Warning"] as string;
        Console.WriteLine($"PROBE reversed bill warning: {flagged ?? "(none)"}");
        reversedMap.State.Should().Be("NeedsResolution",
            "plan (Out of scope): a reversed bill already in QuickBooks 'is flagged for the accountant' — the sync dashboard must show it, " +
            $"not a plain Synced row (State={reversedMap.State}, Warning={flagged ?? "null"})");
        reversedMap.LastErrorCode.Should().Be("ACCOUNTANT_ACTION_NEEDED");
        reversedMap.LastError.Should().Contain("Duplicate of another bill", "the reason travels with the flag");
    }

    private async Task<Guid> ApprovedBillAsync(Partner vendor, string number, decimal subtotal, decimal taxAmount, Guid? taxCode)
    {
        var uuid = (await _k.Ok(_k.Post("/api/finance/invoices", new
        {
            SupplierInvoiceNo = number, SupplierId = vendor.Uuid, InvoiceDate = DateTime.UtcNow.Date, ReceivedDate = DateTime.UtcNow.Date,
            DueDate = DateTime.UtcNow.Date.AddDays(30), Currency = "PKR", Subtotal = subtotal, TaxAmount = taxAmount, TaxCodeUuid = taxCode
        }), $"bill {number}")).GetGuid();
        await _k.Ok(_k.Post($"/api/finance/invoices/{uuid}/approve", new { Notes = "ok" }), $"approve bill {number}");
        return uuid;
    }

    private RemoteBill CreatedBill(string docNumber) =>
        _f.Company.Records(SyncKind.Bill).Should().ContainSingle(r => r.Name == docNumber, $"QuickBooks holds bill {docNumber}").Subject
            .Entity.Should().BeOfType<RemoteBill>().Subject;

    // ── helpers ──────────────────────────────────────────────────────────────────

    private sealed record MapRow(string Kind, string State, string? RemoteId, string? LastErrorCode, string? LastError);

    private async Task<MapRow> MapAsync(Guid externalId)
    {
        var rows = await _f.QueryAsync(
            "SELECT Kind, State, RemoteId, LastErrorCode, LastError FROM integration.EntityMaps WHERE ExternalId = @id",
            ("@id", externalId.ToString()));
        rows.Should().ContainSingle($"exactly one map for {externalId}");
        var r = rows[0];
        return new MapRow(r["Kind"]!.ToString()!, r["State"]!.ToString()!, r["RemoteId"] as string, r["LastErrorCode"] as string, r["LastError"] as string);
    }

    private async Task<JsonElement> PayloadAsync(Guid externalId)
    {
        var json = (string)(await _f.QueryAsync("SELECT PayloadJson FROM integration.EntityMaps WHERE ExternalId = @id", ("@id", externalId.ToString())))
            .Single()["PayloadJson"]!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private RemoteInvoice CreatedInvoice(string docNumber) =>
        _f.Company.Records(SyncKind.SalesInvoice).Should().ContainSingle(r => r.Name == docNumber, $"QuickBooks holds invoice {docNumber}").Subject
            .Entity.Should().BeOfType<RemoteInvoice>().Subject;

    private async Task RunJobsAsync(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            await _f.RunInScopeAsync<DependencyJob>(job => job.RunOnceAsync());
            await _f.RunInScopeAsync<SyncOutboxJob>(job => job.RunOnceAsync());
        }
    }

    private async Task ConnectAsync()
    {
        var consent = await _k.Ok(_k.Post($"{Qb}/connect"), "connect");
        var state = Uri.UnescapeDataString(new Uri(consent.S("consentUrl")!).Query.TrimStart('?').Split('&')
            .Single(p => p.StartsWith("state=")).Substring("state=".Length));
        using var anonymous = _f.CreateAnonymousClient();
        var cb = await anonymous.GetAsync($"{Qb}/callback?code=sap-code&state={Uri.EscapeDataString(state)}&realmId={Realm}");
        cb.Headers.Location!.ToString().Should().EndWith("result=connected");
    }

    /// <summary>A customer's currency is the partner's preferred currency (Suppliers' edit is where it is set).</summary>
    private async Task SetPreferredCurrencyAsync(Partner partner, Guid currencyId)
    {
        var patch = await _k.Patch($"/api/suppliers/{partner.Uuid}", new { PreferredCurrency = currencyId });
        if (patch.Status != HttpStatusCode.OK)
        {
            Console.WriteLine($"NOTE: PATCH /api/suppliers/{{id}} refused a customer-only partner's preferred currency ({patch}); set in SQL.");
            await _f.ExecuteAsync("UPDATE suppliers.BusinessPartners SET PreferredCurrency = @c WHERE UUID = @u", ("@c", currencyId), ("@u", partner.Uuid));
        }
        var row = (await _f.QueryAsync("SELECT PreferredCurrency FROM suppliers.BusinessPartners WHERE UUID = @u", ("@u", partner.Uuid))).Single();
        row["PreferredCurrency"].Should().Be(currencyId);
    }
}
