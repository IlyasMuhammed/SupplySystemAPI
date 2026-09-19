using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Settlement;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Settlement;

// T-56 — accepting a carrier's bill, or querying it. Stops at approval: whether an approved bill
// posts into Finance is decision G10.
public class InvoiceSettlementTests
{
    private const int User = 42;
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(
        LogisticsDbContext Db,
        CarrierInvoiceService Invoices,
        InvoiceMatchingService Matching,
        FreightAccrualService Accruals,
        ThreeWayMatchService ThreeWay,
        InvoiceSettlementService Settlement,
        FakeSupplierInvoicePoster Payables,
        string DbName);

    private static Harness NewHarness()
    {
        var (db, _, dbName) = LogisticsTestDb.New();
        var config = new ConfigurationBuilder().Build();
        var payables = new FakeSupplierInvoicePoster();

        return new Harness(
            db, new CarrierInvoiceService(db), new InvoiceMatchingService(db),
            new FreightAccrualService(db), new ThreeWayMatchService(db, config),
            new InvoiceSettlementService(db, payables), payables, dbName);
    }

    /// <summary>
    /// Linked to a supplier by default (G10), because that is the ordinary case and an unlinked
    /// carrier cannot have its bills approved at all. <paramref name="linkedToSupplier"/> is how the
    /// tests that prove that reach the unlinked state.
    /// </summary>
    private static async Task<Guid> NewCarrier(
        Harness h, string name = "Beta Road", bool linkedToSupplier = true)
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = name, Code = $"C{Guid.NewGuid():N}"[..6], IsActive = true,
            SupplierId = linkedToSupplier ? Guid.NewGuid() : null
        };
        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return carrier.UUID;
    }

    private static async Task<Guid> NewConsignment(
        Harness h, Guid carrier, string awb = "AWB-1", decimal? quoted = 1260m)
    {
        var carrierRow = await h.Db.Carriers.AsNoTracking().SingleAsync(c => c.UUID == carrier);

        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CarrierId = carrierRow.Id, CarrierName = carrierRow.Name,
            MasterAwb = awb, Status = "IN_TRANSIT",
            FreightCost = quoted, FreightCurrency = quoted is null ? null : "PKR",
            FreightRateSource = quoted is null ? null : "CARRIER",
            ActualDispatchAt = T0, CreatedBy = User, CreatedDate = T0
        };

        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return consignment.UUID;
    }

    private static CarrierInvoiceLineRequest Line(
        decimal amount, string? awb = "AWB-1", string description = "Carriage") =>
        new() { Description = description, AwbNumber = awb, ChargeCode = "BASE", Amount = amount };

    /// <summary>A bill whose lines are tied to movements and compared — ready to approve.</summary>
    private static async Task<Guid> NewSettledInvoice(
        Harness h, Guid carrier, decimal total = 1260m, string number = "INV-1",
        bool accrue = true, bool compare = true, params CarrierInvoiceLineRequest[] lines)
    {
        var uuid = await h.Invoices.CreateAsync(new CreateCarrierInvoiceRequest
        {
            CarrierUuid = carrier, InvoiceNumber = number, Currency = "PKR",
            InvoiceDate = T0, TotalAmount = total,
            Lines = [.. lines.Length > 0 ? lines : [Line(total)]]
        }, User);

        if (accrue) await h.Accruals.SweepAsync(User);

        await h.Matching.MatchInvoiceAsync(uuid, User);

        if (compare) await h.ThreeWay.MatchAsync(uuid, User);

        return uuid;
    }

    // ── Approving ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approving_records_what_the_company_accepts_it_owes()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        var settlement = await h.Settlement.ApproveAsync(
            invoice, new ApproveCarrierInvoiceRequest(), User);

        settlement!.Status.Should().Be("APPROVED");
        settlement.ApprovedAmount.Should().Be(1260m, "the whole bill by default");
        settlement.NotAccepted.Should().Be(0m);
        settlement.ApprovedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Approving_closes_the_accruals_the_bill_settles()
    {
        // Closing is what takes the estimate off the books: the liability was carried at what we
        // guessed, and from here it is carried at what was billed and accepted.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        (await h.Accruals.GetAsync(consignment))!.Status.Should().Be("MATCHED");
        (await h.Accruals.GetSummaryAsync()).OpenCount.Should().Be(1);

        var settlement = await h.Settlement.ApproveAsync(invoice, new ApproveCarrierInvoiceRequest(), User);

        settlement!.AccrualsClosed.Should().Be(1);

        var accrual = await h.Accruals.GetAsync(consignment);
        accrual!.Status.Should().Be("CLOSED");
        accrual.ReleaseReason.Should().Contain("INV-1");

        (await h.Accruals.GetSummaryAsync()).OpenCount.Should().Be(0);
    }

    [Fact]
    public async Task A_bill_with_lines_nobody_could_explain_cannot_be_approved()
    {
        // Approving then would accept charges nobody has checked — the whole failure the matching
        // queue exists to prevent.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);

        var invoice = await NewSettledInvoice(h, carrier, total: 1660m, compare: false, lines:
        [
            Line(1260m),
            Line(400m, awb: "AWB-NOT-OURS", description: "Something else")
        ]);

        var act = async () => await h.Settlement.ApproveAsync(
            invoice, new ApproveCarrierInvoiceRequest(), User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*1 line(s)*not tied to a movement*")
            .WithMessage("*match them or set them aside first*");
    }

    [Fact]
    public async Task Approving_for_less_than_was_billed_needs_a_reason()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        var act = async () => await h.Settlement.ApproveAsync(
            invoice, new ApproveCarrierInvoiceRequest { Amount = 1000m }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*the difference is what it will ask about*");
    }

    [Fact]
    public async Task Approving_for_less_is_allowed_when_it_is_explained()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        var settlement = await h.Settlement.ApproveAsync(invoice, new ApproveCarrierInvoiceRequest
        {
            Amount = 1000m, Note = "Agreed by telephone — the fuel surcharge was billed twice."
        }, User);

        settlement!.ApprovedAmount.Should().Be(1000m);
        settlement.NotAccepted.Should().Be(260m);
        settlement.ApprovalNote.Should().Contain("billed twice");
    }

    [Fact]
    public async Task A_bill_cannot_be_approved_for_more_than_the_carrier_asked_for()
    {
        // That is invention, not approval — and it is the carrier's number that would be queried
        // if the two ever disagreed.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        var act = async () => await h.Settlement.ApproveAsync(
            invoice, new ApproveCarrierInvoiceRequest { Amount = 2000m }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*more than the carrier asked for*");
    }

    [Fact]
    public async Task Approving_for_nothing_is_refused()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        var act = async () => await h.Settlement.ApproveAsync(
            invoice, new ApproveCarrierInvoiceRequest { Amount = 0m }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_bill_cannot_be_approved_twice()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);
        await h.Settlement.ApproveAsync(invoice, new ApproveCarrierInvoiceRequest(), User);

        var act = async () => await h.Settlement.ApproveAsync(
            invoice, new ApproveCarrierInvoiceRequest(), User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*already been approved*")
            .WithMessage("*credit claim*");
    }

    [Fact]
    public async Task A_withdrawn_bill_cannot_be_approved()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);
        await h.Invoices.CancelAsync(invoice, new CancelCarrierInvoiceRequest { Reason = "Duplicate" }, User);

        var act = async () => await h.Settlement.ApproveAsync(
            invoice, new ApproveCarrierInvoiceRequest(), User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*nothing to approve*");
    }

    [Fact]
    public async Task An_approved_bill_says_plainly_where_the_money_went()
    {
        // Until G10 was answered this said the opposite — "nothing here has sent it anywhere" —
        // because nothing had. The warning stayed; what it reports is now the payable.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        var settlement = await h.Settlement.ApproveAsync(invoice, new ApproveCarrierInvoiceRequest(), User);

        settlement!.Warnings.Should().ContainMatch("*raised in Finance as INV-*");
        settlement.PostedInvoiceNumber.Should().NotBeNull();
    }

    [Fact]
    public async Task A_bill_approved_before_G10_says_nobody_will_be_paid()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        await h.Settlement.ApproveAsync(invoice, new ApproveCarrierInvoiceRequest(), User);

        // What an approval made before this existed looks like: approved, and no payable behind it.
        var stored = await h.Db.CarrierInvoices.SingleAsync(i => i.UUID == invoice);
        stored.PostedInvoiceUuid   = null;
        stored.PostedInvoiceNumber = null;
        stored.PostedAt            = null;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        (await h.Settlement.GetAsync(invoice))!.Warnings
            .Should().ContainMatch("*Nobody will be paid until one is*");
    }

    // ── Querying ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_query_is_raised_with_a_reason_and_the_carriers_own_reference()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        var settlement = await h.Settlement.RaiseDisputeAsync(invoice, new RaiseDisputeRequest
        {
            Reason = "Billed on 14 kg where we made it 12.5.",
            Reference = "CASE-8821",
            ExpectedCreditAmount = 140m
        }, User);

        settlement!.Status.Should().Be("DISPUTED");
        settlement.DisputeReason.Should().Contain("14 kg");
        settlement.DisputeReference.Should().Be("CASE-8821");
        settlement.ExpectedCreditAmount.Should().Be(140m);
    }

    [Fact]
    public async Task A_queried_bill_is_still_owed_and_its_accruals_stay_open()
    {
        // Closing them here would take the liability off a month early. A queried charge is still
        // owed until somebody decides it is not.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        await h.Settlement.RaiseDisputeAsync(
            invoice, new RaiseDisputeRequest { Reason = "Overcharged." }, User);

        (await h.Accruals.GetAsync(consignment))!.Status.Should().Be("MATCHED");
        (await h.Accruals.GetSummaryAsync()).OpenCount.Should().Be(1);
    }

    [Fact]
    public async Task A_query_needs_something_somebody_can_restate()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        var act = async () => await h.Settlement.RaiseDisputeAsync(
            invoice, new RaiseDisputeRequest { Reason = "  " }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*a dispute nobody wins*");
    }

    [Fact]
    public async Task An_expected_credit_larger_than_the_bill_is_refused()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        var act = async () => await h.Settlement.RaiseDisputeAsync(invoice, new RaiseDisputeRequest
        {
            Reason = "All of it", ExpectedCreditAmount = 5000m
        }, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*more than the*billed*");
    }

    [Fact]
    public async Task An_approved_bill_cannot_be_queried_again_here()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);
        await h.Settlement.ApproveAsync(invoice, new ApproveCarrierInvoiceRequest(), User);

        var act = async () => await h.Settlement.RaiseDisputeAsync(
            invoice, new RaiseDisputeRequest { Reason = "Second thoughts" }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*credit claim*");
    }

    [Fact]
    public async Task Re_querying_clears_how_the_last_query_ended()
    {
        // Otherwise a freshly raised query reads as one that was already settled.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        await h.Settlement.RaiseDisputeAsync(invoice, new RaiseDisputeRequest { Reason = "First" }, User);
        await h.Settlement.ResolveDisputeAsync(invoice, new ResolveDisputeRequest
        {
            Outcome = "ACCEPTED_AS_BILLED"
        }, User);

        // Approved now, so the second query has to go against a fresh bill.
        var second = await NewSettledInvoice(h, carrier, number: "INV-2", accrue: false);
        await h.Settlement.RaiseDisputeAsync(second, new RaiseDisputeRequest { Reason = "Again" }, User);

        var settlement = await h.Settlement.GetAsync(second);
        settlement!.DisputeOutcome.Should().BeNull();
        settlement.DisputeResolvedAt.Should().BeNull();
    }

    // ── Closing a query ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_credit_closes_the_query_and_approves_at_the_lower_figure()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        await h.Settlement.RaiseDisputeAsync(invoice, new RaiseDisputeRequest
        {
            Reason = "Weight", ExpectedCreditAmount = 260m
        }, User);

        var settlement = await h.Settlement.ResolveDisputeAsync(invoice, new ResolveDisputeRequest
        {
            Outcome = "CREDIT_RECEIVED", ApprovedAmount = 1000m,
            Note = "Credit note CN-114 received."
        }, User);

        settlement!.Status.Should().Be("APPROVED");
        settlement.ApprovedAmount.Should().Be(1000m);
        settlement.NotAccepted.Should().Be(260m);
        settlement.DisputeOutcome.Should().Be("CREDIT_RECEIVED");
        settlement.DisputeResolvedAt.Should().NotBeNull();

        (await h.Accruals.GetAsync(consignment))!.Status.Should().Be("CLOSED");
    }

    [Fact]
    public async Task A_credit_has_to_say_what_it_credited_it_to()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);
        await h.Settlement.RaiseDisputeAsync(invoice, new RaiseDisputeRequest { Reason = "Weight" }, User);

        var act = async () => await h.Settlement.ResolveDisputeAsync(
            invoice, new ResolveDisputeRequest { Outcome = "CREDIT_RECEIVED" }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*does not change the figure is not a credit*");
    }

    [Fact]
    public async Task Conceding_the_query_approves_the_bill_as_it_was_issued()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);
        await h.Settlement.RaiseDisputeAsync(invoice, new RaiseDisputeRequest { Reason = "Weight" }, User);

        var settlement = await h.Settlement.ResolveDisputeAsync(invoice, new ResolveDisputeRequest
        {
            Outcome = "ACCEPTED_AS_BILLED", Note = "Carrier produced the weighbridge ticket."
        }, User);

        settlement!.ApprovedAmount.Should().Be(1260m);
        settlement.NotAccepted.Should().Be(0m);
        settlement.DisputeOutcome.Should().Be("ACCEPTED_AS_BILLED");
    }

    [Fact]
    public async Task Letting_a_difference_go_has_to_say_why()
    {
        // A query abandoned without a reason looks identical to one nobody followed up.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);
        await h.Settlement.RaiseDisputeAsync(invoice, new RaiseDisputeRequest { Reason = "Weight" }, User);

        var act = async () => await h.Settlement.ResolveDisputeAsync(
            invoice, new ResolveDisputeRequest { Outcome = "WRITTEN_OFF" }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*nobody followed up*");
    }

    [Fact]
    public async Task An_unknown_outcome_is_refused_and_the_message_lists_the_real_ones()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);
        await h.Settlement.RaiseDisputeAsync(invoice, new RaiseDisputeRequest { Reason = "Weight" }, User);

        var act = async () => await h.Settlement.ResolveDisputeAsync(
            invoice, new ResolveDisputeRequest { Outcome = "GAVE_UP" }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*CREDIT_RECEIVED*")
            .WithMessage("*WRITTEN_OFF*");
    }

    [Fact]
    public async Task Only_a_query_that_was_raised_can_be_closed()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        var act = async () => await h.Settlement.ResolveDisputeAsync(
            invoice, new ResolveDisputeRequest { Outcome = "ACCEPTED_AS_BILLED" }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*Only a query that was raised*");
    }

    [Fact]
    public async Task A_queried_bill_says_it_is_still_owed()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        var settlement = await h.Settlement.RaiseDisputeAsync(
            invoice, new RaiseDisputeRequest { Reason = "Weight" }, User);

        settlement!.Warnings.Should().ContainMatch("*queried and still owed*");
    }

    // ── Not found, and not yours ──────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_bill_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.Settlement.GetAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Settlement.ApproveAsync(
            Guid.NewGuid(), new ApproveCarrierInvoiceRequest(), User)).Should().BeNull();
        (await h.Settlement.RaiseDisputeAsync(
            Guid.NewGuid(), new RaiseDisputeRequest { Reason = "x" }, User)).Should().BeNull();
        (await h.Settlement.ResolveDisputeAsync(
            Guid.NewGuid(), new ResolveDisputeRequest { Outcome = "WRITTEN_OFF" }, User)).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_bill_does_not_exist_here()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        var otherDb = LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid());
        var other   = new InvoiceSettlementService(otherDb, new FakeSupplierInvoicePoster());

        (await other.GetAsync(invoice)).Should().BeNull();
    }

    // ── Reaching Finance (decision G10) ───────────────────────────────────────

    [Fact]
    public async Task An_approved_bill_becomes_a_payable_in_finance()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        await h.Settlement.ApproveAsync(invoice, new ApproveCarrierInvoiceRequest(), User);

        h.Payables.Postings.Should().ContainSingle(
            "this is the gap G10 closed — approval used to reach no ledger at all");

        var posted = h.Payables.Last!;

        posted.SourceType.Should().Be(InvoiceSettlementService.PostingSourceType);
        posted.SourceUuid.Should().Be(invoice);
        posted.SupplierId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_bill_reaches_finance_at_what_was_approved_not_at_what_was_billed()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        var billed = (await h.Settlement.GetAsync(invoice))!.BilledAmount;

        await h.Settlement.ApproveAsync(invoice, new ApproveCarrierInvoiceRequest
        {
            Amount = billed - 200m, Note = "Carrier credited a re-weigh."
        }, User);

        h.Payables.Last!.Subtotal.Should().Be(billed - 200m,
            "the approved figure is what the company accepts it owes");
        h.Payables.Last.Notes.Should().Contain("accepted");
    }

    [Fact]
    public async Task A_carrier_with_no_supplier_cannot_have_its_bills_approved()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, linkedToSupplier: false);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        var approve = async () => await h.Settlement.ApproveAsync(
            invoice, new ApproveCarrierInvoiceRequest(), User);

        await approve.Should().ThrowAsync<ConflictException>()
            .WithMessage("*not linked to a supplier*");

        h.Payables.Postings.Should().BeEmpty();
    }

    [Fact]
    public async Task An_approval_that_finance_refuses_does_not_half_happen()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        h.Payables.Throws = new BadRequestException("Finance said no.");

        var approve = async () => await h.Settlement.ApproveAsync(
            invoice, new ApproveCarrierInvoiceRequest(), User);

        await approve.Should().ThrowAsync<BadRequestException>();

        h.Db.ChangeTracker.Clear();

        var stored = await h.Db.CarrierInvoices.AsNoTracking()
            .SingleAsync(i => i.UUID == invoice);

        stored.Status.Should().NotBe("APPROVED",
            "the approval and the payable are saved together or not at all");
        stored.PostedInvoiceUuid.Should().BeNull();
    }

    [Fact]
    public async Task An_approved_bill_records_which_payable_it_became()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);
        var invoice = await NewSettledInvoice(h, carrier);

        await h.Settlement.ApproveAsync(invoice, new ApproveCarrierInvoiceRequest(), User);

        h.Db.ChangeTracker.Clear();

        var stored = await h.Db.CarrierInvoices.AsNoTracking().SingleAsync(i => i.UUID == invoice);

        stored.PostedInvoiceUuid.Should().NotBeNull();
        stored.PostedInvoiceNumber.Should().StartWith("INV-");
        stored.PostedAt.Should().NotBeNull();
    }
}
