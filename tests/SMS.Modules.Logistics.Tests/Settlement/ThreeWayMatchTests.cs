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

// T-55 — quoted, agreed at booking, and billed, compared side by side.
public class ThreeWayMatchTests
{
    private const int User = 42;
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(
        LogisticsDbContext Db,
        CarrierInvoiceService Invoices,
        InvoiceMatchingService Matching,
        FreightAccrualService Accruals,
        ThreeWayMatchService ThreeWay,
        string DbName);

    private static Harness NewHarness(decimal? tolerancePercent = null, decimal? toleranceAmount = null)
    {
        var (db, _, dbName) = LogisticsTestDb.New();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logistics:Settlement:TolerancePercent"] = tolerancePercent?.ToString(),
            ["Logistics:Settlement:ToleranceAmount"]  = toleranceAmount?.ToString()
        }).Build();

        return new Harness(
            db, new CarrierInvoiceService(db), new InvoiceMatchingService(db),
            new FreightAccrualService(db), new ThreeWayMatchService(db, config), dbName);
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

    /// <summary>A dispatched consignment quoted at 1,260 PKR with BASE and FUEL charge lines.</summary>
    private static async Task<Guid> NewConsignment(
        Harness h, Guid carrier, string awb = "AWB-1", decimal? quoted = 1260m,
        decimal? ratedWeight = 12.5m, string? service = "ROAD", string currency = "PKR",
        params string[] chargeCodes)
    {
        var carrierRow = await h.Db.Carriers.AsNoTracking().SingleAsync(c => c.UUID == carrier);

        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CarrierId = carrierRow.Id, CarrierName = carrierRow.Name,
            MasterAwb = awb, Status = "IN_TRANSIT",
            FreightCost = quoted, FreightCurrency = quoted is null ? null : currency,
            FreightRateSource = quoted is null ? null : "CARRIER",
            RatedChargeableWeightKg = ratedWeight, RatedServiceCode = service,
            ActualDispatchAt = T0, CreatedBy = User, CreatedDate = T0
        };

        foreach (var code in chargeCodes.Length > 0 ? chargeCodes : ["BASE", "FUEL"])
            consignment.Charges.Add(new ConsignmentCharge
            {
                UUID = Guid.NewGuid(), Code = code, Amount = 0m,
                Sequence = consignment.Charges.Count, CreatedBy = User, CreatedDate = T0
            });

        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return consignment.UUID;
    }

    /// <summary>What the carrier agreed when it accepted the booking.</summary>
    private static async Task BookedAt(Harness h, Guid consignmentUuid, decimal cost, string currency = "PKR")
    {
        var consignment = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == consignmentUuid);

        h.Db.CarrierCommands.Add(new CarrierCommand
        {
            UUID = Guid.NewGuid(), ConsignmentId = consignment.Id,
            CommandType = LogisticsCode.Of(CarrierCommandType.Book),
            Status = LogisticsCode.Of(CarrierCommandStatus.Succeeded),
            IdempotencyKey = Guid.NewGuid().ToString("N"), RequestFingerprint = "fp",
            ProviderKey = "SCRIPTED", Cost = cost, CostCurrency = currency,
            AttemptCount = 1, FirstAttemptAt = T0, LastAttemptAt = T0, CompletedAt = T0,
            CreatedBy = User, CreatedDate = T0
        });

        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    private static CarrierInvoiceLineRequest Line(
        decimal amount, string awb = "AWB-1", string? chargeCode = "BASE",
        decimal? weight = null, string? service = null, string description = "Carriage") =>
        new()
        {
            Description = description, AwbNumber = awb, ChargeCode = chargeCode,
            ChargeableWeightKg = weight, ServiceCode = service, Amount = amount
        };

    /// <summary>Records a bill and ties its lines to movements, ready to compare.</summary>
    private static async Task<Guid> NewMatchedInvoice(
        Harness h, Guid carrier, decimal total, string number = "INV-1", string currency = "PKR",
        params CarrierInvoiceLineRequest[] lines)
    {
        var uuid = await h.Invoices.CreateAsync(new CreateCarrierInvoiceRequest
        {
            CarrierUuid = carrier, InvoiceNumber = number, Currency = currency,
            InvoiceDate = T0, TotalAmount = total,
            Lines = [.. lines.Length > 0 ? lines : [Line(total)]]
        }, User);

        await h.Matching.MatchInvoiceAsync(uuid, User);

        return uuid;
    }

    // ── Agreeing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_bill_that_matches_what_was_expected_comes_back_clean()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);
        await h.Accruals.AccrueAsync(consignment, User);

        var invoice = await NewMatchedInvoice(h, carrier, 1260m);

        var result = await h.ThreeWay.MatchAsync(invoice, User);

        result!.IsClean.Should().BeTrue();
        result.Status.Should().Be("MATCHED");
        result.WithinTolerance.Should().Be(1);
        result.VarianceTotal.Should().Be(0m);
        result.Consignments[0].Outcome.Should().Be("WITHIN_TOLERANCE");
        result.Consignments[0].VarianceReason.Should().BeNull();
    }

    [Fact]
    public async Task What_the_carrier_agreed_at_booking_beats_what_we_were_quoted()
    {
        // The same precedence pricing used, for the same reason: the carrier's own word beats our
        // estimate of it.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, quoted: 1260m);
        await BookedAt(h, consignment, 1310m);
        await h.Accruals.AccrueAsync(consignment, User);

        var invoice = await NewMatchedInvoice(h, carrier, 1310m);

        var compared = (await h.ThreeWay.MatchAsync(invoice, User))!.Consignments[0];

        compared.ExpectedBasis.Should().Be("BOOKED");
        compared.ExpectedAmount.Should().Be(1310m);
        compared.QuotedAmount.Should().Be(1260m);
        compared.Outcome.Should().Be("WITHIN_TOLERANCE");
    }

    [Fact]
    public async Task The_quote_is_used_when_the_carrier_said_nothing_at_booking()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);
        await h.Accruals.AccrueAsync(consignment, User);

        var invoice = await NewMatchedInvoice(h, carrier, 1260m);

        (await h.ThreeWay.MatchAsync(invoice, User))!.Consignments[0]
            .ExpectedBasis.Should().Be("QUOTED");
    }

    [Fact]
    public async Task A_difference_inside_the_tolerance_is_accepted()
    {
        var h = NewHarness(tolerancePercent: 2m);
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, quoted: 1000m);
        await h.Accruals.AccrueAsync(consignment, User);

        var invoice = await NewMatchedInvoice(h, carrier, 1019m);   // 1.9%

        var result = await h.ThreeWay.MatchAsync(invoice, User);

        result!.IsClean.Should().BeTrue();
        result.Consignments[0].VariancePercent.Should().Be(1.9m);
    }

    [Fact]
    public async Task An_absolute_tolerance_waves_through_a_small_bill_a_percentage_would_not()
    {
        // Whichever is the more generous applies: the percentage on a large bill, the absolute on a
        // small one where a percentage comes to pennies.
        var h = NewHarness(tolerancePercent: 2m, toleranceAmount: 100m);
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, quoted: 200m);
        await h.Accruals.AccrueAsync(consignment, User);

        var invoice = await NewMatchedInvoice(h, carrier, 290m);   // 45%, but only 90 out

        (await h.ThreeWay.MatchAsync(invoice, User))!.IsClean.Should().BeTrue();
    }

    // ── Disagreeing, with a reason ────────────────────────────────────────────

    [Fact]
    public async Task A_bill_over_the_tolerance_names_the_weight_the_carrier_used()
    {
        // The commonest cause by a wide margin, and T-44 stored our figure so it could be put
        // beside theirs.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, quoted: 1260m, ratedWeight: 12.5m);
        await h.Accruals.AccrueAsync(consignment, User);

        var invoice = await NewMatchedInvoice(h, carrier, 1400m, lines: [Line(1400m, weight: 14m)]);

        var result = await h.ThreeWay.MatchAsync(invoice, User);

        result!.IsClean.Should().BeFalse();
        result.Status.Should().Be("DISPUTED");

        var compared = result.Consignments[0];
        compared.Outcome.Should().Be("OVERCHARGED");
        compared.VarianceAmount.Should().Be(140m);
        compared.VarianceReason.Should().Be("WEIGHT");
        compared.VarianceNote.Should().Contain("14 kg where we made it 12.5 kg");
    }

    [Fact]
    public async Task A_surcharge_the_quote_did_not_carry_is_named()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, quoted: 1260m, chargeCodes: ["BASE", "FUEL"]);
        await h.Accruals.AccrueAsync(consignment, User);

        var invoice = await NewMatchedInvoice(h, carrier, 1560m, lines:
        [
            Line(1260m, chargeCode: "BASE"),
            Line(300m,  chargeCode: "RESIDENTIAL", description: "Residential delivery")
        ]);

        var compared = (await h.ThreeWay.MatchAsync(invoice, User))!.Consignments[0];

        compared.VarianceReason.Should().Be("SURCHARGE");
        compared.VarianceNote.Should().Contain("RESIDENTIAL").And.Contain("the quote did not");
    }

    [Fact]
    public async Task A_different_service_than_the_one_quoted_is_named()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, quoted: 1260m, service: "ROAD");
        await h.Accruals.AccrueAsync(consignment, User);

        var invoice = await NewMatchedInvoice(h, carrier, 2500m, lines:
            [Line(2500m, chargeCode: "BASE", service: "EXPRESS")]);

        var compared = (await h.ThreeWay.MatchAsync(invoice, User))!.Consignments[0];

        compared.VarianceReason.Should().Be("SERVICE");
        compared.VarianceNote.Should().Contain("EXPRESS").And.Contain("ROAD");
    }

    [Fact]
    public async Task A_difference_nothing_on_the_bill_explains_says_exactly_that()
    {
        // A guessed reason would be argued with the carrier and lost. "Nothing says why" is more
        // use than a plausible invention.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, quoted: 1260m);
        await h.Accruals.AccrueAsync(consignment, User);

        var invoice = await NewMatchedInvoice(h, carrier, 1900m, lines: [Line(1900m, chargeCode: "BASE")]);

        var compared = (await h.ThreeWay.MatchAsync(invoice, User))!.Consignments[0];

        compared.VarianceReason.Should().Be("UNEXPLAINED");
        compared.VarianceNote.Should().Contain("nothing on the bill says why");
    }

    [Fact]
    public async Task Being_billed_less_than_expected_is_reported_rather_than_celebrated()
    {
        // Usually a second bill still to come rather than a saving.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, quoted: 1260m);
        await h.Accruals.AccrueAsync(consignment, User);

        var invoice = await NewMatchedInvoice(h, carrier, 600m, lines: [Line(600m)]);

        var result = await h.ThreeWay.MatchAsync(invoice, User);

        result!.Undercharged.Should().Be(1);
        result.Consignments[0].Outcome.Should().Be("UNDERCHARGED");
        result.Warnings.Should().ContainMatch("*second bill still to come*");
    }

    // ── What cannot be compared ───────────────────────────────────────────────

    [Fact]
    public async Task A_movement_that_was_never_accrued_is_not_treated_as_agreement()
    {
        // An unaccrued charge passing silently is exactly how an unexpected bill gets paid.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);        // dispatched, never accrued

        var invoice = await NewMatchedInvoice(h, carrier, 1260m);

        var result = await h.ThreeWay.MatchAsync(invoice, User);

        result!.IsClean.Should().BeFalse();
        result.NotAccrued.Should().Be(1);
        result.Consignments[0].Outcome.Should().Be("NOT_ACCRUED");
        result.Warnings.Should().ContainMatch("*never accrued*");
    }

    [Fact]
    public async Task A_bill_in_one_currency_against_an_accrual_in_another_is_refused_not_compared()
    {
        // There is no exchange rate in this module (F42), so comparing them would be a number
        // nobody could defend.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, quoted: 1260m, currency: "PKR");
        await h.Accruals.AccrueAsync(consignment, User);

        var invoice = await NewMatchedInvoice(h, carrier, 20m, currency: "USD");

        var compared = (await h.ThreeWay.MatchAsync(invoice, User))!.Consignments[0];

        compared.Outcome.Should().Be("NOT_ACCRUED");
        compared.VarianceNote.Should().Contain("no exchange rate");
    }

    [Fact]
    public async Task Lines_nobody_could_tie_to_a_movement_are_reported_and_left_out_of_the_sums()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);
        await h.Accruals.AccrueAsync(consignment, User);

        var invoice = await NewMatchedInvoice(h, carrier, 1660m, lines:
        [
            Line(1260m),
            Line(400m, awb: "AWB-NOT-OURS", description: "Something else")
        ]);

        var result = await h.ThreeWay.MatchAsync(invoice, User);

        result!.IsClean.Should().BeFalse();
        result.UnmatchedLines.Should().Be(1);
        result.UnmatchedAmount.Should().Be(400m);
        result.ExpectedTotal.Should().Be(1260m, "the loose line is not compared against anything");
        result.Warnings.Should().ContainMatch("*not tied to a movement*");
    }

    [Fact]
    public async Task A_bill_with_nothing_tied_to_it_says_there_is_nothing_to_compare()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var invoice = await NewMatchedInvoice(h, carrier, 1260m, lines: [Line(1260m, awb: "AWB-NOWHERE")]);

        var result = await h.ThreeWay.MatchAsync(invoice, User);

        result!.ConsignmentCount.Should().Be(0);
        result.IsClean.Should().BeFalse();
        result.Warnings.Should().ContainMatch("*nothing to compare*");
    }

    // ── What it records ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_third_leg_is_written_onto_the_accrual_beside_the_other_two()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, quoted: 1260m, ratedWeight: 12.5m);
        await BookedAt(h, consignment, 1260m);
        await h.Accruals.AccrueAsync(consignment, User);

        var invoice = await NewMatchedInvoice(h, carrier, 1400m, lines: [Line(1400m, weight: 14m)]);
        await h.ThreeWay.MatchAsync(invoice, User);

        var accrual = await h.Db.FreightAccruals.AsNoTracking().SingleAsync();

        accrual.AccruedAmount.Should().Be(1260m);
        accrual.BookedAmount.Should().Be(1260m);
        accrual.InvoicedAmount.Should().Be(1400m);
        accrual.VarianceAmount.Should().Be(140m);
        accrual.VarianceReason.Should().Be("WEIGHT");
        accrual.MatchedAt.Should().NotBeNull();
        accrual.CarrierInvoiceId.Should().NotBeNull();
    }

    [Fact]
    public async Task A_matched_accrual_stays_on_the_books_until_the_bill_is_approved()
    {
        // A disputed charge is still owed until somebody decides it is not. Closing it here would
        // take the liability off a month early.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);
        await h.Accruals.AccrueAsync(consignment, User);

        var invoice = await NewMatchedInvoice(h, carrier, 1260m);
        await h.ThreeWay.MatchAsync(invoice, User);

        var accrual = await h.Accruals.GetAsync(consignment);
        accrual!.Status.Should().Be("MATCHED");

        (await h.Accruals.GetSummaryAsync()).OpenCount.Should().Be(1, "it is still owed");
    }

    [Fact]
    public async Task A_preview_records_nothing()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);
        await h.Accruals.AccrueAsync(consignment, User);

        var invoice = await NewMatchedInvoice(h, carrier, 1400m, lines: [Line(1400m)]);

        var preview = await h.ThreeWay.PreviewAsync(invoice);

        preview!.Overcharged.Should().Be(1);
        preview.Status.Should().Be("RECEIVED", "the bill has not moved");

        (await h.Db.FreightAccruals.AsNoTracking().SingleAsync()).InvoicedAmount.Should().BeNull();
    }

    [Fact]
    public async Task The_tolerance_used_is_reported_so_a_verdict_can_be_reproduced()
    {
        var h = NewHarness(tolerancePercent: 5m, toleranceAmount: 25m);
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);
        await h.Accruals.AccrueAsync(consignment, User);

        var result = await h.ThreeWay.PreviewAsync(await NewMatchedInvoice(h, carrier, 1260m));

        result!.TolerancePercent.Should().Be(5m);
        result.ToleranceAmount.Should().Be(25m);
    }

    [Fact]
    public async Task Several_lines_on_one_movement_are_summed_before_comparing()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, quoted: 1260m);
        await h.Accruals.AccrueAsync(consignment, User);

        var invoice = await NewMatchedInvoice(h, carrier, 1260m, lines:
        [
            Line(1125m, chargeCode: "BASE"),
            Line(135m,  chargeCode: "FUEL", description: "Fuel surcharge")
        ]);

        var compared = (await h.ThreeWay.MatchAsync(invoice, User))!.Consignments[0];

        compared.InvoicedAmount.Should().Be(1260m);
        compared.Outcome.Should().Be("WITHIN_TOLERANCE");
        compared.Lines.Should().BeEmpty("the lines belong to the matching view, not this one");
    }

    // ── Not found, and not yours ──────────────────────────────────────────────

    [Fact]
    public async Task A_withdrawn_bill_cannot_be_compared()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var invoice = await NewMatchedInvoice(h, carrier, 1260m);
        await h.Invoices.CancelAsync(invoice, new CancelCarrierInvoiceRequest { Reason = "Duplicate" }, User);

        var act = async () => await h.ThreeWay.MatchAsync(invoice, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*nothing to compare*");
    }

    [Fact]
    public async Task An_unknown_bill_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.ThreeWay.MatchAsync(Guid.NewGuid(), User)).Should().BeNull();
        (await h.ThreeWay.PreviewAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_bill_does_not_exist_here()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var invoice = await NewMatchedInvoice(h, carrier, 1260m);

        var otherDb = LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid());
        var other   = new ThreeWayMatchService(otherDb, new ConfigurationBuilder().Build());

        (await other.PreviewAsync(invoice)).Should().BeNull();
    }
}
