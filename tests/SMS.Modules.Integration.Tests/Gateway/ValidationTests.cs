using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway.Validation;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Gateway;

/// <summary>Every rule of every kind, passing and failing. Runs the validator the gateway and executor use.</summary>
public class ValidationTests : IAsyncLifetime
{
    private readonly SyncHarness _h = new();

    public async Task InitializeAsync() => await _h.ConnectAsync();
    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<PayloadValidationResult> Validate(SyncKind kind, object payload, EntityMap? map = null) =>
        _h.Scoped(async sp =>
        {
            var db         = sp.GetRequiredService<IntegrationDbContext>();
            var connection = await db.Connections.SingleAsync();
            var settings   = await db.Settings.SingleAsync();
            map ??= new EntityMap { ConnectionId = connection.Id, Kind = kind, ExternalId = "NEW", SourceSystem = QuickBooksSourceSystems.Scm };
            return await sp.GetRequiredService<IPayloadValidator>().ValidateAsync(kind, payload, map, connection, settings);
        });

    private static void ShouldFailWith(PayloadValidationResult r, string code, string? field = null)
    {
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Code == code && (field == null || e.Field == field),
            $"expected {code}; got {string.Join(", ", r.Errors.Select(e => e.Code + "@" + e.Field))}");
        r.Errors.Where(e => e.Code == code).Should().OnlyContain(e => e.Message.Length > 20, "messages must say how to fix it");
    }

    private async Task SeedMapAsync(SyncKind kind, string externalId, string? remoteName, string label = "x", bool withPayload = true)
    {
        await using var db = _h.DbAs(_h.OrgId);
        var connection = await db.Connections.SingleAsync();
        db.EntityMaps.Add(new EntityMap
        {
            ConnectionId = connection.Id, Kind = kind, ExternalId = externalId, SourceSystem = QuickBooksSourceSystems.Scm,
            RemoteName = remoteName, RemoteId = remoteName is null ? null : "R-" + externalId, DisplayLabel = label,
            PayloadJson = withPayload ? "{}" : null
        });
        await db.SaveChangesAsync();
    }

    // ── Customer / Vendor ────────────────────────────────────────────────────

    [Fact]
    public async Task Valid_customer_and_vendor_pass()
    {
        (await Validate(SyncKind.Customer, TestPayloads.Customer())).IsValid.Should().BeTrue();
        (await Validate(SyncKind.Vendor, TestPayloads.Vendor())).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Customer_without_a_name_is_refused(string name)
    {
        var p = TestPayloads.Customer(name: name);
        ShouldFailWith(await Validate(SyncKind.Customer, p), "DISPLAY_NAME_REQUIRED", "displayName");
    }

    [Fact]
    public async Task Name_over_100_characters_is_refused_never_truncated()
    {
        var name = new string('A', 101);
        var r = await Validate(SyncKind.Customer, TestPayloads.Customer(name: name));

        ShouldFailWith(r, "NAME_TOO_LONG", "displayName");
        r.Errors.Single(e => e.Code == "NAME_TOO_LONG").Message.Should().Contain("101").And.Contain("100").And.Contain("never cut off");

        (await Validate(SyncKind.Customer, TestPayloads.Customer(name: new string('A', 100)))).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Name_that_only_overflows_with_the_D2_suffix_is_refused_and_says_why()
    {
        var name = new string('B', 95);
        await SeedMapAsync(SyncKind.Customer, "OTHER", remoteName: name);

        var r = await Validate(SyncKind.Customer, TestPayloads.Customer(name: name, code: "C00001"));

        ShouldFailWith(r, "NAME_TOO_LONG");
        r.Errors.Single(e => e.Code == "NAME_TOO_LONG").Message.Should().Contain(" (C00001)");
    }

    [Fact]
    public async Task Colon_in_a_name_is_refused()
    {
        ShouldFailWith(await Validate(SyncKind.Customer, TestPayloads.Customer(name: "Acme: Lahore Branch")), "NAME_HAS_COLON");
        ShouldFailWith(await Validate(SyncKind.Vendor, TestPayloads.Vendor(name: "Karachi: Supplies")), "NAME_HAS_COLON");
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    [InlineData("two@@example.com")]
    public async Task Invalid_email_is_refused(string email)
    {
        ShouldFailWith(await Validate(SyncKind.Customer, TestPayloads.Customer(email: email)), "EMAIL_INVALID", "email");
    }

    [Fact]
    public async Task Email_over_100_characters_is_refused_and_a_missing_email_is_fine()
    {
        var longEmail = new string('a', 95) + "@x.com"; // 101
        ShouldFailWith(await Validate(SyncKind.Customer, TestPayloads.Customer(email: longEmail)), "EMAIL_INVALID");
        (await Validate(SyncKind.Customer, TestPayloads.Customer(email: null))).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Foreign_currency_party_is_refused_home_currency_in_any_case_passes()
    {
        var usd = TestPayloads.Customer();
        usd.CurrencyCode = "USD";
        var r = await Validate(SyncKind.Customer, usd);
        ShouldFailWith(r, "CURRENCY_NOT_HOME", "currencyCode");
        r.Errors[0].Message.Should().Contain("USD").And.Contain("PKR");

        var pkr = TestPayloads.Customer();
        pkr.CurrencyCode = "pkr";
        (await Validate(SyncKind.Customer, pkr)).IsValid.Should().BeTrue();

        var none = TestPayloads.Vendor();
        none.CurrencyCode = null;
        (await Validate(SyncKind.Vendor, none)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Home_currency_falls_back_to_the_cached_preferences_when_the_connection_has_none()
    {
        await using (var db = _h.DbAs(_h.OrgId))
        {
            (await db.Connections.SingleAsync()).HomeCurrencyCode = null;
            await db.SaveChangesAsync();
        }

        var usd = TestPayloads.Customer();
        usd.CurrencyCode = "USD";
        (await Validate(SyncKind.Customer, usd)).IsValid.Should().BeTrue("with no home currency known there is nothing to compare");

        _h.Reference.Data = new RemoteReferenceData { Preferences = new RemotePreferences("PKR", false, true, true) };
        ShouldFailWith(await Validate(SyncKind.Customer, usd), "CURRENCY_NOT_HOME");
    }

    [Fact]
    public async Task Unmapped_payment_term_is_a_warning_not_an_error()
    {
        var p = TestPayloads.Customer();
        p.PaymentTermExternalId = "NET30";

        var r = await Validate(SyncKind.Customer, p);
        r.IsValid.Should().BeTrue();
        r.Warnings.Should().ContainSingle(w => w.Contains("NET30") && w.Contains("Mappings"));

        await using (var db = _h.DbAs(_h.OrgId))
        {
            db.PaymentTermMappings.Add(new PaymentTermMapping { ConnectionId = (await db.Connections.SingleAsync()).Id, PaymentTermExternalId = "NET30", QboTermId = "3" });
            await db.SaveChangesAsync();
        }
        (await Validate(SyncKind.Customer, p)).Warnings.Should().BeEmpty();
    }

    // ── Item ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Valid_item_passes()
    {
        (await Validate(SyncKind.Item, TestPayloads.Item())).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Item_name_rules()
    {
        ShouldFailWith(await Validate(SyncKind.Item, TestPayloads.Item(name: " ")), "NAME_REQUIRED", "name");
        ShouldFailWith(await Validate(SyncKind.Item, TestPayloads.Item(name: "Bolt: M8")), "NAME_HAS_COLON");

        // "Name - Variant" is what must fit.
        ShouldFailWith(await Validate(SyncKind.Item, TestPayloads.Item(name: new string('N', 60), variant: new string('V', 40))), "NAME_TOO_LONG");
        (await Validate(SyncKind.Item, TestPayloads.Item(name: new string('N', 57), variant: new string('V', 40)))).IsValid.Should().BeTrue();
        ShouldFailWith(await Validate(SyncKind.Item, TestPayloads.Item(name: "Bolt", variant: "M8:Steel")), "NAME_HAS_COLON");
    }

    [Fact]
    public async Task Item_sku_over_100_is_refused()
    {
        ShouldFailWith(await Validate(SyncKind.Item, TestPayloads.Item(sku: new string('S', 101))), "SKU_TOO_LONG", "sku");
        (await Validate(SyncKind.Item, TestPayloads.Item(sku: new string('S', 100)))).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Sold_or_purchased_items_need_their_default_accounts()
    {
        await _h.UpdateSettingsAsync(s => { s.DefaultIncomeAccountId = null; s.DefaultExpenseAccountId = null; });

        var sold = TestPayloads.Item();
        sold.IsPurchased = false;
        var r = await Validate(SyncKind.Item, sold);
        ShouldFailWith(r, "SETTINGS_ACCOUNT_MISSING", "settings.defaultIncomeAccountId");
        r.Errors.Should().HaveCount(1);
        r.Errors[0].Message.Should().Contain("Mappings");

        var bought = TestPayloads.Item();
        bought.IsSold = false;
        ShouldFailWith(await Validate(SyncKind.Item, bought), "SETTINGS_ACCOUNT_MISSING", "settings.defaultExpenseAccountId");

        var neither = TestPayloads.Item();
        neither.IsSold = false;
        neither.IsPurchased = false;
        (await Validate(SyncKind.Item, neither)).IsValid.Should().BeTrue();
    }

    // ── Sales invoice ────────────────────────────────────────────────────────

    [Fact]
    public async Task Valid_invoice_passes_including_line_and_header_discounts()
    {
        var p = TestPayloads.Invoice(headerDiscount: 10m, lines:
        [
            new TestPayloads.Line("I-1", 3, 19.99m, Discount: 10m),
            new TestPayloads.Line("I-2", 1, 250m, Tax: 0m)
        ]);
        (await Validate(SyncKind.SalesInvoice, p)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Credit_note_is_refused_as_unsupported()
    {
        var p = TestPayloads.Invoice();
        p.Status = SalesInvoicePayloadStatus.CreditNote;
        var r = await Validate(SyncKind.SalesInvoice, p);
        ShouldFailWith(r, "CREDIT_NOTE_UNSUPPORTED", "status");
        r.Errors.Should().HaveCount(1);
    }

    [Fact]
    public async Task Invoice_doc_number_rules()
    {
        ShouldFailWith(await Validate(SyncKind.SalesInvoice, TestPayloads.Invoice(doc: "")), "DOCNUMBER_REQUIRED", "docNumber");

        var r = await Validate(SyncKind.SalesInvoice, TestPayloads.Invoice(doc: new string('9', 22)));
        ShouldFailWith(r, "DOCNUMBER_TOO_LONG", "docNumber");
        r.Errors[0].Message.Should().Contain("22").And.Contain("21");

        (await Validate(SyncKind.SalesInvoice, TestPayloads.Invoice(doc: new string('9', 21)))).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Invoice_needs_a_customer_and_lines()
    {
        ShouldFailWith(await Validate(SyncKind.SalesInvoice, TestPayloads.Invoice(customer: " ")), "CUSTOMER_REQUIRED");

        var empty = TestPayloads.Invoice();
        empty.Lines.Clear();
        ShouldFailWith(await Validate(SyncKind.SalesInvoice, empty), "LINES_REQUIRED", "lines");
    }

    [Fact]
    public async Task Invoice_line_rules()
    {
        var p = TestPayloads.Invoice(lines:
        [
            new TestPayloads.Line("", 1, 10m),
            new TestPayloads.Line("I-2", 0, 10m),
            new TestPayloads.Line("I-3", 1, -5m),
            new TestPayloads.Line("I-4", 1, 10m, Discount: 101m)
        ]);

        var r = await Validate(SyncKind.SalesInvoice, p);
        ShouldFailWith(r, "LINE_ITEM_REQUIRED", "lines[0].itemExternalId");
        ShouldFailWith(r, "LINE_QUANTITY_INVALID", "lines[1].quantity");
        ShouldFailWith(r, "LINE_PRICE_INVALID", "lines[2].unitPrice");
        ShouldFailWith(r, "LINE_DISCOUNT_INVALID", "lines[3].discountPercent");
        r.Errors.Single(e => e.Code == "LINE_QUANTITY_INVALID").Message.Should().StartWith("Line 2");
    }

    [Fact]
    public async Task Unmapped_tax_rates_are_refused_and_listed()
    {
        var p = TestPayloads.Invoice(lines:
        [
            new TestPayloads.Line("I-1", 1, 100m, Tax: 17m),
            new TestPayloads.Line("I-2", 1, 100m, Tax: 5m),
            new TestPayloads.Line("I-3", 1, 100m, Tax: 12.5m)
        ]);

        var r = await Validate(SyncKind.SalesInvoice, p);
        ShouldFailWith(r, "TAX_UNMAPPED");
        var message = r.Errors.Single(e => e.Code == "TAX_UNMAPPED").Message;
        message.Should().Contain("5%").And.Contain("12.5%").And.NotContain("17%");
    }

    [Fact]
    public async Task Tax_rates_are_matched_at_the_four_places_the_mapping_screen_shows()
    {
        await using (var db = _h.DbAs(_h.OrgId))
        {
            db.TaxCodeMappings.Add(new TaxCodeMapping { ConnectionId = (await db.Connections.SingleAsync()).Id, TaxPercent = 12.3457m, QboTaxCodeId = "TAX-ODD" });
            await db.SaveChangesAsync();
        }
        var p = TestPayloads.Invoice(lines: [new TestPayloads.Line("I-1", 1, 100m, Tax: 12.34567m)]);

        (await Validate(SyncKind.SalesInvoice, p)).IsValid.Should().BeTrue();

        var built = await _h.Scoped(async sp =>
        {
            var db = sp.GetRequiredService<IntegrationDbContext>();
            var connection = await db.Connections.SingleAsync();
            var map = new EntityMap { ConnectionId = connection.Id, Kind = SyncKind.SalesInvoice, ExternalId = "X" };
            return await sp.GetRequiredService<SMS.Modules.Integration.Core.Sync.IQboObjectBuilder>()
                .BuildAsync(map, p, connection, await db.Settings.SingleAsync(), dryRun: true);
        });
        ((RemoteInvoice)built.Entity).Lines.Single().TaxCodeId.Should().Be("TAX-ODD");
    }

    [Fact]
    public async Task Foreign_currency_invoice_is_refused()
    {
        var p = TestPayloads.Invoice();
        p.CurrencyCode = "EUR";
        ShouldFailWith(await Validate(SyncKind.SalesInvoice, p), "CURRENCY_NOT_HOME");
    }

    [Fact]
    public async Task Invoice_whose_lines_do_not_add_up_is_refused_with_both_figures()
    {
        var p = TestPayloads.Invoice();       // 2 × 100 + 17% = 234.00
        p.ExpectedTotal = 240m;

        var r = await Validate(SyncKind.SalesInvoice, p);
        ShouldFailWith(r, "TOTAL_MISMATCH", "expectedTotal");
        r.Errors[0].Message.Should().Contain("234.00").And.Contain("240.00");

        p.ExpectedTotal = 234.01m;            // within the 0.01 tolerance
        (await Validate(SyncKind.SalesInvoice, p)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Any_discount_needs_the_discount_account()
    {
        await _h.UpdateSettingsAsync(s => s.DiscountAccountId = null);

        var lineDiscount = TestPayloads.Invoice(lines: [new TestPayloads.Line("I-1", 1, 100m, Discount: 5m)]);
        ShouldFailWith(await Validate(SyncKind.SalesInvoice, lineDiscount), "SETTINGS_ACCOUNT_MISSING", "settings.discountAccountId");

        var headerDiscount = TestPayloads.Invoice(headerDiscount: 3m);
        ShouldFailWith(await Validate(SyncKind.SalesInvoice, headerDiscount), "SETTINGS_ACCOUNT_MISSING");

        (await Validate(SyncKind.SalesInvoice, TestPayloads.Invoice())).IsValid.Should().BeTrue("no discount, no account needed");
    }

    // ── Bill ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Valid_bill_passes_with_goods_freight_other_and_untaxed_lines()
    {
        (await Validate(SyncKind.Bill, TestPayloads.Bill())).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Bill_doc_number_over_21_is_refused_not_truncated()
    {
        var r = await Validate(SyncKind.Bill, TestPayloads.Bill(doc: "SUPPLIER-INVOICE-000123"));
        ShouldFailWith(r, "DOCNUMBER_TOO_LONG");
        r.Errors[0].Message.Should().Contain("never cut off");
        ShouldFailWith(await Validate(SyncKind.Bill, TestPayloads.Bill(doc: "")), "DOCNUMBER_REQUIRED");
    }

    [Fact]
    public async Task Bill_needs_a_vendor_lines_and_non_negative_amounts()
    {
        ShouldFailWith(await Validate(SyncKind.Bill, TestPayloads.Bill(vendor: "")), "VENDOR_REQUIRED");

        var empty = TestPayloads.Bill();
        empty.Lines.Clear();
        ShouldFailWith(await Validate(SyncKind.Bill, empty), "LINES_REQUIRED");

        var negative = TestPayloads.Bill(lines: [new BillLinePayload { Category = BillLineCategory.Other, Amount = -1m }]);
        ShouldFailWith(await Validate(SyncKind.Bill, negative), "LINE_AMOUNT_INVALID", "lines[0].amount");
    }

    [Fact]
    public async Task Freight_needs_the_freight_account_other_and_itemless_goods_need_the_expense_account()
    {
        await _h.UpdateSettingsAsync(s => { s.FreightExpenseAccountId = null; s.DefaultExpenseAccountId = null; });

        var freight = TestPayloads.Bill(lines: [new BillLinePayload { Category = BillLineCategory.Freight, Amount = 10m }]);
        ShouldFailWith(await Validate(SyncKind.Bill, freight), "SETTINGS_ACCOUNT_MISSING", "settings.freightExpenseAccountId");

        var other = TestPayloads.Bill(lines: [new BillLinePayload { Category = BillLineCategory.Other, Amount = 10m }]);
        ShouldFailWith(await Validate(SyncKind.Bill, other), "SETTINGS_ACCOUNT_MISSING", "settings.defaultExpenseAccountId");

        var itemless = TestPayloads.Bill(lines: [new BillLinePayload { Category = BillLineCategory.Goods, Amount = 10m }]);
        ShouldFailWith(await Validate(SyncKind.Bill, itemless), "SETTINGS_ACCOUNT_MISSING", "settings.defaultExpenseAccountId");

        var itemised = TestPayloads.Bill(lines: [new BillLinePayload { ItemExternalId = "I-1", Category = BillLineCategory.Goods, Quantity = 1, UnitPrice = 10m, Amount = 10m }]);
        (await Validate(SyncKind.Bill, itemised)).IsValid.Should().BeTrue("an item-based line posts to the item's accounts");
    }

    [Fact]
    public async Task Bill_line_tax_rates_must_be_mapped_but_no_rate_is_allowed()
    {
        var unmapped = TestPayloads.Bill(lines: [new BillLinePayload { Category = BillLineCategory.Other, Amount = 100m, TaxPercent = 8m }]);
        ShouldFailWith(await Validate(SyncKind.Bill, unmapped), "TAX_UNMAPPED");

        var noRate = TestPayloads.Bill(lines: [new BillLinePayload { Category = BillLineCategory.Other, Amount = 100m, TaxPercent = null }]);
        (await Validate(SyncKind.Bill, noRate)).IsValid.Should().BeTrue();

        await _h.UpdateSettingsAsync(s => s.DefaultPurchaseTaxCodeId = null);
        (await Validate(SyncKind.Bill, noRate)).IsValid.Should().BeTrue("no default purchase tax code means no tax code, not an error");
    }

    [Fact]
    public async Task Bill_total_and_currency_rules()
    {
        var p = TestPayloads.Bill();
        p.ExpectedTotal += 1m;
        ShouldFailWith(await Validate(SyncKind.Bill, p), "TOTAL_MISMATCH");

        var usd = TestPayloads.Bill();
        usd.CurrencyCode = "USD";
        ShouldFailWith(await Validate(SyncKind.Bill, usd), "CURRENCY_NOT_HOME");
    }
}
