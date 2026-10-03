using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Sync;

/// <summary>QboObjectBuilder: stored payload → QuickBooks DTO. D-2 names, D-4 tax codes, D-5 discounts, D-10, account defaults.</summary>
public class BuilderTests : IAsyncLifetime
{
    private readonly SyncHarness _h = new();
    private IntegrationConnection _connection = null!;

    public async Task InitializeAsync() => _connection = await _h.ConnectAsync();
    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<BuiltRemoteEntity> Build(SyncKind kind, object payload, string externalId = "NEW", bool dryRun = false) =>
        _h.Scoped(async sp =>
        {
            var db       = sp.GetRequiredService<IntegrationDbContext>();
            var settings = await db.Settings.SingleAsync();
            var map = await db.EntityMaps.FirstOrDefaultAsync(m => m.Kind == kind && m.ExternalId == externalId)
                      ?? new EntityMap { ConnectionId = _connection.Id, OrganizationId = _h.OrgId, Kind = kind, ExternalId = externalId, SourceSystem = QuickBooksSourceSystems.Scm };
            return await sp.GetRequiredService<IQboObjectBuilder>().BuildAsync(map, payload, _connection, settings, dryRun);
        });

    private async Task SeedMapAsync(SyncKind kind, string externalId, string label, string? remoteName = null, string? remoteId = null, bool withPayload = true)
    {
        await using var db = _h.DbAs(_h.OrgId);
        db.EntityMaps.Add(new EntityMap
        {
            ConnectionId = _connection.Id, Kind = kind, ExternalId = externalId, SourceSystem = QuickBooksSourceSystems.Scm,
            DisplayLabel = label, RemoteName = remoteName, RemoteId = remoteId ?? (remoteName is null ? null : "R-" + externalId),
            PayloadJson = withPayload ? "{}" : null
        });
        await db.SaveChangesAsync();
    }

    // ── D-2 names ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_customer_keeps_its_name_when_it_is_free()
    {
        var built = await Build(SyncKind.Customer, TestPayloads.Customer(name: "  Acme Traders  "));
        built.EffectiveName.Should().Be("Acme Traders");
        ((RemoteCustomer)built.Entity).DisplayName.Should().Be("Acme Traders");
        built.Lookup.Name.Should().Be("Acme Traders");
    }

    [Fact]
    public async Task Two_customers_with_the_same_name_the_second_gets_its_code()
    {
        await SeedMapAsync(SyncKind.Customer, "C-OTHER", "Acme Traders", remoteName: "acme traders");

        var built = await Build(SyncKind.Customer, TestPayloads.Customer(name: "Acme Traders", code: "C002"));
        built.EffectiveName.Should().Be("Acme Traders (C002)");
    }

    [Fact]
    public async Task Without_a_code_the_suffix_is_the_short_external_id()
    {
        await SeedMapAsync(SyncKind.Customer, "C-OTHER", "Acme Traders", remoteName: "Acme Traders");

        var built = await Build(SyncKind.Customer, TestPayloads.Customer(name: "Acme Traders", code: null), externalId: "3f2b9c1e-aaaa-bbbb");
        built.EffectiveName.Should().Be("Acme Traders (3f2b9c1e)");
    }

    [Fact]
    public async Task A_vendor_named_like_a_customer_gets_its_code_even_before_the_customer_is_pushed()
    {
        await SeedMapAsync(SyncKind.Customer, "BP-1", "Acme Traders");   // held, not in QuickBooks yet

        var vendor = await Build(SyncKind.Vendor, TestPayloads.Vendor(id: "BP-1", name: "Acme Traders", code: "S042"), externalId: "BP-1");
        vendor.EffectiveName.Should().Be("Acme Traders (S042)");

        // …and the customer itself keeps the plain name.
        var customer = await Build(SyncKind.Customer, TestPayloads.Customer(id: "BP-1", name: "Acme Traders"), externalId: "BP-1");
        customer.EffectiveName.Should().Be("Acme Traders");
    }

    [Fact]
    public async Task A_customer_named_like_a_vendor_already_in_QuickBooks_gets_its_code()
    {
        await SeedMapAsync(SyncKind.Vendor, "V-9", "Acme Traders", remoteName: "Acme Traders");

        var built = await Build(SyncKind.Customer, TestPayloads.Customer(name: "Acme Traders", code: "C001"));
        built.EffectiveName.Should().Be("Acme Traders (C001)");
    }

    [Fact]
    public async Task A_record_keeps_the_suffixed_name_it_already_has_even_after_the_clash_goes()
    {
        await SeedMapAsync(SyncKind.Customer, "C-1", "Acme Traders", remoteName: "Acme Traders (C001)");

        var built = await Build(SyncKind.Customer, TestPayloads.Customer(name: "Acme Traders", code: "C001"), externalId: "C-1");
        built.EffectiveName.Should().Be("Acme Traders (C001)", "renaming back and forth in QuickBooks helps nobody");
    }

    [Fact]
    public async Task Items_are_named_Name_dash_Variant_and_get_the_Sku_on_a_clash()
    {
        (await Build(SyncKind.Item, TestPayloads.Item(name: "T-Shirt", variant: "Red / L"))).EffectiveName.Should().Be("T-Shirt - Red / L");

        await SeedMapAsync(SyncKind.Item, "I-OTHER", "Widget", remoteName: "Widget");
        (await Build(SyncKind.Item, TestPayloads.Item(name: "Widget", sku: "W-2"))).EffectiveName.Should().Be("Widget (W-2)");
    }

    [Fact]
    public async Task The_D2_name_is_persisted_as_RemoteName_after_a_push_so_the_next_clash_sees_it()
    {
        await _h.UpdateSettingsAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await _h.Send(TestPayloads.Customer(id: "C-1", name: "Acme Traders", code: "C001"));
        await _h.Send(TestPayloads.Customer(id: "C-2", name: "ACME TRADERS", code: "C002"));
        await _h.Send(TestPayloads.Vendor(id: "C-1", name: "Acme Traders", code: "S001"));

        await _h.DrainAsync();

        (await _h.MapAsync(SyncKind.Customer, "C-1")).RemoteName.Should().Be("Acme Traders");
        (await _h.MapAsync(SyncKind.Customer, "C-2")).RemoteName.Should().Be("ACME TRADERS (C002)");
        (await _h.MapAsync(SyncKind.Vendor, "C-1")).RemoteName.Should().Be("Acme Traders (S001)");
        _h.Provider.Company.Select(r => r.Name).Should().BeEquivalentTo("Acme Traders", "ACME TRADERS (C002)", "Acme Traders (S001)");
    }

    // ── Parties ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Party_fields_terms_and_vendor_account_number_are_mapped()
    {
        await using (var db = _h.DbAs(_h.OrgId))
        {
            db.PaymentTermMappings.Add(new PaymentTermMapping { ConnectionId = _connection.Id, PaymentTermExternalId = "NET30", QboTermId = "3" });
            await db.SaveChangesAsync();
        }

        var p = TestPayloads.Vendor(accountNumber: "ACCT-9");
        p.PaymentTermExternalId = "NET30";
        p.CurrencyCode = "pkr";
        p.IsActive = false;
        var vendor = (RemoteVendor)(await Build(SyncKind.Vendor, p)).Entity;

        vendor.AccountNumber.Should().Be("ACCT-9");
        vendor.TermId.Should().Be("3");
        vendor.CurrencyCode.Should().Be("PKR");
        vendor.Active.Should().BeFalse();
        vendor.TaxId.Should().Be("VT-V-1");

        p.PaymentTermExternalId = "NET60";
        var unmapped = await Build(SyncKind.Vendor, p);
        ((RemoteVendor)unmapped.Entity).TermId.Should().BeNull("an unmapped term is left off (validation warns about it)");
    }

    // ── Items ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Item_type_and_accounts_follow_the_payload_and_settings()
    {
        var goods = (RemoteItem)(await Build(SyncKind.Item, TestPayloads.Item())).Entity;
        goods.Type.Should().Be(RemoteItemType.NonInventory);
        goods.IncomeAccountId.Should().Be("ACC-INC");
        goods.ExpenseAccountId.Should().Be("ACC-EXP");
        goods.UnitPrice.Should().Be(100m);
        goods.PurchaseCost.Should().Be(60m);
        goods.Sku.Should().Be("W-1");

        var service = TestPayloads.Item();
        service.Kind = ItemPayloadKind.Service;
        ((RemoteItem)(await Build(SyncKind.Item, service)).Entity).Type.Should().Be(RemoteItemType.Service);

        await _h.UpdateSettingsAsync(s => s.ItemTypeDefault = ItemTypeDefault.Service);
        ((RemoteItem)(await Build(SyncKind.Item, TestPayloads.Item())).Entity).Type.Should().Be(RemoteItemType.Service);

        var soldOnly = TestPayloads.Item();
        soldOnly.IsPurchased = false;
        var sold = (RemoteItem)(await Build(SyncKind.Item, soldOnly)).Entity;
        sold.ExpenseAccountId.Should().BeNull();
        sold.PurchaseCost.Should().BeNull();
        sold.IncomeAccountId.Should().Be("ACC-INC");
    }

    // ── Sales invoice (D-4, D-5) ─────────────────────────────────────────────

    [Fact]
    public async Task Invoice_lines_go_at_gross_with_one_discount_line_for_line_and_header_discounts()
    {
        await SeedMapAsync(SyncKind.Customer, "C-1", "Acme", remoteId: "58");
        await SeedMapAsync(SyncKind.Item, "I-1", "Widget", remoteId: "12");
        await SeedMapAsync(SyncKind.Item, "I-2", "Gadget", remoteId: "13");

        // Lines at gross, each rounded: 3 × 19.99 = 59.97 and 7 × 3.333 = 23.331 → 23.33, so 83.30.
        // SCM rounds once: subtotal 83.30, discount round(5.997 + 3.49965) = 9.50 + 5.00 header = 14.50,
        // tax round(9.17541) = 9.18, grand total 77.98. The discount line is the residue
        // 83.30 + 9.18 − 77.98 = 14.50, so QuickBooks lands on 77.98 exactly.
        var p = TestPayloads.Invoice(headerDiscount: 5m, lines:
        [
            new TestPayloads.Line("I-1", 3, 19.99m, Discount: 10m, Tax: 17m),
            new TestPayloads.Line("I-2", 7, 3.333m, Discount: 15m, Tax: 0m)
        ]);

        var built = await Build(SyncKind.SalesInvoice, p);
        var invoice = (RemoteInvoice)built.Entity;

        built.Unresolved.Should().BeEmpty();
        invoice.CustomerId.Should().Be("58");
        invoice.Lines.Select(l => l.ItemId).Should().Equal("12", "13");
        invoice.Lines.Select(l => l.Amount).Should().Equal(59.97m, 23.33m);
        invoice.Lines.Select(l => l.UnitPrice).Should().Equal(19.99m, 3.333m);
        invoice.Lines.Select(l => l.Quantity).Should().Equal(3m, 7m);
        invoice.Lines.Select(l => l.TaxCodeId).Should().Equal("TAX17", "TAX0");
        p.ExpectedTotal.Should().Be(77.98m);
        p.ExpectedTaxAmount.Should().Be(9.18m);
        invoice.DiscountAmount.Should().Be(14.50m);
        invoice.DiscountAccountId.Should().Be("ACC-DSC");
        built.Warnings.Should().BeEmpty();

        // QuickBooks' total equals SCM's, to the cent.
        (invoice.Lines.Sum(l => l.Amount) - invoice.DiscountAmount + p.ExpectedTaxAmount).Should().Be(p.ExpectedTotal);

        invoice.DocNumber.Should().Be("INV-0001");
        invoice.CurrencyCode.Should().Be("PKR");
        invoice.PrivateNote.Should().Be("SO-1 · DLV-1");
        built.Lookup.DocNumber.Should().Be("INV-0001");
    }

    [Fact]
    public async Task No_discount_means_no_discount_line_or_account()
    {
        var invoice = (RemoteInvoice)(await Build(SyncKind.SalesInvoice, TestPayloads.Invoice(), dryRun: true)).Entity;
        invoice.DiscountAmount.Should().Be(0m);
        invoice.DiscountAccountId.Should().BeNull();
    }

    [Fact]
    public async Task Unresolved_references_are_reported_live_and_placeheld_in_a_dry_run()
    {
        await SeedMapAsync(SyncKind.Customer, "C-1", "Acme", remoteId: null);   // held, not in QuickBooks

        var live = await Build(SyncKind.SalesInvoice, TestPayloads.Invoice());
        live.Unresolved.Should().BeEquivalentTo(new[] { new GatewayDependency(SyncKind.Customer, "C-1"), new GatewayDependency(SyncKind.Item, "I-1") });
        ((RemoteInvoice)live.Entity).CustomerId.Should().BeEmpty();

        var dry = await Build(SyncKind.SalesInvoice, TestPayloads.Invoice(), dryRun: true);
        ((RemoteInvoice)dry.Entity).CustomerId.Should().Be("dry-run:Customer:C-1");
    }

    // ── Bill (D-10) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Bill_lines_use_the_item_when_known_otherwise_the_account_for_their_category()
    {
        await SeedMapAsync(SyncKind.Vendor, "V-1", "Karachi Supplies", remoteId: "70");
        await SeedMapAsync(SyncKind.Item, "I-1", "Widget", remoteId: "12");

        var built = await Build(SyncKind.Bill, TestPayloads.Bill());
        var bill = (RemoteBill)built.Entity;

        bill.VendorId.Should().Be("70");
        built.Lookup.Should().Be(new RemoteLookup(DocNumber: "SUP-INV-1", VendorRemoteId: "70"));

        bill.Lines[0].ItemId.Should().Be("12");
        bill.Lines[0].AccountId.Should().BeNull();
        bill.Lines[0].Quantity.Should().Be(10m);
        bill.Lines[0].TaxCodeId.Should().Be("TAX17");

        bill.Lines[1].ItemId.Should().BeNull();
        bill.Lines[1].AccountId.Should().Be("ACC-FRT", "freight posts to the freight account");
        bill.Lines[1].TaxCodeId.Should().Be("TAX-PUR", "no rate on the line → the default purchase tax code");

        bill.Lines[2].AccountId.Should().Be("ACC-EXP", "other lines post to the default expense account");
        bill.Lines.Select(l => l.Amount).Should().Equal(600m, 50m, 25m);
    }

    [Fact]
    public async Task Bill_line_without_a_rate_and_no_default_code_has_no_tax_code()
    {
        await _h.UpdateSettingsAsync(s => s.DefaultPurchaseTaxCodeId = null);
        var bill = (RemoteBill)(await Build(SyncKind.Bill, TestPayloads.Bill(), dryRun: true)).Entity;
        bill.Lines[1].TaxCodeId.Should().BeNull();
    }

    [Fact]
    public async Task Goods_without_an_item_post_to_the_default_expense_account()
    {
        var p = TestPayloads.Bill(lines: [new BillLinePayload { Category = BillLineCategory.Goods, Amount = 10m }]);
        var built = await Build(SyncKind.Bill, p, dryRun: true);
        ((RemoteBill)built.Entity).Lines[0].AccountId.Should().Be("ACC-EXP");
        built.Unresolved.Should().ContainSingle(d => d.Kind == SyncKind.Vendor, "only the vendor is a dependency");
    }

    // ── Canonical payloads ───────────────────────────────────────────────────

    [Fact]
    public void Fingerprints_ignore_trailing_decimal_zeros_but_see_real_changes()
    {
        var a = TestPayloads.Item();
        var b = TestPayloads.Item();
        a.SalesPrice = 10.5m;
        b.SalesPrice = 10.50m;
        SyncPayloads.Fingerprint(SyncPayloads.Serialize(a)).Should().Be(SyncPayloads.Fingerprint(SyncPayloads.Serialize(b)));

        b.SalesPrice = 10.51m;
        SyncPayloads.Fingerprint(SyncPayloads.Serialize(a)).Should().NotBe(SyncPayloads.Fingerprint(SyncPayloads.Serialize(b)));

        var roundTrip = (ItemPayload)SyncPayloads.Deserialize(SyncKind.Item, SyncPayloads.Serialize(a));
        roundTrip.SalesPrice.Should().Be(10.5m);
        roundTrip.Kind.Should().Be(ItemPayloadKind.Goods);
    }

    [Fact]
    public void Command_keys_differ_by_source_system_and_operation()
    {
        var scm  = SyncPayloads.CommandKey(1, "SCM", SyncKind.Customer, "C-1", OutboxOperation.Upsert, "fp");
        var pos  = SyncPayloads.CommandKey(1, "POS", SyncKind.Customer, "C-1", OutboxOperation.Upsert, "fp");
        var void_ = SyncPayloads.CommandKey(1, "SCM", SyncKind.Customer, "C-1", OutboxOperation.Void, "fp");

        new[] { scm, pos, void_ }.Should().OnlyHaveUniqueItems();
        scm.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]+$");
    }
}
