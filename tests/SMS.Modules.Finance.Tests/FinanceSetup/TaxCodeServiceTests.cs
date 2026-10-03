using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

using static SMS.Modules.Finance.Tests.FinanceSetup.SetupDesk;

namespace SMS.Modules.Finance.Tests.FinanceSetup;

/// <summary>SAP alignment, work package A — tax codes: every validation rule, uniqueness, defaults per side, deactivation.</summary>
public class TaxCodeServiceTests
{
    private readonly SetupWorld _world = new();
    private readonly SetupDesk  _acme;
    private readonly SetupDesk  _globex;

    public TaxCodeServiceTests()
    {
        _acme   = _world.For(Guid.NewGuid());
        _globex = _world.For(Guid.NewGuid());
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_code_is_trimmed_and_upper_cased_and_everything_is_stored_as_given()
    {
        var saved = await _acme.TaxCodes(s => s.CreateAsync(new SaveTaxCodeRequest
        {
            Code = "  gst-17_a ", Name = "  GST 17%  ", Description = "  Standard rate  ", RatePercent = 17.25m, Usage = " sales ",
            IsDefault = false, IsActive = true
        }, SetupWorld.User));

        saved.TaxCode.Code.Should().Be("GST-17_A");
        saved.TaxCode.Name.Should().Be("GST 17%");
        saved.TaxCode.Description.Should().Be("Standard rate");
        saved.TaxCode.RatePercent.Should().Be(17.25m);
        saved.TaxCode.Usage.Should().Be("SALES");
        saved.TaxCode.IsActive.Should().BeTrue();
        saved.Message.Should().Contain("GST-17_A").And.Contain("17.25%");

        await using var db = _world.Auditor();
        var row = await db.TaxCodes.SingleAsync();
        row.OrganizationId.Should().Be(_acme.Org);
        row.CreatedBy.Should().Be(SetupWorld.User);
        row.CreatedDate.Should().Be(TestClock.Start);
    }

    [Theory]
    [InlineData("",                      "Give the tax code a code")]
    [InlineData("   ",                   "Give the tax code a code")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU", "at most 20 characters")]
    [InlineData("GST 17",                "only the letters A–Z")]
    [InlineData("GST.17",                "only the letters A–Z")]
    [InlineData("GST/17",                "only the letters A–Z")]
    [InlineData("ÄST",                   "only the letters A–Z")]
    public async Task A_bad_code_is_refused(string code, string why)
    {
        var act = () => _acme.CreateCode(code, 17m);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage($"*{why}*");
    }

    [Fact]
    public async Task A_code_of_exactly_twenty_characters_is_accepted()
    {
        var saved = await _acme.CreateCode("ABCDEFGHIJKLMNOPQRST", 5m);
        saved.TaxCode.Code.Should().HaveLength(20);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_name_is_required(string? name)
    {
        var req = Code("GST17", 17m);
        req.Name = name;
        var act = () => _acme.TaxCodes(s => s.CreateAsync(req, SetupWorld.User));
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*name*");
    }

    [Fact]
    public async Task A_name_longer_than_100_is_refused_and_100_is_accepted()
    {
        var req = Code("GST17", 17m);
        req.Name = new string('n', 101);
        var act = () => _acme.TaxCodes(s => s.CreateAsync(req, SetupWorld.User));
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*at most 100*");

        req.Name = new string('n', 100);
        (await _acme.TaxCodes(s => s.CreateAsync(req, SetupWorld.User))).TaxCode.Name.Should().HaveLength(100);
    }

    [Fact]
    public async Task A_description_longer_than_300_is_refused_and_a_blank_one_is_stored_as_none()
    {
        var req = Code("GST17", 17m);
        req.Description = new string('d', 301);
        var act = () => _acme.TaxCodes(s => s.CreateAsync(req, SetupWorld.User));
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*description*300*");

        req.Description = "   ";
        (await _acme.TaxCodes(s => s.CreateAsync(req, SetupWorld.User))).TaxCode.Description.Should().BeNull();
    }

    [Theory]
    [InlineData("-0.01", "between 0 and 100")]
    [InlineData("100.01", "between 0 and 100")]
    [InlineData("250", "between 0 and 100")]
    [InlineData("17.125", "at most two decimals")]
    [InlineData("0.001", "at most two decimals")]
    public async Task A_bad_rate_is_refused(string rate, string why)
    {
        var act = () => _acme.CreateCode("GST", decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture));
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage($"*{why}*");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("100")]
    [InlineData("7.5")]
    [InlineData("99.99")]
    [InlineData("17.10")]
    public async Task Rates_from_0_to_100_with_two_decimals_are_accepted(string rate)
    {
        var value = decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture);
        (await _acme.CreateCode("R", value)).TaxCode.RatePercent.Should().Be(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SALE")]
    [InlineData("ALL")]
    [InlineData("PURCHASES")]
    public async Task Usage_must_be_sales_purchase_or_both(string? usage)
    {
        var req = Code("GST17", 17m);
        req.Usage = usage;
        var act = () => _acme.TaxCodes(s => s.CreateAsync(req, SetupWorld.User));
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*SALES, PURCHASE or BOTH*");
    }

    [Theory]
    [InlineData("purchase", "PURCHASE")]
    [InlineData("Both", "BOTH")]
    [InlineData("SALES", "SALES")]
    public async Task Usage_is_accepted_in_any_case(string usage, string stored)
    {
        (await _acme.CreateCode("X", 1m, usage)).TaxCode.Usage.Should().Be(stored);
    }

    [Fact]
    public async Task No_request_is_a_bad_request()
    {
        var act = () => _acme.TaxCodes(s => s.CreateAsync(null!, SetupWorld.User));
        await act.Should().ThrowAsync<BadRequestException>();
    }

    // ── Uniqueness ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_code_is_unique_in_its_organization_whatever_the_case()
    {
        await _acme.CreateCode("GST17", 17m);

        var act = () => _acme.CreateCode("gst17", 18m);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*already a tax code GST17*");
    }

    [Fact]
    public async Task A_deactivated_code_still_holds_its_code_and_the_message_says_to_reactivate_it()
    {
        await _acme.CreateCode("OLD", 5m, isActive: false);

        var act = () => _acme.CreateCode("OLD", 6m);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*deactivated*reactivate OLD*");
    }

    [Fact]
    public async Task Two_organizations_may_use_the_same_code_and_never_see_each_others()
    {
        await _acme.CreateCode("GST17", 17m, "SALES", isDefault: true);
        await _globex.CreateCode("GST17", 16m, "SALES", isDefault: true);

        var acme   = await _acme.ListCodes(includeInactive: true);
        var globex = await _globex.ListCodes(includeInactive: true);

        acme.Should().ContainSingle().Which.RatePercent.Should().Be(17m);
        globex.Should().ContainSingle().Which.RatePercent.Should().Be(16m);
        acme.Single().IsDefault.Should().BeTrue("Globex's default is Globex's business");
    }

    [Fact]
    public async Task A_super_admin_working_in_an_organization_is_limited_to_it_although_the_tenant_filter_lets_them_through()
    {
        await _globex.CreateCode("GST17", 16m);

        // Acme's super admin sees every row through the filter; the service still answers for Acme only.
        var list = await _acme.TaxCodes(s => s.ListAsync(null, true), superAdmin: true);
        list.Should().BeEmpty();

        var created = await _acme.TaxCodes(s => s.CreateAsync(Code("GST17", 17m), SetupWorld.User), superAdmin: true);
        created.TaxCode.Code.Should().Be("GST17", "Globex's GST17 does not collide with Acme's");
    }

    [Fact]
    public async Task Changing_a_code_to_one_already_taken_is_refused_but_keeping_its_own_is_not()
    {
        await _acme.CreateCode("GST17", 17m);
        var other = (await _acme.CreateCode("GST16", 16m)).TaxCode;

        var clash = () => _acme.UpdateCode(other.Uuid, Code("GST17", 16m));
        (await clash.Should().ThrowAsync<ConflictException>()).WithMessage("*GST17*");

        var same = await _acme.UpdateCode(other.Uuid, Code("gst16", 16.5m));
        same.TaxCode.Code.Should().Be("GST16");
    }

    [Fact]
    public async Task Another_organizations_code_cannot_be_changed_and_an_unknown_one_is_not_found()
    {
        var globex = (await _globex.CreateCode("GST17", 16m)).TaxCode;

        var theirs  = () => _acme.UpdateCode(globex.Uuid, Code("GST17", 0m));
        var unknown = () => _acme.UpdateCode(Guid.NewGuid(), Code("GST17", 0m));

        await theirs.Should().ThrowAsync<NotFoundException>();
        await unknown.Should().ThrowAsync<NotFoundException>();
        (await _globex.ListCodes()).Single().RatePercent.Should().Be(16m);
    }

    // ── One default per side ─────────────────────────────────────────────────

    [Fact]
    public async Task Setting_a_sales_default_takes_it_from_the_previous_sales_default_and_leaves_purchases_alone()
    {
        await _acme.CreateCode("S1", 17m, "SALES", isDefault: true);
        await _acme.CreateCode("P1", 16m, "PURCHASE", isDefault: true);

        var saved = await _acme.CreateCode("S2", 18m, "SALES", isDefault: true);

        saved.Message.Should().Contain("now the default for sales").And.Contain("S1 is no longer the default");
        saved.Message.Should().NotContain("No code is the default");
        var codes = (await _acme.ListCodes()).ToDictionary(c => c.Code);
        codes["S2"].IsDefault.Should().BeTrue();
        codes["S1"].IsDefault.Should().BeFalse();
        codes["P1"].IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task A_both_default_competes_with_both_sides()
    {
        await _acme.CreateCode("S1", 17m, "SALES", isDefault: true);
        await _acme.CreateCode("P1", 16m, "PURCHASE", isDefault: true);

        var saved = await _acme.CreateCode("B1", 15m, "BOTH", isDefault: true);

        saved.Message.Should().Contain("default for sales and purchases").And.Contain("are no longer defaults");
        var codes = (await _acme.ListCodes()).ToDictionary(c => c.Code);
        codes.Values.Where(c => c.IsDefault).Select(c => c.Code).Should().Equal("B1");
    }

    [Fact]
    public async Task A_side_default_takes_over_from_a_both_default_and_the_other_side_is_left_without_one_and_told_so()
    {
        await _acme.CreateCode("B1", 15m, "BOTH", isDefault: true);

        var saved = await _acme.CreateCode("S1", 17m, "SALES", isDefault: true);

        saved.Message.Should().Contain("B1 is no longer the default").And.Contain("No code is the default for purchases now");
        var codes = (await _acme.ListCodes()).ToDictionary(c => c.Code);
        codes["S1"].IsDefault.Should().BeTrue();
        codes["B1"].IsDefault.Should().BeFalse();

        await using (var db = _acme.Finance())
        {
            var b1 = await db.TaxCodes.SingleAsync(t => t.Code == "B1");
            b1.ModifiedBy.Should().Be(SetupWorld.User, "the code that lost its default was changed by this user");
            b1.ModifiedDate.Should().NotBeNull();
        }

        (await _acme.Lookup(l => l.GetDefaultAsync("SALES")))!.Code.Should().Be("S1");
        (await _acme.Lookup(l => l.GetDefaultAsync("PURCHASE"))).Should().BeNull();
    }

    [Fact]
    public async Task Defaults_of_the_other_organization_are_never_cleared()
    {
        await _globex.CreateCode("G1", 16m, "BOTH", isDefault: true);

        await _acme.CreateCode("A1", 17m, "BOTH", isDefault: true);

        (await _globex.ListCodes()).Single().IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Making_an_existing_code_the_default_switches_and_changing_its_usage_reconsiders_the_sides()
    {
        await _acme.CreateCode("P1", 16m, "PURCHASE", isDefault: true);
        var s1 = (await _acme.CreateCode("S1", 17m, "SALES")).TaxCode;

        // S1 becomes the default — for sales; P1 keeps purchases.
        var req = From(s1);
        req.IsDefault = true;
        await _acme.UpdateCode(s1.Uuid, req);
        (await _acme.ListCodes()).Where(c => c.IsDefault).Select(c => c.Code).Should().BeEquivalentTo(["S1", "P1"]);

        // S1 now covers both sides, so it competes with P1 as well.
        req.Usage = "BOTH";
        var saved = await _acme.UpdateCode(s1.Uuid, req);
        saved.Message.Should().Contain("P1 is no longer the default");
        (await _acme.ListCodes()).Where(c => c.IsDefault).Select(c => c.Code).Should().Equal("S1");
    }

    [Fact]
    public async Task Unticking_default_leaves_the_side_without_one_and_says_so()
    {
        var s1 = (await _acme.CreateCode("S1", 17m, "SALES", isDefault: true)).TaxCode;

        var req = From(s1);
        req.IsDefault = false;
        var saved = await _acme.UpdateCode(s1.Uuid, req);

        saved.TaxCode.IsDefault.Should().BeFalse();
        saved.Message.Should().Contain("No code is the default for sales now");
    }

    // ── Deactivation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Deactivating_a_default_code_clears_its_default_even_when_the_request_still_says_default()
    {
        var s1 = (await _acme.CreateCode("S1", 17m, "SALES", isDefault: true)).TaxCode;

        var req = From(s1);
        req.IsActive = false; // IsDefault still true, as a form that only flipped "active" would send
        var saved = await _acme.UpdateCode(s1.Uuid, req);

        saved.TaxCode.IsActive.Should().BeFalse();
        saved.TaxCode.IsDefault.Should().BeFalse();
        saved.Message.Should().Contain("deactivated").And.Contain("No code is the default for sales now");
        (await _acme.Lookup(l => l.GetDefaultAsync("SALES"))).Should().BeNull();
    }

    [Fact]
    public async Task A_code_created_inactive_is_never_the_default_and_takes_no_default_away()
    {
        await _acme.CreateCode("S1", 17m, "SALES", isDefault: true);

        var saved = await _acme.CreateCode("S2", 18m, "SALES", isDefault: true, isActive: false);

        saved.TaxCode.IsDefault.Should().BeFalse();
        saved.Message.Should().Contain("inactive");
        (await _acme.Lookup(l => l.GetDefaultAsync("SALES")))!.Code.Should().Be("S1");
    }

    [Fact]
    public async Task A_deactivated_code_is_kept_and_can_be_reactivated()
    {
        var s1 = (await _acme.CreateCode("S1", 17m, "SALES")).TaxCode;
        var req = From(s1);
        req.IsActive = false;
        await _acme.UpdateCode(s1.Uuid, req);

        (await _acme.ListCodes()).Should().BeEmpty();
        (await _acme.ListCodes(includeInactive: true)).Should().ContainSingle(c => c.Code == "S1" && !c.IsActive);

        req.IsActive = true;
        var again = await _acme.UpdateCode(s1.Uuid, req);
        again.Message.Should().Contain("active again");
        (await _acme.ListCodes()).Should().ContainSingle();
    }

    // ── Rate changes and renames ─────────────────────────────────────────────

    [Fact]
    public async Task Changing_the_rate_is_allowed_and_the_message_says_documents_keep_the_old_one()
    {
        var gst = (await _acme.CreateCode("GST", 16m, "SALES")).TaxCode;
        var req = From(gst);
        req.RatePercent = 18m;

        var saved = await _acme.UpdateCode(gst.Uuid, req);

        saved.TaxCode.RatePercent.Should().Be(18m);
        saved.Message.Should().Contain("from 16% to 18%").And.Contain("documents already raised keep 16%").And.Contain("new lines use 18%");
    }

    [Fact]
    public async Task An_unused_code_can_be_renamed()
    {
        var gst = (await _acme.CreateCode("GTS17", 17m)).TaxCode;
        var req = From(gst);
        req.Code = "GST17";

        var saved = await _acme.UpdateCode(gst.Uuid, req);

        saved.TaxCode.Code.Should().Be("GST17");
        saved.Message.Should().Contain("GTS17 is now GST17");
    }

    [Fact]
    public async Task A_code_used_on_a_sale_order_line_cannot_be_renamed_but_everything_else_about_it_can_change()
    {
        var gst = (await _acme.CreateCode("GST17", 17m, "SALES")).TaxCode;
        var order = await _acme.PlaceOrder(false, 17m);
        await using (var demand = _acme.Demand())
        {
            var line = await demand.SaleOrderLines.SingleAsync(l => l.SaleOrderId == order.Id);
            line.TaxCodeUuid = gst.Uuid;
            line.TaxCode = gst.Code;
            await demand.SaveChangesAsync();
        }

        var req = From(gst);
        req.Code = "GST-17";
        var rename = () => _acme.UpdateCode(gst.Uuid, req);
        (await rename.Should().ThrowAsync<ConflictException>())
            .WithMessage("*GST17 is already used on sale orders*cannot be renamed to GST-17*deactivate GST17*");

        req.Code = "gst17"; // the same code in another case is not a rename
        req.Name = "GST standard";
        req.RatePercent = 18m;
        (await _acme.UpdateCode(gst.Uuid, req)).TaxCode.Name.Should().Be("GST standard");
    }

    [Fact]
    public async Task A_code_used_on_a_sales_invoice_line_or_a_supplier_invoice_cannot_be_renamed()
    {
        var onSales    = (await _acme.CreateCode("SAL", 17m, "SALES")).TaxCode;
        var onSupplier = (await _acme.CreateCode("PUR", 16m, "PURCHASE")).TaxCode;

        var salesInvoice = Receivables.Invoice(_acme.Org, Guid.NewGuid(), "SINV-1", new DateTime(2026, 9, 1), 117m);
        salesInvoice.Lines.Add(new SalesInvoiceLine
        {
            UUID = Guid.NewGuid(), OrganizationId = _acme.Org, LineNo = 1, SoLineUuid = Guid.NewGuid(), VariantUuid = Guid.NewGuid(),
            Quantity = 1m, UnitPrice = 100m, TaxPercent = 17m, LineTotal = 117m, TaxCodeUuid = onSales.Uuid, TaxCode = onSales.Code
        });

        await _acme.Seed(
            salesInvoice,
            new Invoice
            {
                UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), InvoiceNumber = "INV-1", SupplierId = Guid.NewGuid(),
                SupplierName = "Karachi Steel", Currency = "PKR", TaxCodeUuid = onSupplier.Uuid, TaxCode = onSupplier.Code,
                CreatedBy = 1, CreatedDate = DateTime.UtcNow
            });

        var renameSales = From(onSales);
        renameSales.Code = "SAL2";
        var renameSupplier = From(onSupplier);
        renameSupplier.Code = "PUR2";

        (await ((Func<Task>)(() => _acme.UpdateCode(onSales.Uuid, renameSales))).Should().ThrowAsync<ConflictException>())
            .WithMessage("*sales invoices*");
        (await ((Func<Task>)(() => _acme.UpdateCode(onSupplier.Uuid, renameSupplier))).Should().ThrowAsync<ConflictException>())
            .WithMessage("*supplier invoices*");
    }

    // ── Rename vs. other modules that keep the code by its text (ITaxCodeReferenceChecker) ──

    [Fact]
    public async Task A_code_mapped_in_QuickBooks_cannot_be_renamed_and_the_checker_is_asked_about_this_organizations_old_code()
    {
        var gst  = (await _acme.CreateCode("GST17", 17m, "SALES")).TaxCode;
        var refs = new FakeTaxCodeReferences();
        refs.Mapped.Add((_acme.Org, "GST17"));

        var req = From(gst);
        req.Code = "GST-17";
        var rename = () => UpdateWith(refs, gst.Uuid, req);

        (await rename.Should().ThrowAsync<ConflictException>())
            .WithMessage("GST17 is mapped in the QuickBooks tax mappings, which find it by its text, so it cannot be renamed to GST-17. "
                       + "Create GST-17 as a new code, map it and deactivate GST17 instead.");
        refs.Asked.Should().Equal((_acme.Org, "GST17"));
        (await _acme.ListCodes()).Single().Code.Should().Be("GST17", "nothing was saved");
    }

    [Fact]
    public async Task A_code_both_used_on_a_document_and_mapped_names_both_reasons()
    {
        var gst = (await _acme.CreateCode("GST17", 17m, "SALES")).TaxCode;
        var order = await _acme.PlaceOrder(false, 17m);
        await using (var demand = _acme.Demand())
        {
            var line = await demand.SaleOrderLines.SingleAsync(l => l.SaleOrderId == order.Id);
            line.TaxCodeUuid = gst.Uuid;
            line.TaxCode     = gst.Code;
            await demand.SaveChangesAsync();
        }

        var refs = new FakeTaxCodeReferences();
        refs.Mapped.Add((_acme.Org, "GST17"));
        var req = From(gst);
        req.Code = "GST-17";
        var rename = () => UpdateWith(refs, gst.Uuid, req);

        (await rename.Should().ThrowAsync<ConflictException>())
            .WithMessage("GST17 is already used on sale orders, which keep and print the code, and is mapped in the QuickBooks tax mappings*"
                       + "cannot be renamed to GST-17*map it and deactivate GST17*");
    }

    [Fact]
    public async Task A_code_another_organization_mapped_or_nobody_mapped_can_still_be_renamed()
    {
        var gts  = (await _acme.CreateCode("GTS17", 17m)).TaxCode;
        var refs = new FakeTaxCodeReferences();
        refs.Mapped.Add((_globex.Org, "GTS17"));

        var req = From(gts);
        req.Code = "GST17";

        (await UpdateWith(refs, gts.Uuid, req)).TaxCode.Code.Should().Be("GST17");
    }

    [Fact]
    public async Task An_edit_that_keeps_the_code_text_does_not_ask_the_checkers()
    {
        var gst  = (await _acme.CreateCode("GST17", 17m)).TaxCode;
        var refs = new FakeTaxCodeReferences();
        refs.Mapped.Add((_acme.Org, "GST17"));

        var req = From(gst);
        req.Code        = "gst17"; // the same code in another case is not a rename
        req.RatePercent = 18m;
        req.Name        = "GST standard";

        (await UpdateWith(refs, gst.Uuid, req)).TaxCode.RatePercent.Should().Be(18m);
        refs.Asked.Should().BeEmpty();
    }

    private async Task<TaxCodeSaved> UpdateWith(ITaxCodeReferenceChecker checker, Guid uuid, SaveTaxCodeRequest req)
    {
        await using var db     = _acme.Finance();
        await using var demand = _acme.Demand();
        return await new SMS.Modules.Finance.Services.TaxCodeService(db, demand, _world.Clock, [checker])
            .UpdateAsync(uuid, req, SetupWorld.User);
    }

    /// <summary>Stands in for Integration's checker: a code is "mapped" for the (organization, code) pairs in <see cref="Mapped"/>.</summary>
    private sealed class FakeTaxCodeReferences : ITaxCodeReferenceChecker
    {
        public HashSet<(Guid Org, string Code)> Mapped { get; } = [];
        public List<(Guid Org, string Code)>    Asked  { get; } = [];

        public Task<string?> FindCodeReferenceAsync(Guid organizationId, string code, CancellationToken ct = default)
        {
            Asked.Add((organizationId, code));
            return Task.FromResult(Mapped.Contains((organizationId, code)) ? "the QuickBooks tax mappings" : null);
        }
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_puts_the_default_first_then_orders_by_code_and_a_side_includes_both_codes()
    {
        await _acme.CreateCode("ZERO", 0m, "SALES");
        await _acme.CreateCode("BOTH16", 16m, "BOTH");
        await _acme.CreateCode("PUR5", 5m, "PURCHASE");
        await _acme.CreateCode("SAL17", 17m, "SALES", isDefault: true);
        await _acme.CreateCode("ARCHIVE", 10m, "SALES", isActive: false);

        (await _acme.ListCodes("SALES")).Select(c => c.Code).Should().Equal("SAL17", "BOTH16", "ZERO");
        (await _acme.ListCodes("purchase")).Select(c => c.Code).Should().Equal("BOTH16", "PUR5");
        (await _acme.ListCodes()).Select(c => c.Code).Should().Equal("SAL17", "BOTH16", "PUR5", "ZERO");
        (await _acme.ListCodes("SALES", includeInactive: true)).Select(c => c.Code)
            .Should().Equal("SAL17", "BOTH16", "ZERO", "ARCHIVE");
    }

    [Theory]
    [InlineData("BOTH")]
    [InlineData("INVOICES")]
    public async Task A_side_other_than_sales_or_purchase_is_a_bad_request(string side)
    {
        var act = () => _acme.ListCodes(side);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*SALES or PURCHASE*");
    }

    [Fact]
    public async Task Bad_input_comes_back_as_a_faulted_task_not_as_a_throw_at_the_call()
    {
        await using var db     = _acme.Finance();
        await using var demand = _acme.Demand();
        var svc = new SMS.Modules.Finance.Services.TaxCodeService(db, demand, _world.Clock);

        Task<TaxCodeSaved>? create = null, update = null;
        var call = () =>
        {
            create = svc.CreateAsync(Code("GST 17", 17m), SetupWorld.User);
            update = svc.UpdateAsync(Guid.NewGuid(), null!, SetupWorld.User);
        };

        call.Should().NotThrow("a caller that starts the task and awaits it later gets the error from the await");
        await FluentActions.Awaiting(() => create!).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => update!).Should().ThrowAsync<BadRequestException>();
    }
}
