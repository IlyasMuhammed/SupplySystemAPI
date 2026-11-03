using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.ArAp.Payables;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Finance.Tests.SupplierInvoices;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.ArApPayables;

/// <summary>A48 C9 — TS-MT-001..010 and BR-MT-005..008 through the real services (in-memory).</summary>
public class InvoiceMatchingServiceTests
{
    private static readonly ApActor Actor = ApRig.Actor;

    private static async Task<(ApRig Rig, Guid Invoice)> InvoiceAsync(
        decimal poQty, decimal poPrice, decimal? grnQty, decimal invQty, decimal invPrice, string no = "KSW/1001", bool received = true, bool withGrn = true)
    {
        var rig = ApRig.New();
        await rig.DefaultRuleAsync();
        var (po, lines) = await rig.P.PoAsync(received, (poQty, poPrice));
        Guid? grn = null;
        if (withGrn && grnQty is { } g)
        {
            // The GRN's receipt reaches the PO line too (QtyReceived), as Warehouse posts it.
            grn = (await rig.P.GrnAsync(po, lines, g)).UUID;
            var line = await rig.P.Demand.PurchaseOrderLines.SingleAsync(l => l.UUID == lines[0].UUID);
            line.QtyReceived = g;
            await rig.P.Demand.SaveChangesAsync();
            rig.Forget();
        }

        var request = rig.P.Request(po, lines, grn: grn);
        request.SupplierInvoiceNo = no;
        request.Lines![0].QtyInvoiced = invQty;
        request.Lines![0].UnitPrice   = invPrice;
        var uuid = await rig.P.CreateAsync(request);
        return (rig, uuid);
    }

    [Fact]
    public async Task TS_MT_001_every_line_within_tolerance_is_Matched_and_stored_line_by_line()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 100);

        var result = await rig.Matching.MatchAsync(invoice, null, Actor);

        result.MatchType.Should().Be("ThreeWay");
        result.LineMatchResult.Should().Be("Matched");
        result.WithinTolerance.Should().BeTrue();
        result.MatchStatus.Should().Be(InvoiceMatchStatus.Matched);
        result.ToleranceRule!.Name.Should().Be("DEFAULT");
        result.MatchedByName.Should().Be("Tess Tester");
        var line = result.Lines.Should().ContainSingle().Subject;
        (line.PoQuantity, line.GrnQuantity, line.InvoiceQuantity, line.LineStatus).Should().Be((10m, 10m, 10m, "Matched"));
        (await rig.Fin.Set<InvoiceMatchLine>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TS_MT_002_price_3_percent_over_with_2_percent_is_a_PriceMismatch_and_the_invoice_a_Variance()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 103);

        var result = await rig.Matching.MatchAsync(invoice, null, Actor);

        result.Lines.Single().LineStatus.Should().Be("PriceMismatch");
        result.Lines.Single().PriceVariancePercent.Should().Be(3m);
        result.LineMatchResult.Should().Be("Mismatch");
        result.MatchStatus.Should().Be(InvoiceMatchStatus.Variance);
        result.VarianceAmount.Should().Be(30m);
    }

    [Fact]
    public async Task TS_MT_003_price_1_5_percent_over_with_2_percent_is_Matched()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 101.5m);
        (await rig.Matching.MatchAsync(invoice, null, Actor)).LineMatchResult.Should().Be("Matched");
    }

    [Fact]
    public async Task TS_MT_004_invoice_quantity_above_the_GRN_is_a_QuantityMismatch()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 8, 10, 100);

        var result = await rig.Matching.MatchAsync(invoice, null, Actor);

        result.Lines.Single().LineStatus.Should().Be("QuantityMismatch");
        result.Lines.Single().QuantityVariance.Should().Be(2m);
        result.LineMatchResult.Should().Be("Mismatch");
    }

    [Fact]
    public async Task TS_MT_005_override_with_reason_marks_the_variance_lines_Overridden_and_lets_it_be_approved()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 103);
        await rig.Matching.MatchAsync(invoice, null, Actor);

        await FluentActions.Awaiting(() => rig.Matching.OverrideAsync(invoice, "  ", Actor))
            .Should().ThrowAsync<BadRequestException>().WithMessage("An override needs a reason.");

        var result = await rig.Matching.OverrideAsync(invoice, "Agreed surcharge for express delivery", Actor);
        result.OverrideReason.Should().Be("Agreed surcharge for express delivery");
        result.OverriddenByName.Should().Be("Tess Tester");
        result.Lines.Single().LineStatus.Should().Be("Overridden");

        await FluentActions.Awaiting(() => rig.Matching.OverrideAsync(invoice, "again", Actor))
            .Should().ThrowAsync<ConflictException>().WithMessage("Only an invoice with a match variance can be overridden.");

        (await rig.Matching.ApproveAsync(invoice, Actor)).MatchStatus.Should().Be(InvoiceMatchStatus.Approved);
    }

    [Fact]
    public async Task A_matched_invoice_cannot_be_overridden()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 100);
        await rig.Matching.MatchAsync(invoice, null, Actor);
        await FluentActions.Awaiting(() => rig.Matching.OverrideAsync(invoice, "why not", Actor))
            .Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task TS_MT_006_approve_books_the_invoice_through_the_existing_path_and_creates_its_payment_schedule()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 100);
        await rig.Matching.MatchAsync(invoice, null, Actor);

        var result = await rig.Matching.ApproveAsync(invoice, Actor);

        result.MatchStatus.Should().Be(InvoiceMatchStatus.Approved);
        (await rig.P.LedgerAsync(invoice)).Should().ContainSingle(e => e.TransactionType == "INVOICE_APPROVED" && e.DebitAmount == 1000m);
        var schedule = await rig.ScheduleAsync(invoice);
        (schedule.Status, schedule.AmountRemaining, schedule.OriginalAmount).Should().Be(("Open", 1000m, 1000m));
    }

    [Fact]
    public async Task BR_MT_008_a_variance_not_overridden_blocks_approval_on_both_paths()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 103);
        await rig.Matching.MatchAsync(invoice, null, Actor);

        var text = $"Invoice {(await rig.P.LoadAsync(invoice)).InvoiceNumber} has a match variance; override it before approving.";
        await FluentActions.Awaiting(() => rig.Matching.ApproveAsync(invoice, Actor))
            .Should().ThrowAsync<ConflictException>().WithMessage(text);
        await FluentActions.Awaiting(() => rig.Invoices.ApproveAsync(invoice, null, 7))
            .Should().ThrowAsync<ConflictException>().WithMessage(text);
        (await rig.P.LedgerAsync(invoice)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_invoice_the_A48_match_never_ran_on_is_approved_by_the_existing_path_as_before()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 103);
        (await rig.Invoices.ApproveAsync(invoice, null, 7)).Should().BeTrue();
        await FluentActions.Awaiting(() => rig.Matching.ApproveAsync(invoice, Actor)).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task TS_MT_007_a_duplicate_vendor_invoice_number_is_refused_on_create_and_on_edit()
    {
        var (rig, first) = await InvoiceAsync(10, 100, 10, 10, 100, no: "KSW/2002");
        var (po, lines) = await rig.P.PoAsync(true, (5m, 100m));
        var request = rig.P.Request(po, lines);
        request.SupplierInvoiceNo = " ksw/2002 ";
        var firstNumber = (await rig.P.LoadAsync(first)).InvoiceNumber;

        await FluentActions.Awaiting(() => rig.P.CreateAsync(request)).Should().ThrowAsync<ConflictException>()
            .WithMessage($"Supplier invoice number ksw/2002 is already recorded for Karachi Steel (invoice {firstNumber}).");

        request.SupplierInvoiceNo = "KSW/2003";
        var second = await rig.P.CreateAsync(request);
        await FluentActions.Awaiting(() => rig.Invoices.PatchAsync(second, new PatchInvoiceRequest { SupplierInvoiceNo = "KSW/2002" }, 7))
            .Should().ThrowAsync<ConflictException>();

        // A rejected invoice frees its number.
        (await rig.Invoices.RejectAsync(first, "entered twice", 7)).Should().BeTrue();
        (await rig.Invoices.PatchAsync(second, new PatchInvoiceRequest { SupplierInvoiceNo = "KSW/2002" }, 7)).Should().BeTrue();
    }

    [Fact]
    public async Task TS_MT_008_an_auto_approve_rule_approves_an_invoice_within_tolerance()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 101);
        var rule = await rig.Fin.Set<MatchToleranceRule>().SingleAsync();
        rule.AutoApproveWithin = true;
        await rig.Fin.SaveChangesAsync();
        rig.Forget();

        var result = await rig.Matching.MatchAsync(invoice, null, Actor);

        result.MatchStatus.Should().Be(InvoiceMatchStatus.Approved);
        result.AutoApproved.Should().BeTrue();
        result.AutoApproveMessage.Should().BeNull();
        (await rig.P.LedgerAsync(invoice)).Should().ContainSingle(e => e.TransactionType == "INVOICE_APPROVED");
        (await rig.ScheduleAsync(invoice)).Status.Should().Be("Open");
    }

    [Fact]
    public async Task Auto_approve_off_leaves_a_matched_invoice_for_a_person()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 100);
        var result = await rig.Matching.MatchAsync(invoice, null, Actor);
        result.MatchStatus.Should().Be(InvoiceMatchStatus.Matched);
        result.AutoApproved.Should().BeFalse();
    }

    [Fact]
    public async Task TS_MT_009_two_way_match_compares_with_the_PO_only()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, null, 10, 100, received: false, withGrn: false);

        var result = await rig.Matching.MatchAsync(invoice, null, Actor);

        result.MatchType.Should().Be("TwoWay", "nothing was received and no GRN is named");
        result.LineMatchResult.Should().Be("Matched");
        result.Lines.Single().GrnQuantity.Should().BeNull();

        // Forced three-way on the same invoice: nothing received, so the quantity fails.
        (await rig.Matching.MatchAsync(invoice, "ThreeWay", Actor)).LineMatchResult.Should().Be("Mismatch");
    }

    [Fact]
    public async Task TS_MT_010_partial_delivery_invoiced_for_what_arrived_is_Matched()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 6, 6, 100);

        var result = await rig.Matching.MatchAsync(invoice, null, Actor);

        result.LineMatchResult.Should().Be("Matched");
        var line = result.Lines.Single();
        (line.PoQuantity, line.GrnQuantity, line.InvoiceQuantity, line.PoLineTotal).Should().Be((10m, 6m, 6m, 1000m));
    }

    [Fact]
    public async Task Re_matching_replaces_the_lines_and_clears_an_override()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 103);
        await rig.Matching.MatchAsync(invoice, null, Actor);
        await rig.Matching.OverrideAsync(invoice, "ok", Actor);

        var again = await rig.Matching.MatchAsync(invoice, null, Actor);

        again.OverrideReason.Should().BeNull();
        again.Lines.Should().ContainSingle();
        (await rig.Fin.Set<InvoiceMatchLine>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_approved_invoice_cannot_be_matched_and_an_invoice_without_a_PO_has_nothing_to_match()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 100);
        await rig.Invoices.ApproveAsync(invoice, null, 7);
        await FluentActions.Awaiting(() => rig.Matching.MatchAsync(invoice, null, Actor)).Should().ThrowAsync<ConflictException>()
            .WithMessage("*only a pending, matched or variance invoice can be matched.");

        var vendor = Guid.NewGuid();
        var noPo = await rig.Invoices.CreateAsync(new CreateInvoiceRequest
        {
            SupplierId = vendor, SupplierInvoiceNo = "FREIGHT-1", Subtotal = 50m, InvoiceDate = DateTime.UtcNow, ReceivedDate = DateTime.UtcNow, DueDate = DateTime.UtcNow.AddDays(30)
        }, 7);
        await FluentActions.Awaiting(() => rig.Matching.MatchAsync(noPo, null, Actor)).Should().ThrowAsync<UnprocessableEntityException>();
    }

    [Fact]
    public async Task A_vendor_rule_wins_over_the_organization_default()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 104);
        rig.Partners.Vendor(rig.P.Supplier, "Karachi Steel", "V-KS");
        await rig.Rules.CreateAsync(new ToleranceRuleRequest { Name = "Karachi Steel 5%", VendorUuid = rig.P.Supplier, PriceTolerancePercent = 5m }, Actor);
        rig.Forget();

        var result = await rig.Matching.MatchAsync(invoice, null, Actor);

        result.ToleranceRule!.Name.Should().Be("Karachi Steel 5%");
        result.LineMatchResult.Should().Be("Matched");
    }

    [Fact]
    public async Task Pending_lists_invoices_waiting_for_the_match_or_its_outcome_and_by_PO_lists_all()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 103);
        (await rig.Matching.ListPendingAsync(null, "NotMatched", 1, 25)).TotalRecords.Should().Be(1);
        await rig.Matching.MatchAsync(invoice, null, Actor);
        (await rig.Matching.ListPendingAsync(null, "Mismatch", 1, 25)).Data.Should().ContainSingle(m => m.InvoiceUuid == invoice);
        (await rig.Matching.ListPendingAsync(null, "NotMatched", 1, 25)).TotalRecords.Should().Be(0);
        var po = (await rig.P.LoadAsync(invoice)).PoUuid!.Value;
        (await rig.Matching.ByPurchaseOrderAsync(po)).Should().ContainSingle();
        await FluentActions.Awaiting(() => rig.Matching.ListPendingAsync(null, "Bogus", 1, 25)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task REV_8_two_invoices_matched_on_the_same_received_quantity_cannot_both_be_approved()
    {
        var rig = ApRig.New();
        await rig.DefaultRuleAsync();
        var (po, lines) = await rig.P.PoAsync(true, (10m, 100m));
        var a = await rig.P.CreateAsync(rig.P.Request(po, lines));
        var b = await rig.P.CreateAsync(rig.P.Request(po, lines));
        (await rig.Matching.MatchAsync(a, null, Actor)).LineMatchResult.Should().Be("Matched");
        (await rig.Matching.MatchAsync(b, null, Actor)).LineMatchResult.Should().Be("Matched", "neither is approved yet");

        await rig.Matching.ApproveAsync(a, Actor);
        rig.Forget();

        await FluentActions.Awaiting(() => rig.Matching.ApproveAsync(b, Actor)).Should().ThrowAsync<ConflictException>()
            .WithMessage("*no longer matches its purchase order (line 1 is QuantityMismatch)*");
        await FluentActions.Awaiting(() => rig.Invoices.ApproveAsync(b, null, 7)).Should().ThrowAsync<ConflictException>();
        (await rig.P.PoNowAsync(po.UUID)).Lines.Single().QtyInvoiced.Should().Be(10m, "billed once");
    }

    [Fact]
    public async Task REV_8_a_header_only_invoice_is_matched_against_what_is_still_to_bill()
    {
        var rig = ApRig.New();
        await rig.DefaultRuleAsync();
        var (po, lines) = await rig.P.PoAsync(true, (10m, 100m));
        var first = await rig.P.CreateAsync(rig.P.Request(po, lines));
        await rig.Invoices.ApproveAsync(first, null, 7);
        rig.Forget();

        var headerOnly = await rig.P.CreateAsync(rig.P.Request(po, subtotal: 1000m));
        (await rig.Matching.MatchAsync(headerOnly, null, Actor)).LineMatchResult.Should().Be("Mismatch", "the PO is already billed in full");
    }

    [Fact]
    public async Task REV_23_a_PO_with_receipts_cannot_be_matched_two_way()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 100);
        await FluentActions.Awaiting(() => rig.Matching.MatchAsync(invoice, "TwoWay", Actor)).Should().ThrowAsync<BadRequestException>()
            .WithMessage("Purchase order * has goods received; its invoices are matched three-way.");
    }

    [Fact]
    public async Task FE_7_pending_matches_can_be_searched_by_number_PO_or_supplier()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 100, no: "KSW/7777");
        var stored = await rig.P.LoadAsync(invoice);
        (await rig.Matching.ListPendingAsync(null, null, 1, 25, default, "ksw/77")).Data.Should().ContainSingle();
        (await rig.Matching.ListPendingAsync(null, null, 1, 25, default, stored.PoNumber![^5..])).Data.Should().ContainSingle();
        (await rig.Matching.ListPendingAsync(null, null, 1, 25, default, "karachi")).Data.Should().ContainSingle();
        (await rig.Matching.ListPendingAsync(null, null, 1, 25, default, "nobody")).TotalRecords.Should().Be(0);
    }

    [Fact]
    public async Task FE_6_a_variance_can_be_rejected_from_the_matching_page()
    {
        var (rig, invoice) = await InvoiceAsync(10, 100, 10, 10, 120);
        await rig.Matching.MatchAsync(invoice, null, Actor);
        await FluentActions.Awaiting(() => rig.Matching.RejectAsync(invoice, " ", Actor)).Should().ThrowAsync<BadRequestException>();

        var rejected = await rig.Matching.RejectAsync(invoice, "Price not agreed", Actor);

        rejected.MatchStatus.Should().Be(InvoiceMatchStatus.Rejected);
        (await rig.Matching.ListPendingAsync(null, null, 1, 25)).TotalRecords.Should().Be(0);
    }
}
