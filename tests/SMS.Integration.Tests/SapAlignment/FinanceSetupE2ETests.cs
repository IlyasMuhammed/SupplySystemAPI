using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Integration.Tests.SapAlignment;

/// <summary>
/// SAP alignment (docs/finance/SAP-ALIGNMENT-PLAN.md), scenario 1 — Finance master data through the real host:
/// tax codes (S-1/S-2/S-3: validation, uniqueness, sides, one default per side, deactivation, snapshots on
/// sale-order lines, "codes from rates in use"), exchange rates (S-4: latest on or before the date, reciprocal,
/// same currency = 1, nothing = null), who may change them, and the organization's base currency edit.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~FinanceSetupE2ETests</c>.</para>
/// </summary>
public sealed class FinanceSetupE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private const string TaxCodes = "/api/finance/tax-codes";
    private const string Rates    = "/api/finance/exchange-rates";

    private readonly SapKit _k;

    public FinanceSetupE2ETests(SapWebApplicationFactory factory) => _k = new SapKit(factory, "FS");

    // ═══════════════════════════════════════════════════════════════════════════
    // Tax codes
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Tax_codes_are_validated_listed_by_side_defaulted_deactivated_snapshotted_and_seeded_from_rates_in_use()
    {
        string[] ours = ["GST17", "PGST17", "EXEMPT", "GST18", "GST18OLD", "TAX5"];

        var pkr = await _k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        await _k.SetBaseCurrencyAsync(pkr);
        var customer = await _k.CreateCustomerAsync("Tax Customer");
        var p1 = await _k.CreateProductAsync("Taxed Widget", purchasePrice: 60m, sellingPrice: 100m);
        var p2 = await _k.CreateProductAsync("Legacy Gadget", purchasePrice: 25m, sellingPrice: 40m);

        (await ListAsync()).Where(c => ours.Contains(c.S("code"))).Should().BeEmpty("a new organization has no tax codes");

        // ── Create: the code is trimmed and upper-cased ──────────────────────────
        var gst17 = await _k.Ok(_k.Post(TaxCodes, Code(" gst17 ", 17m, "SALES", isDefault: true, name: "GST 17%")), "create GST17");
        gst17.S("code").Should().Be("GST17");
        gst17.D("ratePercent").Should().Be(17m);
        gst17.S("usage").Should().Be("SALES");
        gst17.B("isDefault").Should().BeTrue();
        gst17.B("isActive").Should().BeTrue();
        gst17.S("name").Should().Be("GST 17%");
        var gst17Id = gst17.G("uuid");

        var pgst17 = await _k.Ok(_k.Post(TaxCodes, Code("PGST17", 17m, "PURCHASE", isDefault: true)), "create PGST17");
        var exempt = await _k.Ok(_k.Post(TaxCodes, Code("EXEMPT", 0m, "BOTH", isDefault: false)), "create EXEMPT");
        pgst17.B("isDefault").Should().BeTrue("PURCHASE has its own default, separate from SALES");
        exempt.D("ratePercent").Should().Be(0m);

        // ── Validation: 400 for bad input, 409 for a code already taken ──────────
        foreach (var (body, why) in new (object, string)[]
                 {
                     (Code("", 5m, "SALES"), "an empty code"),
                     (Code("GST 18", 18m, "SALES"), "a space in the code"),
                     (Code("ABCDEFGHIJKLMNOPQRSTU", 18m, "SALES"), "a 21-character code"),
                     (Code("GST$", 18m, "SALES"), "a character outside A-Z 0-9 _ -"),
                     (Code("NONAME", 18m, "SALES", name: ""), "an empty name"),
                     (Code("NEG", -1m, "SALES"), "a negative rate"),
                     (Code("OVER", 100.01m, "SALES"), "a rate above 100"),
                     (Code("PREC", 17.125m, "SALES"), "a rate with three decimals"),
                     (Code("USAGE", 17m, "RETAIL"), "an unknown usage"),
                     (new { code = "NOUSAGE", name = "x", ratePercent = 1m, isDefault = false, isActive = true }, "no usage at all"),
                 })
        {
            var refused = await _k.Post(TaxCodes, body);
            refused.Status.Should().Be(HttpStatusCode.BadRequest, $"{why} is refused — {refused}");
        }

        (await _k.Post(TaxCodes, Code("GST17", 18m, "SALES"))).Status.Should().Be(HttpStatusCode.Conflict, "codes are unique per organization");
        (await _k.Post(TaxCodes, Code("gst17", 18m, "SALES"))).Status.Should().Be(HttpStatusCode.Conflict, "uniqueness ignores case");

        // ── List by side: BOTH shows on either side ──────────────────────────────
        (await CodesAsync("SALES")).Should().Equal(["GST17", "EXEMPT"], "defaults first, BOTH included, PURCHASE excluded");
        (await CodesAsync("PURCHASE")).Should().Equal(["PGST17", "EXEMPT"]);
        (await CodesAsync("sales")).Should().Equal(["GST17", "EXEMPT"], "the side is case-insensitive");
        (await CodesAsync(null)).Should().BeEquivalentTo(["GST17", "PGST17", "EXEMPT"]);
        (await _k.Get($"{TaxCodes}?side=BOTH")).Status.Should().Be(HttpStatusCode.BadRequest, "BOTH is a usage, not a side");

        // ── One default per side ──────────────────────────────────────────────────
        var gst18Save = await _k.Post(TaxCodes, Code("GST18", 18m, "SALES", isDefault: true));
        gst18Save.Status.Should().Be(HttpStatusCode.OK, gst18Save.ToString());
        gst18Save.Message.Should().Contain("GST17", "the message says which code stopped being the default");
        var gst18Id = gst18Save.Result.G("uuid");
        (await DefaultsAsync()).Should().BeEquivalentTo(["GST18", "PGST17"], "a new SALES default replaces GST17 only");

        await Save(gst17Id, Code("GST17", 17m, "SALES", isDefault: true, name: "GST 17%"));
        (await DefaultsAsync()).Should().BeEquivalentTo(["GST17", "PGST17"]);

        await Save(exempt.G("uuid"), Code("EXEMPT", 0m, "BOTH", isDefault: true));
        (await DefaultsAsync()).Should().BeEquivalentTo(["EXEMPT"], "a BOTH default competes with the default of every side");

        await Save(gst17Id, Code("GST17", 17m, "SALES", isDefault: true, name: "GST 17%"));
        await Save(pgst17.G("uuid"), Code("PGST17", 17m, "PURCHASE", isDefault: true));
        (await DefaultsAsync()).Should().BeEquivalentTo(["GST17", "PGST17"]);

        // ── Deactivate: never deleted, never a default, hidden unless asked for ──
        var deactivated = await Save(gst18Id, Code("GST18", 18m, "SALES", isDefault: true, isActive: false));
        deactivated.B("isActive").Should().BeFalse();
        deactivated.B("isDefault").Should().BeFalse("an inactive code is never the default");
        (await DefaultsAsync()).Should().BeEquivalentTo(["GST17", "PGST17"], "deactivating with isDefault=true did not take GST17's place");
        (await CodesAsync("SALES")).Should().NotContain("GST18");
        var withInactive = await _k.Ok(_k.Get($"{TaxCodes}?side=SALES&includeInactive=true"), "list incl. inactive");
        withInactive.Items().Should().ContainSingle(c => c.S("code") == "GST18").Which.B("isActive").Should().BeFalse();
        var dup = await _k.Post(TaxCodes, Code("GST18", 18m, "SALES"));
        dup.Status.Should().Be(HttpStatusCode.Conflict, "a deactivated code still holds its name");

        (await _k.Put($"{TaxCodes}/{Guid.NewGuid()}", Code("GHOST", 1m, "SALES"))).Status.Should().Be(HttpStatusCode.NotFound);

        // ── Sale-order lines: the code decides the rate, and keeps it as a snapshot ──
        var soUuid = await _k.CreateSaleOrderAsync(customer, pkr,
            SapKit.Line(p1, 2m, taxCode: gst17Id, taxPercent: 99m),       // the 99% a caller sends is ignored
            SapKit.Line(p2, 1m, taxPercent: 5m),                           // no code: the percent as entered (legacy)
            SapKit.Line(p1, 1m, taxCode: gst17Id, discount: 10m));
        var so = await _k.GetSaleOrderAsync(soUuid);
        var lines = so.A("lines");
        var coded = lines.Where(l => l.S("taxCode") == "GST17").ToList();
        coded.Should().HaveCount(2);
        coded.Should().OnlyContain(l => l.D("taxPercent") == 17m && l.G("taxCodeUuid") == gst17Id);
        coded.Single(l => l.D("discountPercent") == 0m).D("lineTotal").Should().Be(234.00m, "2 × 100 × 1.17");
        coded.Single(l => l.D("discountPercent") == 10m).D("lineTotal").Should().Be(105.30m, "100 × 0.9 × 1.17");
        var legacy = lines.Single(l => l.S("taxCode") is null);
        legacy.NG("taxCodeUuid").Should().BeNull();
        legacy.D("taxPercent").Should().Be(5m);
        legacy.D("lineTotal").Should().Be(42.00m);
        so.D("subtotal").Should().Be(340m);
        so.D("discountAmount").Should().Be(10m);
        so.D("taxAmount").Should().Be(51.30m, "34 + 15.30 + 2");
        so.D("grandTotal").Should().Be(381.30m);

        foreach (var (line, why) in new (object, string)[]
                 {
                     (SapKit.Line(p1, 1m, taxCode: pgst17.G("uuid")), "a PURCHASE-only code on a sale"),
                     (SapKit.Line(p1, 1m, taxCode: gst18Id), "an inactive code"),
                     (SapKit.Line(p1, 1m, taxCode: Guid.NewGuid()), "a code that does not exist"),
                     (SapKit.Line(p1, 1m, taxPercent: 101m), "a raw percent above 100"),
                     (SapKit.Line(p1, 1m, taxPercent: 17.125m), "a raw percent with three decimals (the column keeps two)"),
                     (SapKit.Line(p1, 1m, taxCode: gst17Id, discount: 100.5m), "a discount above 100"),
                     (SapKit.Line(p1, 1m, taxCode: gst17Id, discount: 2.505m), "a discount with three decimals"),
                 })
        {
            var refused = await _k.TryCreateSaleOrderAsync(customer, pkr, line);
            refused.Status.Should().Be(HttpStatusCode.BadRequest, $"{why} is refused — {refused}");
        }

        // A later change of the code's rate does not touch the line already raised; a rebuilt draft takes the new rate.
        var rateChange = await _k.Put($"{TaxCodes}/{gst17Id}", Code("GST17", 18m, "SALES", isDefault: true, name: "GST 17%"));
        rateChange.Status.Should().Be(HttpStatusCode.OK, rateChange.ToString());
        rateChange.Message.Should().Contain("17").And.Contain("18");
        (await _k.GetSaleOrderAsync(soUuid)).A("lines").Where(l => l.S("taxCode") == "GST17")
            .Should().OnlyContain(l => l.D("taxPercent") == 17m, "a document keeps the rate it was raised with");

        object[] sameLines =
        [
            SapKit.Line(p1, 2m, taxCode: gst17Id), SapKit.Line(p2, 1m, taxPercent: 5m), SapKit.Line(p1, 1m, taxCode: gst17Id, discount: 10m)
        ];
        await _k.Ok(_k.Put($"/api/sale-orders/{soUuid}", new { CurrencyId = pkr, DeliveryMode = "SELF_PICKUP", Lines = sameLines }), "update draft SO");
        (await _k.GetSaleOrderAsync(soUuid)).A("lines").Where(l => l.S("taxCode") == "GST17")
            .Should().OnlyContain(l => l.D("taxPercent") == 18m, "a draft's lines are rebuilt, and pick up the code's current rate");

        await Save(gst17Id, Code("GST17", 17m, "SALES", isDefault: true, name: "GST 17%"));
        await _k.Ok(_k.Put($"/api/sale-orders/{soUuid}", new { CurrencyId = pkr, DeliveryMode = "SELF_PICKUP", Lines = sameLines }), "update draft SO back");
        (await _k.GetSaleOrderAsync(soUuid)).D("grandTotal").Should().Be(381.30m);

        // A code printed on documents can no longer be renamed; an unused one still can.
        (await _k.Put($"{TaxCodes}/{gst17Id}", Code("GST17X", 17m, "SALES", isDefault: true))).Status
            .Should().Be(HttpStatusCode.Conflict, "GST17 is on sale order lines");
        (await Save(gst18Id, Code("GST18OLD", 18m, "SALES", isActive: false))).S("code").Should().Be("GST18OLD");

        // ── Codes from the rates already in use ──────────────────────────────────
        var fromRates = await _k.Ok(_k.Post($"{TaxCodes}/from-rates-in-use"), "codes from rates in use");
        var created = fromRates.A("created");
        created.Should().ContainSingle($"only 5% has no active SALES/BOTH code — {J.Short(fromRates)}");
        created[0].S("code").Should().Be("TAX5");
        created[0].D("ratePercent").Should().Be(5m);
        created[0].S("usage").Should().Be("SALES");
        created[0].B("isDefault").Should().BeFalse();
        created[0].B("isActive").Should().BeTrue();
        fromRates.A("skippedRates").Select(r => r.GetDecimal()).Should().BeEquivalentTo([17m], "GST17 already covers 17%");

        var again = await _k.Ok(_k.Post($"{TaxCodes}/from-rates-in-use"), "codes from rates in use, again");
        again.A("created").Should().BeEmpty("running it twice creates nothing new");
        again.A("skippedRates").Select(r => r.GetDecimal()).Should().BeEquivalentTo([5m, 17m]);

        (await CodesAsync("SALES")).Should().Contain("TAX5");
        (await _k.GetSaleOrderAsync(soUuid)).A("lines").Single(l => l.D("taxPercent") == 5m).S("taxCode")
            .Should().BeNull("existing lines are not re-coded behind the user's back");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Exchange rates
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Exchange_rates_are_validated_and_quoted_by_date_inversely_and_per_currency()
    {
        await _k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        await _k.EnsureCurrencyAsync("USD", "US Dollar", "$");
        await _k.EnsureCurrencyAsync("EUR", "Euro", "€");

        var jan = await _k.Ok(_k.Post(Rates, Rate("usd", "pkr", 278.5m, "2026-01-01")), "USD→PKR January");
        jan.S("fromCurrencyCode").Should().Be("USD", "codes are stored upper-case");
        jan.S("toCurrencyCode").Should().Be("PKR");
        jan.D("rate").Should().Be(278.5m);
        jan.S("effectiveDate").Should().Be("2026-01-01");
        jan.S("source").Should().Be("MANUAL");
        var june = await _k.Ok(_k.Post(Rates, Rate("USD", "PKR", 280.25m, "2026-06-01")), "USD→PKR June");
        var juneId = june.G("uuid");

        // ── Validation ────────────────────────────────────────────────────────────
        (await _k.Post(Rates, Rate("USD", "PKR", 281m, "2026-06-01"))).Status.Should().Be(HttpStatusCode.Conflict, "one live rate per pair per day");
        foreach (var (body, why) in new (object, string)[]
                 {
                     (Rate("PKR", "PKR", 1m, "2026-01-01"), "the same currency on both sides"),
                     (Rate("USD", "PKR", 0m, "2026-02-01"), "a zero rate"),
                     (Rate("USD", "PKR", -1m, "2026-02-01"), "a negative rate"),
                     (Rate("USD", "PKR", 1.123456789m, "2026-02-01"), "nine decimals"),
                     (Rate("XYZ", "PKR", 2m, "2026-02-01"), "a currency not in the catalog"),
                     (Rate("USD", "PKR", 2m, null), "no effective date"),
                     (Rate("USD", "PKR", 2m, "2026-13-01"), "an impossible date"),
                 })
        {
            var refused = await _k.Post(Rates, body);
            refused.Status.Should().Be(HttpStatusCode.BadRequest, $"{why} is refused — {refused}");
        }

        // ── List, newest first ──────────────────────────────────────────────────────
        var listed = await _k.Ok(_k.Get($"{Rates}?from=USD&to=PKR"), "list USD→PKR");
        listed.Items().Select(r => r.S("effectiveDate")).Should().Equal(["2026-06-01", "2026-01-01"]);

        // ── Quotes ──────────────────────────────────────────────────────────────────
        (await Quote("USD", "PKR", "2026-03-15")).Should().Be((278.5m, "2026-01-01", false), "the latest rate on or before the date");
        (await Quote("USD", "PKR", "2026-06-01")).Should().Be((280.25m, "2026-06-01", false), "a rate applies from its own day");
        (await Quote("USD", "PKR", "2026-12-31")).Should().Be((280.25m, "2026-06-01", false));
        (await QuoteOrNull("USD", "PKR", "2025-12-31")).Should().BeNull("nothing is on file before the first rate");

        var inverse = Math.Round(1m / 278.5m, 8, MidpointRounding.AwayFromZero);
        (await Quote("PKR", "USD", "2026-03-15")).Should().Be((inverse, "2026-01-01", true), "the reciprocal of the opposite pair");
        (await Quote("pkr", "pkr", "2026-03-15")).Should().Be((1m, "2026-03-15", false), "the same currency is always 1");
        (await QuoteOrNull("EUR", "PKR", "2026-03-15")).Should().BeNull("no EUR rate, and no triangulation through USD");
        (await _k.Get($"{Rates}/quote?to=PKR&date=2026-03-15")).Status.Should().Be(HttpStatusCode.BadRequest);

        // ── Change and delete ─────────────────────────────────────────────────────
        await _k.Ok(_k.Put($"{Rates}/{juneId}", Rate("USD", "PKR", 281m, "2026-06-01")), "change June");
        (await Quote("USD", "PKR", "2026-12-31")).Rate.Should().Be(281m);
        (await _k.Put($"{Rates}/{juneId}", Rate("USD", "PKR", 281m, "2026-01-01"))).Status
            .Should().Be(HttpStatusCode.Conflict, "moving it onto January's day clashes with the January rate");

        await _k.Ok(_k.Delete($"{Rates}/{juneId}"), "delete June");
        (await Quote("USD", "PKR", "2026-12-31")).Should().Be((278.5m, "2026-01-01", false), "deleted: back to the previous rate");
        (await _k.Ok(_k.Get($"{Rates}?from=USD&to=PKR"), "list")).Items().Should().ContainSingle("a deleted rate is not listed");
        (await _k.Delete($"{Rates}/{juneId}")).Status.Should().Be(HttpStatusCode.NotFound, "already deleted");

        var reAdded = await _k.Post(Rates, Rate("USD", "PKR", 282m, "2026-06-01"));
        reAdded.Status.Should().Be(HttpStatusCode.OK, $"the deleted rate no longer holds its day — {reAdded}");
        (await Quote("USD", "PKR", "2026-12-31")).Rate.Should().Be(282m);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Permissions
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Only_finance_setup_managers_change_codes_and_rates_but_anyone_signed_in_reads_them()
    {
        // GBP, not EUR: the exchange-rate fact asserts that EUR has no rate at all.
        await _k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        await _k.EnsureCurrencyAsync("GBP", "Pound Sterling", "£");

        var auditor = await _k.LoginAsNewUserAsync((int)EnumRole.Auditor, "auditor");
        var requester = await _k.LoginAsNewUserAsync((int)EnumRole.Requester, "requester");
        var financeManager = await _k.LoginAsNewUserAsync((int)EnumRole.FinanceManager, "finmgr");

        foreach (var (client, who) in new[] { (auditor, "an auditor"), (requester, "a requester") })
        {
            (await _k.Get(TaxCodes, client)).Status.Should().Be(HttpStatusCode.OK, $"{who} may read tax codes (they feed pickers)");
            (await _k.Get(Rates, client)).Status.Should().Be(HttpStatusCode.OK, $"{who} may read rates");
            (await _k.Get($"{Rates}/quote?from=GBP&to=PKR&date=2026-03-01", client)).Status.Should().Be(HttpStatusCode.OK);

            (await _k.Post(TaxCodes, Code("NOPE1", 1m, "SALES"), client)).Status.Should().Be(HttpStatusCode.Forbidden, $"{who} lacks FINANCE_SETUP_MANAGE");
            (await _k.Post($"{TaxCodes}/from-rates-in-use", null, client)).Status.Should().Be(HttpStatusCode.Forbidden);
            (await _k.Post(Rates, Rate("GBP", "PKR", 350m, "2026-02-01"), client)).Status.Should().Be(HttpStatusCode.Forbidden);
        }

        using (var anonymous = _k.F.CreateAnonymousClient())
            (await _k.Get(TaxCodes, anonymous)).Status.Should().Be(HttpStatusCode.Unauthorized, "reads still need a sign-in");

        var made = await _k.Post(TaxCodes, Code("FMTAX12", 12.5m, "PURCHASE"), financeManager);
        made.Status.Should().Be(HttpStatusCode.OK, $"a Finance Manager holds FINANCE_SETUP_MANAGE — {made}");
        var rate = await _k.Post(Rates, Rate("GBP", "PKR", 350m, "2026-02-01"), financeManager);
        rate.Status.Should().Be(HttpStatusCode.OK, rate.ToString());

        (await _k.Put($"{TaxCodes}/{made.Result.G("uuid")}", Code("FMTAX12", 13m, "PURCHASE"), auditor)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await _k.Delete($"{Rates}/{rate.Result.G("uuid")}", auditor)).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Organization base currency (WP-A fix: an edit no longer wipes it)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task An_organization_edit_that_does_not_mention_the_base_currency_keeps_it()
    {
        var pkr = await _k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        await _k.SetBaseCurrencyAsync(pkr);

        var orgUrl = $"/api/system/organizations/{_k.F.OrganizationId}";
        var org = await _k.Ok(_k.Get(orgUrl), "read org");
        org.G("baseCurrency").Should().Be(pkr);

        await _k.Ok(_k.Put(orgUrl, new { orgName = org.S("orgName"), contactEmail = org.S("contactEmail"), country = org.S("country") }), "profile-only edit");
        (await _k.Ok(_k.Get(orgUrl), "read org")).G("baseCurrency").Should().Be(pkr, "a client that does not send the base currency must not wipe it");

        (await _k.Put(orgUrl, new { orgName = org.S("orgName"), baseCurrency = pkr, clearBaseCurrency = true })).Status
            .Should().Be(HttpStatusCode.BadRequest, "choose or clear, not both");
        (await _k.Put(orgUrl, new { orgName = org.S("orgName"), baseCurrency = Guid.NewGuid() })).Status
            .Should().Be(HttpStatusCode.BadRequest, "a base currency must be a catalog currency with a code");

        await _k.Ok(_k.Put(orgUrl, new { orgName = org.S("orgName"), clearBaseCurrency = true }), "clear base currency");
        (await _k.Ok(_k.Get(orgUrl), "read org")).NG("baseCurrency").Should().BeNull();

        await _k.SetBaseCurrencyAsync(pkr);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Per organization (S-2)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Tax_codes_and_rates_belong_to_one_organization()
    {
        // AED, so no other fact's assertions about USD/EUR rates are touched.
        await _k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        await _k.EnsureCurrencyAsync("AED", "UAE Dirham", "AED");

        var mine = (await _k.Ok(_k.Post(TaxCodes, Code("ISO9", 9m, "BOTH")), "org 1 code")).G("uuid");
        var myRate = (await _k.Ok(_k.Post(Rates, Rate("AED", "PKR", 75m, "2026-02-02")), "org 1 rate")).G("uuid");

        // A second organization with its own admin (the invitation is accepted by hand: activate + password).
        var adminEmail = $"org2-{Guid.NewGuid():N}@sap-e2e.test";
        var org2 = await _k.Ok(_k.Post("/api/system/organizations", new
        {
            OrgCode = $"S2{Guid.NewGuid():N}"[..10].ToUpperInvariant(), OrgName = $"{_k.Marker} Second Org", Plan = "ENTERPRISE",
            AdminFirstName = "Second", AdminLastName = "Admin", AdminEmail = adminEmail
        }), "create a second organization");
        org2.G("organizationId").Should().NotBe(_k.F.OrganizationId);
        await _k.F.SetPasswordAsync(adminEmail, "Org2@12345!");
        await _k.F.ExecuteAsync("UPDATE auth.UserAccounts SET IsActive = 1 WHERE Email = @e", ("@e", adminEmail));
        var other = _k.F.CreateBearerClient(await _k.F.LoginAsync(adminEmail, "Org2@12345!"));

        (await _k.Ok(_k.Get(TaxCodes, other), "org 2 codes")).Items().Should().BeEmpty("org 1's codes are not org 2's");
        (await _k.Ok(_k.Get(Rates, other), "org 2 rates")).Items().Should().BeEmpty();
        var quote = await _k.Ok(_k.Get($"{Rates}/quote?from=AED&to=PKR&date=2026-03-01", other), "org 2 quote");
        quote.ValueKind.Should().Be(JsonValueKind.Null, "org 1's rate does not convert org 2's documents");

        (await _k.Put($"{TaxCodes}/{mine}", Code("ISO9", 1m, "BOTH"), other)).Status.Should().Be(HttpStatusCode.NotFound);
        (await _k.Put($"{Rates}/{myRate}", Rate("AED", "PKR", 1m, "2026-02-02"), other)).Status.Should().Be(HttpStatusCode.NotFound);
        (await _k.Delete($"{Rates}/{myRate}", other)).Status.Should().Be(HttpStatusCode.NotFound);

        var theirs = await _k.Post(TaxCodes, Code("ISO9", 12m, "BOTH"), other);
        theirs.Status.Should().Be(HttpStatusCode.OK, $"the same code in another organization is no clash — {theirs}");
        (await _k.Post(Rates, Rate("AED", "PKR", 80m, "2026-02-02"), other)).Status.Should().Be(HttpStatusCode.OK, "nor the same pair and day");

        (await Quote("AED", "PKR", "2026-03-01")).Rate.Should().Be(75m, "org 1 still converts at its own rate");
        (await _k.Ok(_k.Get($"{Rates}/quote?from=AED&to=PKR&date=2026-03-01", other), "org 2 quote")).D("rate").Should().Be(80m);
        (await ListAsync()).Where(c => c.S("code") == "ISO9").Should().ContainSingle("the super admin bypasses the tenant filter, but sees only its own codes")
            .Which.D("ratePercent").Should().Be(9m);
        (await _k.Ok(_k.Get($"{Rates}?from=AED&to=PKR"), "org 1 rates")).Items().Should().ContainSingle().Which.D("rate").Should().Be(75m);

        // A document in org 2 cannot use org 1's code.
        var vendor2 = (await _k.Ok(_k.Post("/api/suppliers", new { SupplierName = $"{_k.Marker} Org2 Vendor", SupplierCode = $"O2{Guid.NewGuid():N}"[..10] }, other), "org 2 vendor")).GetGuid();
        var foreignCode = await _k.Post("/api/finance/invoices", new
        {
            SupplierInvoiceNo = $"O2-{Guid.NewGuid():N}"[..16], SupplierId = vendor2, InvoiceDate = DateTime.UtcNow.Date, ReceivedDate = DateTime.UtcNow.Date,
            DueDate = DateTime.UtcNow.Date.AddDays(30), Currency = "PKR", Subtotal = 100m, TaxAmount = 0m, TaxCodeUuid = mine
        }, other);
        foreignCode.Status.Should().Be(HttpStatusCode.BadRequest, $"org 1's tax code does not exist in org 2 — {foreignCode}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static object Code(string code, decimal rate, string usage, bool isDefault = false, bool isActive = true, string? name = null) => new
    {
        code, name = name ?? $"{code} tax", description = (string?)null, ratePercent = rate, usage, isDefault, isActive
    };

    private static object Rate(string from, string to, decimal rate, string? date) => new
    {
        fromCurrencyCode = from, toCurrencyCode = to, rate, effectiveDate = date, notes = (string?)null
    };

    private async Task<JsonElement> Save(Guid uuid, object body) => await _k.Ok(_k.Put($"{TaxCodes}/{uuid}", body), $"update tax code {uuid}");

    private async Task<List<JsonElement>> ListAsync(string? side = null) =>
        (await _k.Ok(_k.Get(side is null ? TaxCodes : $"{TaxCodes}?side={side}"), "list tax codes")).Items();

    private static readonly HashSet<string> Ours = ["GST17", "PGST17", "EXEMPT", "GST18", "GST18OLD", "TAX5"];

    /// <summary>This test's codes on a side, in the order the API lists them (another fact may have made others).</summary>
    private async Task<List<string>> CodesAsync(string? side) =>
        (await ListAsync(side)).Select(c => c.S("code")!).Where(Ours.Contains).ToList();

    private async Task<List<string>> DefaultsAsync() =>
        (await ListAsync()).Where(c => c.B("isDefault")).Select(c => c.S("code")!).Where(Ours.Contains).ToList();

    private async Task<(decimal Rate, string EffectiveDate, bool Inverted)> Quote(string from, string to, string date) =>
        (await QuoteOrNull(from, to, date)) ?? throw new InvalidOperationException($"No {from}->{to} quote for {date}");

    private async Task<(decimal Rate, string EffectiveDate, bool Inverted)?> QuoteOrNull(string from, string to, string date)
    {
        var result = await _k.Ok(_k.Get($"{Rates}/quote?from={from}&to={to}&date={date}"), $"quote {from}->{to} {date}");
        if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        result.S("fromCurrencyCode").Should().Be(from.ToUpperInvariant());
        result.S("toCurrencyCode").Should().Be(to.ToUpperInvariant());
        return (result.D("rate"), result.S("effectiveDate")!, result.B("inverted"));
    }
}
