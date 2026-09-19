using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Visibility;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Visibility;

// T-63 — how each carrier has actually performed, from figures already stored. Nothing here
// records anything new; it reads what T-39, T-55, T-60 and T-61 had to store anyway.
public class CarrierScorecardTests
{
    private const int User = 42;

    private static readonly DateTime To   = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = To.AddDays(-90);
    private static readonly DateTime Mid  = To.AddDays(-30);

    private sealed record Harness(LogisticsDbContext Db, CarrierScorecardService Scorecard, string DbName);

    private static Harness NewHarness()
    {
        var (db, _, dbName) = LogisticsTestDb.New();
        return new Harness(db, new CarrierScorecardService(db), dbName);
    }

    private static ScorecardFilter Window(string? sortBy = null, bool billing = true, Guid? carrier = null) =>
        new()
        {
            From = From, To = To, SortBy = sortBy, CarrierUuid = carrier, IncludeBilling = billing
        };

    private static async Task<Carrier> NewCarrier(Harness h, string name)
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = name, Code = $"C{Guid.NewGuid():N}"[..6], IsActive = true
        };
        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return carrier;
    }

    private static async Task<Consignment> NewConsignment(
        Harness h, Carrier carrier, string status = "DELIVERED",
        DateTime? dispatchedAt = null, DateTime? eta = null, DateTime? arrivedAt = null,
        DateTime? stuckSince = null, bool neverDispatched = false)
    {
        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CarrierId = carrier.Id, CarrierName = carrier.Name,
            Status = status,
            ActualDispatchAt = neverDispatched ? null : dispatchedAt ?? Mid,
            Eta = eta, ActualArrivalAt = arrivedAt,
            StuckSince = stuckSince,
            CreatedBy = User, CreatedDate = Mid
        };

        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return consignment;
    }

    private static async Task NewException(
        Harness h, Consignment consignment, string type = "DELAYED",
        string severity = "NORMAL", string status = "OPEN",
        DateTime? occurredAt = null, DateTime? resolvedAt = null)
    {
        h.Db.DeliveryExceptions.Add(new DeliveryException
        {
            UUID = Guid.NewGuid(), ConsignmentId = consignment.Id, CarrierId = consignment.CarrierId,
            ExceptionType = type, Severity = severity, Status = status,
            Source = "CARRIER", Description = "Something happened.",
            OccurredAt = occurredAt ?? Mid, ResolvedAt = resolvedAt,
            CreatedBy = User, CreatedDate = Mid
        });

        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    private static async Task NewProof(
        Harness h, Consignment consignment, string? receivedBy = "R. Ahmed", bool withFile = true)
    {
        var proof = new DeliveryProof
        {
            UUID = Guid.NewGuid(), ConsignmentId = consignment.Id,
            ReceivedBy = receivedBy, DeliveredAt = Mid, Source = "MANUAL",
            CreatedBy = User, CreatedDate = Mid
        };

        h.Db.DeliveryProofs.Add(proof);
        await h.Db.SaveChangesAsync();

        if (withFile)
        {
            h.Db.DeliveryProofFiles.Add(new DeliveryProofFile
            {
                UUID = Guid.NewGuid(), DeliveryProofId = proof.Id, Kind = "SIGNATURE",
                ContentType = "image/png", FileName = "sig.png",
                Content = [0x89, 0x50], SizeBytes = 2,
                Sha256 = Guid.NewGuid().ToString("N").PadRight(64, '0')[..64],
                CreatedBy = User, CreatedDate = Mid
            });
            await h.Db.SaveChangesAsync();
        }

        h.Db.ChangeTracker.Clear();
    }

    private static async Task NewAccrual(
        Harness h, Consignment consignment, decimal accrued = 1000m, decimal? invoiced = null,
        string? varianceReason = null, string currency = "PKR")
    {
        h.Db.FreightAccruals.Add(new FreightAccrual
        {
            UUID = Guid.NewGuid(), ConsignmentId = consignment.Id, CarrierId = consignment.CarrierId,
            CarrierName = consignment.CarrierName,
            AccruedAmount = accrued, Currency = currency,
            InvoicedAmount = invoiced,
            VarianceAmount = invoiced is null ? null : invoiced - accrued,
            VarianceReason = varianceReason,
            Status = "ACCRUED", AccruedAt = Mid, AccruedBy = User,
            CreatedBy = User, CreatedDate = Mid
        });

        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    private static CarrierScoreModel Only(CarrierScorecardModel scorecard) => scorecard.Carriers.Single();

    // ── The window ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_cohort_is_what_the_carrier_collected_in_the_window()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");

        await NewConsignment(h, carrier, dispatchedAt: Mid);
        await NewConsignment(h, carrier, dispatchedAt: From.AddDays(-10));  // before it
        await NewConsignment(h, carrier, dispatchedAt: To.AddDays(5));      // after it

        var scorecard = await h.Scorecard.GetAsync(Window());

        Only(scorecard).Consignments.Should().Be(1,
            "a consignment must not move between periods as it progresses");
    }

    [Fact]
    public async Task A_consignment_never_collected_is_not_in_the_cohort()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");
        await NewConsignment(h, carrier, status: "BOOKED", neverDispatched: true);

        var scorecard = await h.Scorecard.GetAsync(Window());

        scorecard.Carriers.Should().BeEmpty();
        scorecard.Warnings.Should().Contain(w => w.Contains("nothing to compare"));
    }

    [Fact]
    public async Task A_cancelled_consignment_is_not_held_against_anybody()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");
        await NewConsignment(h, carrier, status: "CANCELLED");
        await NewConsignment(h, carrier);

        Only(await h.Scorecard.GetAsync(Window())).Consignments.Should().Be(1);
    }

    [Fact]
    public async Task A_window_that_starts_after_it_ends_is_refused()
    {
        var h = NewHarness();

        var get = async () => await h.Scorecard.GetAsync(new ScorecardFilter { From = To, To = From });

        await get.Should().ThrowAsync<BadRequestException>().WithMessage("*starts after it ends*");
    }

    [Fact]
    public async Task A_sort_the_module_does_not_know_is_refused_with_the_ones_it_does()
    {
        var h = NewHarness();

        var get = async () => await h.Scorecard.GetAsync(Window(sortBy: "CHEAPEST"));

        await get.Should().ThrowAsync<BadRequestException>().WithMessage("*ON_TIME*");
    }

    // ── On time ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task On_time_is_worked_out_only_from_deliveries_that_had_an_eta()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");

        await NewConsignment(h, carrier, eta: Mid.AddDays(3), arrivedAt: Mid.AddDays(2));
        await NewConsignment(h, carrier, eta: Mid.AddDays(3), arrivedAt: Mid.AddDays(3));
        await NewConsignment(h, carrier, eta: Mid.AddDays(3), arrivedAt: Mid.AddDays(5));
        await NewConsignment(h, carrier, eta: null,           arrivedAt: Mid.AddDays(2));

        var score = Only(await h.Scorecard.GetAsync(Window()));

        score.Judgeable.Should().Be(3);
        score.OnTime.Should().Be(2);
        score.Late.Should().Be(1);
        score.OnTimePercent.Should().BeApproximately(66.7, 0.1);
        score.NotJudgeable.Should().Be(1);
        score.Warnings.Should().Contain(w => w.Contains("had no ETA"));
    }

    [Fact]
    public async Task Arriving_exactly_on_the_eta_is_on_time()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");
        await NewConsignment(h, carrier, eta: Mid.AddDays(3), arrivedAt: Mid.AddDays(3));

        Only(await h.Scorecard.GetAsync(Window())).OnTime.Should().Be(1);
    }

    [Fact]
    public async Task With_no_eta_anywhere_there_is_no_on_time_figure_at_all()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");
        await NewConsignment(h, carrier, eta: null, arrivedAt: Mid.AddDays(2));

        var score = Only(await h.Scorecard.GetAsync(Window()));

        score.OnTimePercent.Should().BeNull("a percentage of nothing is not zero");
        score.Warnings.Should().Contain(w => w.Contains("no on-time figure at all"));
    }

    [Fact]
    public async Task How_late_the_late_ones_were_is_reported_beside_how_many()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");

        await NewConsignment(h, carrier, eta: Mid, arrivedAt: Mid.AddDays(2));
        await NewConsignment(h, carrier, eta: Mid, arrivedAt: Mid.AddDays(4));

        Only(await h.Scorecard.GetAsync(Window())).AverageDaysLate.Should().Be(3);
    }

    // ── Outcomes ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_outcome_is_counted_and_they_add_up()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");

        await NewConsignment(h, carrier, status: "DELIVERED");
        await NewConsignment(h, carrier, status: "RETURNED_TO_ORIGIN");
        await NewConsignment(h, carrier, status: "LOST");
        await NewConsignment(h, carrier, status: "IN_TRANSIT");

        var score = Only(await h.Scorecard.GetAsync(Window()));

        score.Delivered.Should().Be(1);
        score.ReturnedToOrigin.Should().Be(1);
        score.Lost.Should().Be(1);
        score.StillMoving.Should().Be(1);
        (score.Delivered + score.ReturnedToOrigin + score.Lost + score.StillMoving)
            .Should().Be(score.Consignments);
    }

    // ── Exceptions ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Exceptions_are_counted_per_hundred_consignments_not_as_a_bare_total()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");

        var first = await NewConsignment(h, carrier);
        await NewConsignment(h, carrier);
        await NewConsignment(h, carrier);
        await NewConsignment(h, carrier);

        await NewException(h, first, severity: "CRITICAL");

        var score = Only(await h.Scorecard.GetAsync(Window()));

        score.Exceptions.Should().Be(1);
        score.CriticalExceptions.Should().Be(1);
        score.StillOpen.Should().Be(1);
        score.ExceptionsPer100.Should().Be(25);
    }

    [Fact]
    public async Task A_withdrawn_exception_is_not_held_against_the_carrier()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");
        var consignment = await NewConsignment(h, carrier);

        await NewException(h, consignment, status: "WITHDRAWN", resolvedAt: Mid.AddHours(2));

        Only(await h.Scorecard.GetAsync(Window())).Exceptions.Should().Be(0,
            "counting a mistaken exception would flatter nobody and mislead everybody");
    }

    [Fact]
    public async Task How_long_exceptions_took_to_settle_is_averaged_over_the_settled_ones()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");
        var consignment = await NewConsignment(h, carrier);

        await NewException(h, consignment, status: "RESOLVED",
            occurredAt: Mid, resolvedAt: Mid.AddHours(4));
        await NewException(h, consignment, status: "RESOLVED",
            occurredAt: Mid, resolvedAt: Mid.AddHours(8));
        await NewException(h, consignment, status: "OPEN", occurredAt: Mid);

        Only(await h.Scorecard.GetAsync(Window())).AverageHoursToResolve.Should().Be(6);
    }

    [Fact]
    public async Task What_is_stuck_right_now_is_reported_as_a_live_figure()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");
        await NewConsignment(h, carrier, status: "IN_TRANSIT", stuckSince: Mid);

        var score = Only(await h.Scorecard.GetAsync(Window()));

        score.StuckNow.Should().Be(1);
        score.Warnings.Should().Contain(w => w.Contains("live figure"));
    }

    // ── Evidence ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Proof_coverage_uses_the_same_bar_that_T61_sets()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");

        var good    = await NewConsignment(h, carrier);
        var noFile  = await NewConsignment(h, carrier);
        var noName  = await NewConsignment(h, carrier);
        await NewConsignment(h, carrier);   // nothing at all

        await NewProof(h, good);
        await NewProof(h, noFile, withFile: false);
        await NewProof(h, noName, receivedBy: null);

        var score = Only(await h.Scorecard.GetAsync(Window()));

        score.Defensible.Should().Be(1, "a name and an artefact — one of the two is not evidence");
        score.ProofCoveragePercent.Should().Be(25);
        score.Warnings.Should().Contain(w => w.Contains("could not be demonstrated if they were denied"));
    }

    [Fact]
    public async Task A_consignment_still_moving_is_not_counted_as_an_undocumented_delivery()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");
        await NewConsignment(h, carrier, status: "IN_TRANSIT");

        var score = Only(await h.Scorecard.GetAsync(Window()));

        score.ProofCoveragePercent.Should().BeNull();
        score.Warnings.Should().NotContain(w => w.Contains("could not be demonstrated"));
    }

    // ── Money ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Billing_accuracy_reuses_what_the_three_way_match_already_decided()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");

        var withinTolerance = await NewConsignment(h, carrier);
        var over            = await NewConsignment(h, carrier);
        var under           = await NewConsignment(h, carrier);
        var notBilled       = await NewConsignment(h, carrier);

        await NewAccrual(h, withinTolerance, 1000m, invoiced: 1005m);
        await NewAccrual(h, over,  1000m, invoiced: 1400m, varianceReason: "WEIGHT");
        await NewAccrual(h, under, 1000m, invoiced: 700m,  varianceReason: "SERVICE");
        await NewAccrual(h, notBilled, 1000m);

        var billing = Only(await h.Scorecard.GetAsync(Window())).Billing!;

        billing.Invoiced.Should().Be(3);
        billing.Overcharged.Should().Be(1);
        billing.Undercharged.Should().Be(1, "being billed too little is not good news either");
        billing.AccuracyPercent.Should().BeApproximately(33.3, 0.1);
        billing.NotYetInvoiced.Should().Be(1);
    }

    [Fact]
    public async Task Variance_is_never_summed_across_currencies()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");

        var rupees  = await NewConsignment(h, carrier);
        var dirhams = await NewConsignment(h, carrier);

        await NewAccrual(h, rupees,  1000m, invoiced: 1400m, varianceReason: "WEIGHT", currency: "PKR");
        await NewAccrual(h, dirhams, 1000m, invoiced: 1050m, varianceReason: "FUEL",   currency: "AED");

        var variance = Only(await h.Scorecard.GetAsync(Window())).Billing!.Variance;

        variance.Should().HaveCount(2);
        variance.Single(v => v.Currency == "PKR").Variance.Should().Be(400m);
        variance.Single(v => v.Currency == "AED").Variance.Should().Be(50m);
    }

    [Fact]
    public async Task Somebody_who_may_not_see_what_the_company_pays_is_told_so_rather_than_shown_zeroes()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");
        var consignment = await NewConsignment(h, carrier);
        await NewAccrual(h, consignment, 1000m, invoiced: 1400m, varianceReason: "WEIGHT");

        var scorecard = await h.Scorecard.GetAsync(Window(billing: false));

        Only(scorecard).Billing.Should().BeNull();
        scorecard.Warnings.Should().Contain(w => w.Contains("FREIGHT_INVOICE_VIEW"));
    }

    // ── Comparing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Carriers_are_sorted_by_whichever_measure_the_question_is_about()
    {
        var h = NewHarness();
        var punctual = await NewCarrier(h, "Punctual Freight");
        var late     = await NewCarrier(h, "Late Logistics");

        await NewConsignment(h, punctual, eta: Mid.AddDays(3), arrivedAt: Mid.AddDays(2));
        await NewConsignment(h, late, eta: Mid.AddDays(3), arrivedAt: Mid.AddDays(9));
        await NewConsignment(h, late, eta: Mid.AddDays(3), arrivedAt: Mid.AddDays(9));

        var byOnTime = await h.Scorecard.GetAsync(Window(sortBy: "ON_TIME"));
        var byVolume = await h.Scorecard.GetAsync(Window(sortBy: "VOLUME"));

        byOnTime.Carriers.First().CarrierName.Should().Be("Punctual Freight");
        byVolume.Carriers.First().CarrierName.Should().Be("Late Logistics");
    }

    [Fact]
    public async Task Sorting_by_exceptions_puts_the_worst_first()
    {
        var h = NewHarness();
        var messy = await NewCarrier(h, "Messy Movers");
        var clean = await NewCarrier(h, "Clean Cartage");

        var one = await NewConsignment(h, messy);
        await NewConsignment(h, clean);
        await NewException(h, one);

        var scorecard = await h.Scorecard.GetAsync(Window(sortBy: "EXCEPTIONS"));

        scorecard.Carriers.First().CarrierName.Should().Be("Messy Movers");
    }

    [Fact]
    public async Task A_carrier_with_no_figure_for_the_chosen_measure_goes_last_rather_than_counting_as_zero()
    {
        var h = NewHarness();
        var measured = await NewCarrier(h, "Measured Freight");
        var unknown  = await NewCarrier(h, "Unknown Transit");

        await NewConsignment(h, measured, eta: Mid.AddDays(3), arrivedAt: Mid.AddDays(9));  // 0% on time
        await NewConsignment(h, unknown,  eta: null, arrivedAt: Mid.AddDays(2));            // no figure

        var scorecard = await h.Scorecard.GetAsync(Window(sortBy: "ON_TIME"));

        scorecard.Carriers.First().CarrierName.Should().Be("Measured Freight",
            "absent is not bad — a carrier nothing could be judged on must not outrank one that was judged");
    }

    [Fact]
    public async Task The_scorecard_never_produces_one_overall_number()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");
        await NewConsignment(h, carrier);

        var scorecard = await h.Scorecard.GetAsync(Window());

        typeof(CarrierScoreModel).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(["Score", "Rating", "Rank", "OverallScore"],
                "rolling these into one number needs weights nobody has agreed");

        scorecard.SortedBy.Should().Be("VOLUME");
    }

    [Fact]
    public async Task A_small_sample_still_shows_its_percentage_and_says_it_is_too_small()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");
        await NewConsignment(h, carrier, eta: Mid.AddDays(3), arrivedAt: Mid.AddDays(2));

        var score = Only(await h.Scorecard.GetAsync(Window()));

        score.OnTimePercent.Should().Be(100, "hiding it would be its own kind of lie");
        score.Warnings.Should().Contain(w => w.Contains("Too few to compare"));
    }

    [Fact]
    public async Task A_scorecard_for_one_carrier_says_it_is_not_a_comparison()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");
        var other   = await NewCarrier(h, "Delta Air");
        await NewConsignment(h, carrier);
        await NewConsignment(h, other);

        var scorecard = await h.Scorecard.GetAsync(Window(carrier: carrier.UUID));

        Only(scorecard).CarrierName.Should().Be("Beta Road");
        scorecard.Warnings.Should().Contain(w => w.Contains("not a comparison"));
    }

    // ── Tenancy ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_organization_never_sees_anothers_carriers()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");
        await NewConsignment(h, carrier);

        var stranger = new CarrierScorecardService(LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid()));

        (await stranger.GetAsync(Window())).Carriers.Should().BeEmpty();
    }
}
