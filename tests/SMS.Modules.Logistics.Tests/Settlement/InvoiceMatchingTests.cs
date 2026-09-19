using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Settlement;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Settlement;

// T-54 — tying each line of a carrier's bill to the movement it charges for.
public class InvoiceMatchingTests
{
    private const int User = 42;
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(
        LogisticsDbContext Db, CarrierInvoiceService Invoices, InvoiceMatchingService Matching, string DbName);

    private static Harness NewHarness()
    {
        var (db, _, dbName) = LogisticsTestDb.New();
        return new Harness(db, new CarrierInvoiceService(db), new InvoiceMatchingService(db), dbName);
    }

    private static async Task<Guid> NewCarrier(Harness h, string name = "Beta Road")
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = name, Code = $"C{Guid.NewGuid():N}"[..6], IsActive = true
        };
        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return carrier.UUID;
    }

    private static async Task<(Guid Uuid, string Number)> NewConsignment(
        Harness h, Guid carrier, string? awb = "AWB-0001", decimal? cost = 1260m)
    {
        var carrierRow = await h.Db.Carriers.AsNoTracking().SingleAsync(c => c.UUID == carrier);

        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CarrierId = carrierRow.Id, CarrierName = carrierRow.Name,
            MasterAwb = awb, Status = "IN_TRANSIT",
            FreightCost = cost, FreightCurrency = cost is null ? null : "PKR",
            CreatedBy = User, CreatedDate = T0
        };

        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return (consignment.UUID, consignment.ConsignmentNumber);
    }

    private static CarrierInvoiceLineRequest Line(
        decimal amount, string? awb = null, string? reference = null,
        string description = "Carriage", string? chargeCode = "BASE") =>
        new()
        {
            Description = description, AwbNumber = awb, ConsignmentReference = reference,
            ChargeCode = chargeCode, Amount = amount
        };

    private static Task<Guid> NewInvoice(
        Harness h, Guid carrier, string number = "INV-1", decimal total = 1260m,
        params CarrierInvoiceLineRequest[] lines) =>
        h.Invoices.CreateAsync(new CreateCarrierInvoiceRequest
        {
            CarrierUuid = carrier, InvoiceNumber = number, Currency = "PKR",
            InvoiceDate = T0, TotalAmount = total,
            Lines = [.. lines.Length > 0 ? lines : [Line(total, awb: "AWB-0001")]]
        }, User);

    // ── Matching on the airway bill ───────────────────────────────────────────

    [Fact]
    public async Task A_line_is_tied_to_the_movement_its_airway_bill_names()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, awb: "AWB-77123");
        var invoice = await NewInvoice(h, carrier, lines: [Line(1260m, awb: "AWB-77123")]);

        var result = await h.Matching.MatchInvoiceAsync(invoice, User);

        result!.Matched.Should().Be(1);
        result.IsComplete.Should().BeTrue();

        var line = result.Lines[0];
        line.MatchStatus.Should().Be("MATCHED");
        line.MatchMethod.Should().Be("AWB");
        line.MatchedConsignmentNumber.Should().Be(consignment.Number);
    }

    [Theory]
    [InlineData("awb-77123")]
    [InlineData("  AWB-77123  ")]
    public async Task An_airway_bill_matches_however_the_carrier_printed_it(string onTheBill)
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier, awb: "AWB-77123");
        var invoice = await NewInvoice(h, carrier, lines: [Line(1260m, awb: onTheBill)]);

        (await h.Matching.MatchInvoiceAsync(invoice, User))!.Matched.Should().Be(1);
    }

    [Fact]
    public async Task Several_lines_may_charge_one_movement()
    {
        // Base carriage and fuel are two lines for one movement. Matching is not one-to-one, and
        // enforcing that it were would make every real bill unmatchable.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier, awb: "AWB-1");
        var invoice = await NewInvoice(h, carrier, total: 1260m, lines:
        [
            Line(1125m, awb: "AWB-1"),
            Line(135m,  awb: "AWB-1", description: "Fuel surcharge", chargeCode: "FUEL")
        ]);

        var result = await h.Matching.MatchInvoiceAsync(invoice, User);

        result!.Matched.Should().Be(2);
        result.Lines.Select(l => l.MatchedConsignmentNumber).Distinct().Should().ContainSingle();
    }

    // ── Falling back to our own reference ─────────────────────────────────────

    [Fact]
    public async Task A_line_with_no_airway_bill_falls_back_to_our_consignment_number()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, awb: null);
        var invoice = await NewInvoice(h, carrier, lines: [Line(1260m, reference: consignment.Number)]);

        var line = (await h.Matching.MatchInvoiceAsync(invoice, User))!.Lines[0];

        line.MatchStatus.Should().Be("MATCHED");
        line.MatchMethod.Should().Be("REFERENCE");
    }

    [Fact]
    public async Task The_airway_bill_wins_over_our_own_reference()
    {
        // It is the carrier's own identifier and the only one both sides always have; ours is
        // whatever the carrier chose to echo back.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var byAwb    = await NewConsignment(h, carrier, awb: "AWB-1");
        var byNumber = await NewConsignment(h, carrier, awb: "AWB-2");

        var invoice = await NewInvoice(h, carrier, lines:
            [Line(1260m, awb: "AWB-1", reference: byNumber.Number)]);

        var line = (await h.Matching.MatchInvoiceAsync(invoice, User))!.Lines[0];

        line.MatchMethod.Should().Be("AWB");
        line.MatchedConsignmentNumber.Should().Be(byAwb.Number);
    }

    // ── Refusing to guess ─────────────────────────────────────────────────────

    [Fact]
    public async Task Two_movements_on_one_airway_bill_leave_the_line_for_a_person()
    {
        // Picking either would settle a charge against a movement nobody checked.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var first  = await NewConsignment(h, carrier, awb: "AWB-DUP");
        var second = await NewConsignment(h, carrier, awb: "AWB-DUP");

        var invoice = await NewInvoice(h, carrier, lines: [Line(1260m, awb: "AWB-DUP")]);

        var result = await h.Matching.MatchInvoiceAsync(invoice, User);

        result!.Ambiguous.Should().Be(1);
        result.IsComplete.Should().BeFalse();
        result.Lines[0].MatchedConsignmentUuid.Should().BeNull();
        result.Lines[0].MatchNote.Should()
            .Contain("2 consignments carry airway bill").And
            .Contain(first.Number).And.Contain(second.Number);
    }

    [Fact]
    public async Task An_airway_bill_belonging_to_another_carrier_is_named_as_such()
    {
        // Either the bill is wrong or the consignment is, and matching across carriers would hide
        // whichever it is behind a tidy-looking result.
        var h = NewHarness();
        var billing = await NewCarrier(h, "Beta Road");
        var other   = await NewCarrier(h, "Alpha Air");

        await NewConsignment(h, other, awb: "AWB-ALPHA");

        var invoice = await NewInvoice(h, billing, lines: [Line(1260m, awb: "AWB-ALPHA")]);

        var line = (await h.Matching.MatchInvoiceAsync(invoice, User))!.Lines[0];

        line.MatchStatus.Should().Be("AMBIGUOUS");
        line.MatchNote.Should().Contain("Alpha Air").And.Contain("not by the carrier that sent this bill");
    }

    [Fact]
    public async Task A_line_matching_nothing_says_which_kind_of_nothing()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier, awb: "AWB-1");

        var invoice = await NewInvoice(h, carrier, total: 900m, lines:
        [
            Line(500m, awb: "AWB-NOT-OURS"),
            Line(400m, description: "Monthly account fee", chargeCode: "ADMIN")
        ]);

        var result = await h.Matching.MatchInvoiceAsync(invoice, User);

        result!.Unmatched.Should().Be(2);
        result.Lines[0].MatchNote.Should().Contain("Nothing this carrier moved matches");
        result.Lines[1].MatchNote.Should().Contain("neither an airway bill nor a consignment reference");
    }

    // ── Working the queue ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_queue_holds_everything_still_needing_a_person_largest_first()
    {
        // The biggest unexplained charge is the one worth an hour of somebody's time, and a queue
        // ordered by date buries it.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier, awb: "AWB-1");

        var invoice = await NewInvoice(h, carrier, total: 8360m, lines:
        [
            Line(1260m, awb: "AWB-1"),                                  // matches
            Line(100m,  description: "Small mystery", chargeCode: "X"), // does not
            Line(7000m, description: "Large mystery", chargeCode: "Y")  // does not
        ]);

        await h.Matching.MatchInvoiceAsync(invoice, User);

        var queue = await h.Matching.GetQueueAsync(new UnmatchedLineFilter());

        queue.TotalRecords.Should().Be(2);
        queue.Data[0].Description.Should().Be("Large mystery");
        queue.Data[0].Amount.Should().Be(7000m);
    }

    [Fact]
    public async Task The_queue_can_be_narrowed_to_one_carrier_or_one_bill()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        var air  = await NewCarrier(h, "Alpha Air");

        var roadInvoice = await NewInvoice(h, road, "R-1", 500m, Line(500m, description: "Mystery"));
        await NewInvoice(h, air, "A-1", 500m, Line(500m, description: "Mystery"));

        await h.Matching.MatchInvoiceAsync(roadInvoice, User);

        (await h.Matching.GetQueueAsync(new UnmatchedLineFilter { CarrierUuid = road }))
            .TotalRecords.Should().Be(1);

        (await h.Matching.GetQueueAsync(new UnmatchedLineFilter { InvoiceUuid = roadInvoice }))
            .TotalRecords.Should().Be(1);
    }

    [Fact]
    public async Task A_withdrawn_bills_lines_leave_the_queue()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var invoice = await NewInvoice(h, carrier, lines: [Line(1260m, description: "Mystery")]);
        await h.Matching.MatchInvoiceAsync(invoice, User);

        (await h.Matching.GetQueueAsync(new UnmatchedLineFilter())).TotalRecords.Should().Be(1);

        await h.Invoices.CancelAsync(invoice, new CancelCarrierInvoiceRequest { Reason = "Duplicate" }, User);

        (await h.Matching.GetQueueAsync(new UnmatchedLineFilter())).TotalRecords.Should().Be(0);
    }

    // ── Deciding by hand ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_line_can_be_matched_by_hand_with_a_note()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, awb: "AWB-1");
        var invoice = await NewInvoice(h, carrier, lines: [Line(1260m, description: "Mystery")]);

        await h.Matching.MatchInvoiceAsync(invoice, User);

        var lineUuid = (await h.Matching.GetMatchesAsync(invoice))!.Lines[0].LineUuid;

        (await h.Matching.MatchLineAsync(lineUuid, new MatchLineRequest
        {
            ConsignmentUuid = consignment.Uuid,
            Note = "Carrier confirmed by email — their reference was mis-keyed."
        }, User)).Should().BeTrue();

        var line = (await h.Matching.GetMatchesAsync(invoice))!.Lines[0];
        line.MatchStatus.Should().Be("MATCHED");
        line.MatchMethod.Should().Be("MANUAL");
        line.MatchNote.Should().Contain("mis-keyed");
    }

    [Fact]
    public async Task A_match_made_by_hand_needs_to_say_what_it_is_based_on()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);
        var invoice = await NewInvoice(h, carrier, lines: [Line(1260m, description: "Mystery")]);
        await h.Matching.MatchInvoiceAsync(invoice, User);

        var lineUuid = (await h.Matching.GetMatchesAsync(invoice))!.Lines[0].LineUuid;

        var act = async () => await h.Matching.MatchLineAsync(
            lineUuid, new MatchLineRequest { ConsignmentUuid = consignment.Uuid, Note = "  " }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*weaker claim than matching on an airway bill*");
    }

    [Fact]
    public async Task Matching_across_carriers_is_refused_even_by_hand()
    {
        // A bill from one carrier charging another's movement is wrong however confident somebody
        // is about it.
        var h = NewHarness();
        var billing = await NewCarrier(h, "Beta Road");
        var other   = await NewCarrier(h, "Alpha Air");
        var theirs  = await NewConsignment(h, other);

        var invoice = await NewInvoice(h, billing, lines: [Line(1260m, description: "Mystery")]);
        await h.Matching.MatchInvoiceAsync(invoice, User);

        var lineUuid = (await h.Matching.GetMatchesAsync(invoice))!.Lines[0].LineUuid;

        var act = async () => await h.Matching.MatchLineAsync(
            lineUuid, new MatchLineRequest { ConsignmentUuid = theirs.Uuid, Note = "I checked" }, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*not carried by the carrier that sent this bill*");
    }

    [Fact]
    public async Task A_line_that_is_not_a_movement_charge_can_be_set_aside_with_a_reason()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var invoice = await NewInvoice(h, carrier, lines: [Line(1260m, description: "Monthly account fee")]);
        await h.Matching.MatchInvoiceAsync(invoice, User);

        var lineUuid = (await h.Matching.GetMatchesAsync(invoice))!.Lines[0].LineUuid;

        (await h.Matching.ExcludeLineAsync(lineUuid, new ExcludeLineRequest
        {
            Reason = "Monthly account charge, not carriage."
        }, User)).Should().BeTrue();

        var result = await h.Matching.GetMatchesAsync(invoice);
        result!.Excluded.Should().Be(1);
        result.IsComplete.Should().BeTrue("nothing is left for a person to do");

        (await h.Matching.GetQueueAsync(new UnmatchedLineFilter())).TotalRecords.Should().Be(0);
    }

    [Fact]
    public async Task Setting_a_line_aside_needs_a_reason()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var invoice = await NewInvoice(h, carrier, lines: [Line(1260m, description: "Mystery")]);
        await h.Matching.MatchInvoiceAsync(invoice, User);

        var lineUuid = (await h.Matching.GetMatchesAsync(invoice))!.Lines[0].LineUuid;

        var act = async () => await h.Matching.ExcludeLineAsync(
            lineUuid, new ExcludeLineRequest { Reason = " " }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*a charge nobody ever explained*");
    }

    [Fact]
    public async Task A_match_can_be_undone_and_the_line_goes_back_to_the_queue()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier, awb: "AWB-1");
        var invoice = await NewInvoice(h, carrier, lines: [Line(1260m, awb: "AWB-1")]);
        await h.Matching.MatchInvoiceAsync(invoice, User);

        var lineUuid = (await h.Matching.GetMatchesAsync(invoice))!.Lines[0].LineUuid;

        await h.Matching.UnmatchLineAsync(
            lineUuid, new ExcludeLineRequest { Reason = "Wrong movement — the AWB was reused." }, User);

        var line = (await h.Matching.GetMatchesAsync(invoice))!.Lines[0];
        line.MatchStatus.Should().Be("UNMATCHED");
        line.MatchedConsignmentUuid.Should().BeNull();

        (await h.Matching.GetQueueAsync(new UnmatchedLineFilter())).TotalRecords.Should().Be(1);
    }

    [Fact]
    public async Task Re_running_the_match_never_undoes_a_decision_somebody_made()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, awb: "AWB-1");
        var invoice = await NewInvoice(h, carrier, total: 1360m, lines:
        [
            Line(1260m, awb: "AWB-1"),
            Line(100m,  description: "Account fee", chargeCode: "ADMIN")
        ]);

        await h.Matching.MatchInvoiceAsync(invoice, User);

        var fee = (await h.Matching.GetMatchesAsync(invoice))!.Lines[1].LineUuid;
        await h.Matching.ExcludeLineAsync(fee, new ExcludeLineRequest { Reason = "Not carriage" }, User);

        await h.Matching.MatchInvoiceAsync(invoice, User);

        var result = await h.Matching.GetMatchesAsync(invoice);
        result!.Excluded.Should().Be(1, "the exclusion survived a re-run");
        result.Matched.Should().Be(1);
        _ = consignment;
    }

    // ── Candidates ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Candidates_are_offered_with_a_reason_each()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var exact = await NewConsignment(h, carrier, awb: "AWB-77123");
        await NewConsignment(h, carrier, awb: "AWB-99999");

        var invoice = await NewInvoice(h, carrier, lines: [Line(1260m, awb: "AWB-77123")]);
        await h.Matching.MatchInvoiceAsync(invoice, User);

        var lineUuid = (await h.Matching.GetMatchesAsync(invoice))!.Lines[0].LineUuid;

        var candidates = await h.Matching.GetCandidatesAsync(lineUuid);

        candidates.Should().ContainSingle();
        candidates[0].ConsignmentNumber.Should().Be(exact.Number);
        candidates[0].Reason.Should().Be("The airway bill matches.");
        candidates[0].FreightCost.Should().Be(1260m);
    }

    [Fact]
    public async Task Candidates_never_cross_carriers()
    {
        var h = NewHarness();
        var billing = await NewCarrier(h, "Beta Road");
        var other   = await NewCarrier(h, "Alpha Air");
        await NewConsignment(h, other, awb: "AWB-77123");

        var invoice = await NewInvoice(h, billing, lines: [Line(1260m, awb: "AWB-77123")]);
        await h.Matching.MatchInvoiceAsync(invoice, User);

        var lineUuid = (await h.Matching.GetMatchesAsync(invoice))!.Lines[0].LineUuid;

        (await h.Matching.GetCandidatesAsync(lineUuid)).Should().BeEmpty();
    }

    // ── Charged twice ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_movement_charged_on_two_bills_is_flagged_rather_than_refused()
    {
        // A carrier may legitimately bill carriage and a surcharge separately and weeks apart. A
        // double charge looks exactly the same until somebody checks.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier, awb: "AWB-1");

        var first  = await NewInvoice(h, carrier, "INV-1", 1260m, Line(1260m, awb: "AWB-1"));
        var second = await NewInvoice(h, carrier, "INV-2", 1260m, Line(1260m, awb: "AWB-1"));

        await h.Matching.MatchInvoiceAsync(first, User);
        var result = await h.Matching.MatchInvoiceAsync(second, User);

        result!.Matched.Should().Be(1, "it is still matched");
        result.Lines[0].DuplicateWarning.Should().Contain("Also charged on INV-1");
        result.Warnings.Should().ContainMatch("*already charged on another*");
    }

    [Fact]
    public async Task A_withdrawn_bill_does_not_count_as_a_duplicate_charge()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier, awb: "AWB-1");

        var first  = await NewInvoice(h, carrier, "INV-1", 1260m, Line(1260m, awb: "AWB-1"));
        await h.Matching.MatchInvoiceAsync(first, User);
        await h.Invoices.CancelAsync(first, new CancelCarrierInvoiceRequest { Reason = "Duplicate" }, User);

        var second = await NewInvoice(h, carrier, "INV-2", 1260m, Line(1260m, awb: "AWB-1"));
        var result = await h.Matching.MatchInvoiceAsync(second, User);

        result!.Lines[0].DuplicateWarning.Should().BeNull();
    }

    // ── Not found, and not yours ──────────────────────────────────────────────

    [Fact]
    public async Task A_withdrawn_bill_cannot_be_matched()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var invoice = await NewInvoice(h, carrier);
        await h.Invoices.CancelAsync(invoice, new CancelCarrierInvoiceRequest { Reason = "Wrong" }, User);

        var act = async () => await h.Matching.MatchInvoiceAsync(invoice, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*nothing to match it against*");
    }

    [Fact]
    public async Task An_unknown_bill_or_line_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.Matching.MatchInvoiceAsync(Guid.NewGuid(), User)).Should().BeNull();
        (await h.Matching.GetMatchesAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Matching.GetCandidatesAsync(Guid.NewGuid())).Should().BeEmpty();
        (await h.Matching.MatchLineAsync(
            Guid.NewGuid(), new MatchLineRequest { ConsignmentUuid = Guid.NewGuid(), Note = "x" }, User))
            .Should().BeFalse();
        (await h.Matching.ExcludeLineAsync(
            Guid.NewGuid(), new ExcludeLineRequest { Reason = "x" }, User)).Should().BeFalse();
    }

    [Fact]
    public async Task Another_organizations_lines_do_not_exist_here()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var invoice = await NewInvoice(h, carrier);

        var otherDb = LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid());
        var other   = new InvoiceMatchingService(otherDb);

        (await other.GetMatchesAsync(invoice)).Should().BeNull();
        (await other.GetQueueAsync(new UnmatchedLineFilter())).TotalRecords.Should().Be(0);
    }
}
