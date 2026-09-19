using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Settlement;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Settlement;

// T-53 — bills from carriers: the third leg of the three-way match.
public class CarrierInvoiceTests
{
    private const int User = 42;
    private static readonly DateTime Issued = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(LogisticsDbContext Db, CarrierInvoiceService Invoices, string DbName);

    private static Harness NewHarness()
    {
        var (db, _, dbName) = LogisticsTestDb.New();
        return new Harness(db, new CarrierInvoiceService(db), dbName);
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

    private static CarrierInvoiceLineRequest Line(
        decimal amount, string description = "Carriage", string? awb = "AWB-0001",
        string? chargeCode = "BASE", decimal? weight = null) =>
        new()
        {
            Description = description, AwbNumber = awb, ChargeCode = chargeCode,
            ChargeableWeightKg = weight, Amount = amount
        };

    private static CreateCarrierInvoiceRequest InvoiceReq(
        Guid carrier, string number = "INV-9001", decimal total = 1260m,
        string currency = "PKR", DateTime? issued = null, DateTime? due = null,
        params CarrierInvoiceLineRequest[] lines) =>
        new()
        {
            CarrierUuid = carrier, InvoiceNumber = number, Currency = currency,
            InvoiceDate = issued ?? Issued, DueDate = due, TotalAmount = total,
            Lines = [.. lines.Length > 0 ? lines : [Line(1125m), Line(135m, "Fuel surcharge", chargeCode: "FUEL")]]
        };

    // ── Recording a bill ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_bill_is_recorded_with_its_lines()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var uuid = await h.Invoices.CreateAsync(InvoiceReq(carrier), User);

        var invoice = await h.Invoices.GetByUuidAsync(uuid);

        invoice!.InvoiceNumber.Should().Be("INV-9001");
        invoice.CarrierName.Should().Be("Beta Road");
        invoice.Currency.Should().Be("PKR");
        invoice.TotalAmount.Should().Be(1260m);
        invoice.LineTotal.Should().Be(1260m);
        invoice.Status.Should().Be("RECEIVED");
        invoice.Source.Should().Be("KEYED");
        invoice.Lines.Should().HaveCount(2);
        invoice.Lines[0].LineNo.Should().Be(1);
        invoice.Lines[1].ChargeCode.Should().Be("FUEL");
    }

    [Fact]
    public async Task A_bill_whose_lines_do_not_add_up_to_its_own_total_is_refused()
    {
        // Matching line by line against a header that says something else is how a discrepancy
        // gets absorbed without anybody seeing it.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Invoices.CreateAsync(
            InvoiceReq(carrier, total: 2000m, lines: [Line(1125m), Line(135m)]), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*lines come to PKR 1,260.00*")
            .WithMessage("*bill says PKR 2,000.00*")
            .WithMessage("*hide the difference rather than find it*");
    }

    [Fact]
    public async Task A_cent_of_rounding_is_tolerated()
    {
        // A carrier rounding its own total is ordinary. Anything larger is a keying error.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Invoices.CreateAsync(
            InvoiceReq(carrier, total: 1260.01m, lines: [Line(1125m), Line(135m)]), User);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task The_same_bill_cannot_be_recorded_twice()
    {
        // Keying it twice is how it gets paid twice, and the duplicate looks exactly as legitimate.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Invoices.CreateAsync(InvoiceReq(carrier, "INV-9001"), User);

        var act = async () => await h.Invoices.CreateAsync(InvoiceReq(carrier, " INV-9001 "), User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*already recorded*")
            .WithMessage("*Paying the same bill twice*");
    }

    [Fact]
    public async Task Two_carriers_may_use_the_same_invoice_number()
    {
        var h = NewHarness();
        var one = await NewCarrier(h, "Beta Road");
        var two = await NewCarrier(h, "Alpha Air");

        await h.Invoices.CreateAsync(InvoiceReq(one, "0001"), User);

        var act = async () => await h.Invoices.CreateAsync(InvoiceReq(two, "0001"), User);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_bill_with_no_lines_is_refused()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var req = InvoiceReq(carrier);
        req.Lines = [];

        var act = async () => await h.Invoices.CreateAsync(req, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*cannot be matched against anything*");
    }

    [Fact]
    public async Task A_line_that_charges_nothing_is_refused()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Invoices.CreateAsync(
            InvoiceReq(carrier, total: 1125m, lines: [Line(1125m), Line(0m)]), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*Line 2 charges nothing*");
    }

    [Fact]
    public async Task A_credit_line_is_allowed_because_carriers_issue_them()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var uuid = await h.Invoices.CreateAsync(
            InvoiceReq(carrier, total: 1060m, lines: [Line(1260m), Line(-200m, "Credit — re-weigh")]),
            User);

        var invoice = await h.Invoices.GetByUuidAsync(uuid);

        invoice!.TotalAmount.Should().Be(1060m);
        invoice.Lines[1].Amount.Should().Be(-200m);
    }

    [Fact]
    public async Task A_bill_for_nothing_is_not_a_bill()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Invoices.CreateAsync(
            InvoiceReq(carrier, total: 0m, lines: [Line(200m), Line(-200m)]), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*not a bill*");
    }

    [Fact]
    public async Task A_bill_needs_a_number_a_date_a_currency_and_a_real_carrier()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var noNumber = async () => await h.Invoices.CreateAsync(InvoiceReq(carrier, "  "), User);
        (await noNumber.Should().ThrowAsync<BadRequestException>()).WithMessage("*invoice number*");

        var noDate = async () => await h.Invoices.CreateAsync(
            InvoiceReq(carrier, issued: default(DateTime)), User);
        (await noDate.Should().ThrowAsync<BadRequestException>()).WithMessage("*date the carrier issued it*");

        var badCurrency = async () => await h.Invoices.CreateAsync(
            InvoiceReq(carrier, currency: "RUPEES"), User);
        (await badCurrency.Should().ThrowAsync<BadRequestException>()).WithMessage("*three-letter ISO*");

        var noCarrier = async () => await h.Invoices.CreateAsync(InvoiceReq(Guid.NewGuid()), User);
        await noCarrier.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_bill_cannot_fall_due_before_it_was_issued()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Invoices.CreateAsync(
            InvoiceReq(carrier, issued: Issued, due: Issued.AddDays(-1)), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*before it was issued*");
    }

    [Fact]
    public async Task The_carriers_own_references_are_kept_as_given()
    {
        // The airway bill is what matching hangs off, and the weight the carrier says it billed on
        // is the most common reason a freight bill turns out to be wrong.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var uuid = await h.Invoices.CreateAsync(InvoiceReq(carrier, total: 1260m, lines:
        [
            new CarrierInvoiceLineRequest
            {
                Description = "Carriage", AwbNumber = " AWB-77123 ",
                ConsignmentReference = "SHP-2026-00042", ChargeCode = "base",
                ServiceCode = "ROAD", ChargeableWeightKg = 14m,
                ShipDate = Issued, Amount = 1260m
            }
        ]), User);

        var line = (await h.Invoices.GetByUuidAsync(uuid))!.Lines[0];

        line.AwbNumber.Should().Be("AWB-77123");
        line.ConsignmentReference.Should().Be("SHP-2026-00042");
        line.ChargeCode.Should().Be("BASE", "charge codes are compared, so they are normalised");
        line.ChargeableWeightKg.Should().Be(14m);
    }

    // ── Bringing bills in in bulk ─────────────────────────────────────────────

    [Fact]
    public async Task An_import_judges_every_bill_on_its_own()
    {
        // One bad bill never fails the batch: an import that stopped at the first problem would
        // leave somebody re-running it and re-importing everything before it.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var result = await h.Invoices.ImportAsync(new ImportCarrierInvoicesRequest
        {
            Invoices =
            [
                InvoiceReq(carrier, "INV-1"),
                InvoiceReq(carrier, "INV-2", total: 9999m),          // does not add up
                InvoiceReq(carrier, "INV-3")
            ]
        }, User);

        result.Accepted.Should().Be(2);
        result.Refused.Should().Be(1);

        result.Results[1].Accepted.Should().BeFalse();
        result.Results[1].InvoiceNumber.Should().Be("INV-2");
        result.Results[1].Reason.Should().Contain("do not add up");

        result.Results[0].UUID.Should().NotBeNull();
    }

    [Fact]
    public async Task An_import_refuses_a_duplicate_without_losing_the_rest()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await h.Invoices.CreateAsync(InvoiceReq(carrier, "INV-1"), User);

        var result = await h.Invoices.ImportAsync(new ImportCarrierInvoicesRequest
        {
            Invoices = [InvoiceReq(carrier, "INV-1"), InvoiceReq(carrier, "INV-2")]
        }, User);

        result.Accepted.Should().Be(1);
        result.Refused.Should().Be(1);
        result.Results[0].Reason.Should().Contain("already recorded");
    }

    [Fact]
    public async Task An_imported_bill_is_marked_as_imported()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var result = await h.Invoices.ImportAsync(new ImportCarrierInvoicesRequest
        {
            Invoices = [InvoiceReq(carrier, "INV-1")]
        }, User);

        (await h.Invoices.GetByUuidAsync(result.Results[0].UUID!.Value))!.Source.Should().Be("IMPORT");
    }

    [Fact]
    public async Task An_empty_import_does_nothing_rather_than_failing()
    {
        var h = NewHarness();

        var result = await h.Invoices.ImportAsync(new ImportCarrierInvoicesRequest(), User);

        result.Accepted.Should().Be(0);
        result.Refused.Should().Be(0);
    }

    // ── Listing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bills_list_newest_first_and_can_be_narrowed()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        var air  = await NewCarrier(h, "Alpha Air");

        await h.Invoices.CreateAsync(InvoiceReq(road, "OLD", issued: Issued), User);
        await h.Invoices.CreateAsync(InvoiceReq(road, "NEW", issued: Issued.AddDays(10)), User);
        await h.Invoices.CreateAsync(InvoiceReq(air,  "AIR", issued: Issued.AddDays(5)), User);

        var all = await h.Invoices.GetListAsync(new CarrierInvoiceFilter());
        all.TotalRecords.Should().Be(3);
        all.Data[0].InvoiceNumber.Should().Be("NEW");

        var byCarrier = await h.Invoices.GetListAsync(new CarrierInvoiceFilter { CarrierUuid = air });
        byCarrier.Data.Should().ContainSingle().Which.InvoiceNumber.Should().Be("AIR");

        var byDate = await h.Invoices.GetListAsync(new CarrierInvoiceFilter { From = Issued.AddDays(7) });
        byDate.Data.Should().ContainSingle().Which.InvoiceNumber.Should().Be("NEW");
    }

    [Fact]
    public async Task A_bill_can_be_found_by_the_airway_bill_on_one_of_its_lines()
    {
        // Somebody chasing one parcel's charge has the airway bill, not the carrier's invoice number.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Invoices.CreateAsync(
            InvoiceReq(carrier, "INV-1", total: 500m, lines: [Line(500m, awb: "AWB-55500")]), User);
        await h.Invoices.CreateAsync(
            InvoiceReq(carrier, "INV-2", total: 500m, lines: [Line(500m, awb: "AWB-99999")]), User);

        var found = await h.Invoices.GetListAsync(new CarrierInvoiceFilter { Search = "55500" });

        found.Data.Should().ContainSingle().Which.InvoiceNumber.Should().Be("INV-1");
    }

    // ── Correcting ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sending_lines_replaces_them_all_and_is_checked_against_the_total()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var uuid = await h.Invoices.CreateAsync(InvoiceReq(carrier), User);

        await h.Invoices.PatchAsync(uuid, new PatchCarrierInvoiceRequest
        {
            TotalAmount = 900m,
            Lines = [Line(900m, "Carriage, re-rated")]
        }, User);

        var invoice = await h.Invoices.GetByUuidAsync(uuid);

        invoice!.TotalAmount.Should().Be(900m);
        invoice.Lines.Should().ContainSingle().Which.Description.Should().Be("Carriage, re-rated");
    }

    [Fact]
    public async Task A_header_cannot_be_edited_away_from_its_own_lines()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var uuid = await h.Invoices.CreateAsync(InvoiceReq(carrier), User);

        var act = async () => await h.Invoices.PatchAsync(
            uuid, new PatchCarrierInvoiceRequest { TotalAmount = 5000m }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*Change the lines too*");
    }

    [Fact]
    public async Task A_replacement_set_of_lines_is_validated_like_a_new_one()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var uuid = await h.Invoices.CreateAsync(InvoiceReq(carrier), User);

        var act = async () => await h.Invoices.PatchAsync(uuid, new PatchCarrierInvoiceRequest
        {
            Lines = [Line(50m)]     // nowhere near the stored total
        }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    // ── Withdrawing ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_bill_can_be_withdrawn_with_a_reason()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var uuid = await h.Invoices.CreateAsync(InvoiceReq(carrier), User);

        (await h.Invoices.CancelAsync(uuid, new CancelCarrierInvoiceRequest
        {
            Reason = "Duplicate of INV-9000, sent twice by the carrier."
        }, User)).Should().BeTrue();

        var invoice = await h.Invoices.GetByUuidAsync(uuid);
        invoice!.Status.Should().Be("CANCELLED");
        invoice.CancelReason.Should().Contain("Duplicate");
    }

    [Fact]
    public async Task A_bill_cannot_be_withdrawn_without_a_reason()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var uuid = await h.Invoices.CreateAsync(InvoiceReq(carrier), User);

        var act = async () => await h.Invoices.CancelAsync(
            uuid, new CancelCarrierInvoiceRequest { Reason = " " }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*when the carrier chases it*");
    }

    [Fact]
    public async Task A_withdrawn_bill_cannot_be_edited_or_withdrawn_again()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var uuid = await h.Invoices.CreateAsync(InvoiceReq(carrier), User);
        await h.Invoices.CancelAsync(uuid, new CancelCarrierInvoiceRequest { Reason = "Duplicate" }, User);

        var edit = async () => await h.Invoices.PatchAsync(
            uuid, new PatchCarrierInvoiceRequest { Notes = "Late note" }, User);
        (await edit.Should().ThrowAsync<ConflictException>()).WithMessage("*Record a fresh bill*");

        var again = async () => await h.Invoices.CancelAsync(
            uuid, new CancelCarrierInvoiceRequest { Reason = "Again" }, User);
        await again.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task A_withdrawn_bill_keeps_its_number_reserved()
    {
        // The carrier has not reissued the number, so recording it again would be a second copy of
        // the same bill rather than a correction.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var uuid = await h.Invoices.CreateAsync(InvoiceReq(carrier, "INV-9001"), User);
        await h.Invoices.CancelAsync(uuid, new CancelCarrierInvoiceRequest { Reason = "Wrong carrier" }, User);

        var act = async () => await h.Invoices.CreateAsync(InvoiceReq(carrier, "INV-9001"), User);

        await act.Should().ThrowAsync<ConflictException>();
    }

    // ── Not found, and not yours ──────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_bill_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.Invoices.GetByUuidAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Invoices.PatchAsync(Guid.NewGuid(), new PatchCarrierInvoiceRequest(), User)).Should().BeFalse();
        (await h.Invoices.CancelAsync(
            Guid.NewGuid(), new CancelCarrierInvoiceRequest { Reason = "x" }, User)).Should().BeFalse();
    }

    [Fact]
    public async Task Another_organizations_bills_do_not_exist_here()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var uuid = await h.Invoices.CreateAsync(InvoiceReq(carrier), User);

        var otherDb = LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid());
        var other   = new CarrierInvoiceService(otherDb);

        (await other.GetByUuidAsync(uuid)).Should().BeNull();
        (await other.GetListAsync(new CarrierInvoiceFilter())).TotalRecords.Should().Be(0);
    }

    [Fact]
    public async Task Line_numbers_are_assigned_in_order_and_are_unique()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var uuid = await h.Invoices.CreateAsync(InvoiceReq(carrier, total: 600m,
            lines: [Line(100m, "A"), Line(200m, "B"), Line(300m, "C")]), User);

        (await h.Invoices.GetByUuidAsync(uuid))!.Lines.Select(l => l.LineNo)
            .Should().Equal([1, 2, 3]);

        (await h.Db.CarrierInvoiceLines.CountAsync()).Should().Be(3);
    }
}
