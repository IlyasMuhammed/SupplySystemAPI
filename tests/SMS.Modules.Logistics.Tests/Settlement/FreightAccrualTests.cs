using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Settlement;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Settlement;

// T-52 — what is owed to carriers for movements that have already happened.
public class FreightAccrualTests
{
    private const int User = 42;
    private static readonly DateTime T0 = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(LogisticsDbContext Db, FreightAccrualService Accruals, string DbName);

    private static Harness NewHarness()
    {
        var (db, _, dbName) = LogisticsTestDb.New();
        return new Harness(db, new FreightAccrualService(db), dbName);
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

    /// <summary>A consignment the carrier has taken, priced at 1,260 PKR unless told otherwise.</summary>
    private static async Task<Guid> NewConsignment(
        Harness h, Guid? carrier = null, string status = "PICKED_UP",
        decimal? freightCost = 1260m, string? currency = "PKR", string? source = "CARRIER")
    {
        var carrierRow = carrier is null
            ? null
            : await h.Db.Carriers.AsNoTracking().SingleAsync(c => c.UUID == carrier);

        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CarrierId = carrierRow?.Id, CarrierName = carrierRow?.Name,
            Status = status,
            FreightCost = freightCost,
            FreightCurrency = freightCost is null ? null : currency,
            FreightRateSource = freightCost is null ? null : source,
            ActualDispatchAt = T0,
            CreatedBy = User, CreatedDate = T0
        };

        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return consignment.UUID;
    }

    /// <summary>A successful booking command carrying what the carrier said it would charge.</summary>
    private static async Task BookedAt(Harness h, Guid consignmentUuid, decimal cost, string currency = "PKR")
    {
        var consignment = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == consignmentUuid);

        h.Db.CarrierCommands.Add(new CarrierCommand
        {
            UUID = Guid.NewGuid(), ConsignmentId = consignment.Id,
            CommandType = LogisticsCode.Of(CarrierCommandType.Book),
            Status = LogisticsCode.Of(CarrierCommandStatus.Succeeded),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            RequestFingerprint = "fp", ProviderKey = "SCRIPTED",
            Cost = cost, CostCurrency = currency,
            AttemptCount = 1, FirstAttemptAt = T0, LastAttemptAt = T0, CompletedAt = T0,
            CreatedBy = User, CreatedDate = T0
        });

        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    private static Task<Consignment> Reload(Harness h, Guid uuid) =>
        h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == uuid);

    // ── Accruing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_consignment_the_carrier_has_taken_is_accrued_at_what_it_was_priced()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);

        var accrual = await h.Accruals.AccrueAsync(consignment, User);

        accrual!.AccruedAmount.Should().Be(1260m);
        accrual.Currency.Should().Be("PKR");
        accrual.QuoteSource.Should().Be("CARRIER");
        accrual.Status.Should().Be("ACCRUED");
        accrual.CarrierName.Should().Be("Beta Road");
        accrual.AccruedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task What_the_carrier_said_at_booking_is_kept_beside_what_was_quoted()
    {
        // Finding F45: it was only ever on the command ledger — the same shape as F37, which T-47
        // fixed for the quote. The three-way match needs all three legs in one place.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);
        await BookedAt(h, consignment, 1310m);

        var accrual = await h.Accruals.AccrueAsync(consignment, User);

        accrual!.BookedAmount.Should().Be(1310m);
        accrual.BookedCurrency.Should().Be("PKR");
        accrual.BookedVariance.Should().Be(50m, "the carrier said more than we were quoted");
    }

    [Fact]
    public async Task A_booking_in_another_currency_produces_no_variance_rather_than_a_meaningless_one()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);
        await BookedAt(h, consignment, 20m, "USD");

        var accrual = await h.Accruals.AccrueAsync(consignment, User);

        accrual!.BookedAmount.Should().Be(20m);
        accrual.BookedVariance.Should().BeNull("a rupee quote against a dollar booking compares nothing");
    }

    [Fact]
    public async Task A_manual_booking_accrues_with_no_carrier_figure_at_all()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);

        var accrual = await h.Accruals.AccrueAsync(consignment, User);

        accrual!.AccruedAmount.Should().Be(1260m);
        accrual.BookedAmount.Should().BeNull();
        accrual.BookedVariance.Should().BeNull();
    }

    [Theory]
    [InlineData("PICKED_UP")]
    [InlineData("IN_TRANSIT")]
    [InlineData("OUT_FOR_DELIVERY")]
    [InlineData("DELIVERED")]
    [InlineData("RETURNED_TO_ORIGIN")]
    [InlineData("LOST")]
    public async Task Everything_the_carrier_has_taken_is_billable(string status)
    {
        // A delivered consignment was collected; a lost one was collected and then lost. Both cost
        // money to move, and a carrier bills for both.
        var h = NewHarness();
        var consignment = await NewConsignment(h, await NewCarrier(h), status);

        (await h.Accruals.AccrueAsync(consignment, User)).Should().NotBeNull();
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("RATED")]
    [InlineData("BOOKED")]
    [InlineData("LABEL_READY")]
    public async Task Nothing_is_accrued_before_the_carrier_has_the_goods(string status)
    {
        // A booked parcel still on our own dock is not a movement anybody owes for.
        var h = NewHarness();
        var consignment = await NewConsignment(h, await NewCarrier(h), status);

        var act = async () => await h.Accruals.AccrueAsync(consignment, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*until the carrier has taken the goods*");
    }

    [Fact]
    public async Task Accruing_twice_does_not_double_the_liability()
    {
        // It runs from a nightly sweep as well as by hand, and a second accrual would look exactly
        // as legitimate as the first.
        var h = NewHarness();
        var consignment = await NewConsignment(h, await NewCarrier(h));

        var first  = await h.Accruals.AccrueAsync(consignment, User);
        var second = await h.Accruals.AccrueAsync(consignment, User);

        second!.UUID.Should().Be(first!.UUID);
        (await h.Db.FreightAccruals.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_consignment_that_moved_without_ever_being_priced_is_refused_not_accrued_at_zero()
    {
        // Finding F44. A zero accrual understates the liability and looks entirely correct doing it.
        var h = NewHarness();
        var consignment = await NewConsignment(h, await NewCarrier(h), freightCost: null);

        var act = async () => await h.Accruals.AccrueAsync(consignment, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*never priced*")
            .WithMessage("*accruing zero would understate*");

        (await h.Db.FreightAccruals.CountAsync()).Should().Be(0);
    }

    // ── The sweep ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_sweep_accrues_everything_that_has_moved_and_is_not_yet_accrued()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await NewConsignment(h, carrier);
        await NewConsignment(h, carrier, "DELIVERED");
        var alreadyDone = await NewConsignment(h, carrier, "IN_TRANSIT");
        await NewConsignment(h, carrier, "DRAFT");           // not moved

        await h.Accruals.AccrueAsync(alreadyDone, User);

        var result = await h.Accruals.SweepAsync(User);

        result.Accrued.Should().Be(2);
        (await h.Db.FreightAccruals.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task The_sweep_reports_what_it_could_not_accrue_rather_than_skipping_it_quietly()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await NewConsignment(h, carrier);
        var unpriced = await NewConsignment(h, carrier, freightCost: null);

        var result = await h.Accruals.SweepAsync(User);

        result.Accrued.Should().Be(1);
        result.CouldNotAccrue.Should().ContainSingle()
            .Which.ConsignmentUuid.Should().Be(unpriced);
        result.CouldNotAccrue[0].Reason.Should().Contain("never priced");
    }

    [Fact]
    public async Task The_sweep_writes_back_an_accrual_whose_consignment_was_cancelled_afterwards()
    {
        // A liability for a movement that did not happen, written back rather than left to be
        // noticed at a period end.
        var h = NewHarness();
        var consignment = await NewConsignment(h, await NewCarrier(h));
        await h.Accruals.AccrueAsync(consignment, User);

        var row = await h.Db.Consignments.SingleAsync(c => c.UUID == consignment);
        row.Status = "CANCELLED";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var result = await h.Accruals.SweepAsync(User);

        result.Reversed.Should().Be(1);

        var accrual = await h.Accruals.GetAsync(consignment);
        accrual!.Status.Should().Be("REVERSED");
        accrual.ReleaseReason.Should().Contain("cancelled");
        accrual.ReleasedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_sweep_with_nothing_to_do_does_nothing()
    {
        var h = NewHarness();

        var result = await h.Accruals.SweepAsync(User);

        result.Accrued.Should().Be(0);
        result.Reversed.Should().Be(0);
        result.CouldNotAccrue.Should().BeEmpty();
    }

    // ── The balance ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_summary_totals_what_is_owed_by_carrier()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        var air  = await NewCarrier(h, "Alpha Air");

        await h.Accruals.AccrueAsync(await NewConsignment(h, road), User);
        await h.Accruals.AccrueAsync(await NewConsignment(h, road), User);
        await h.Accruals.AccrueAsync(await NewConsignment(h, air), User);

        var summary = await h.Accruals.GetSummaryAsync();

        summary.OpenCount.Should().Be(3);
        summary.OpenTotal.Should().Be(3780m);
        summary.Currency.Should().Be("PKR");

        summary.ByCarrier.Should().HaveCount(2);
        summary.ByCarrier[0].CarrierName.Should().Be("Beta Road");
        summary.ByCarrier[0].Total.Should().Be(2520m);
        summary.ByCarrier[0].Count.Should().Be(2);
    }

    [Fact]
    public async Task Accruals_in_two_currencies_are_not_added_together()
    {
        // This module holds no exchange rate (F42), and a grand total across two currencies is a
        // number nobody could defend.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Accruals.AccrueAsync(await NewConsignment(h, carrier), User);
        await h.Accruals.AccrueAsync(
            await NewConsignment(h, carrier, freightCost: 20m, currency: "USD"), User);

        var summary = await h.Accruals.GetSummaryAsync();

        summary.OpenTotal.Should().Be(0m);
        summary.Currency.Should().BeNull();
        summary.ByCarrier.Should().HaveCount(2, "one row per carrier and currency");
        summary.Warnings.Should().ContainMatch("*no exchange rate here*");
    }

    [Fact]
    public async Task The_balance_is_struck_as_at_a_date_not_as_at_today()
    {
        // An accrual released last week was still a liability at month end, and a balance that
        // forgets that cannot be reconciled to anything.
        var h = NewHarness();
        var consignment = await NewConsignment(h, await NewCarrier(h));
        await h.Accruals.AccrueAsync(consignment, User);

        // Back-dated so the two events are far enough apart to ask a question between them.
        var row = await h.Db.FreightAccruals.SingleAsync();
        row.AccruedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var monthEnd = new DateTime(2026, 8, 31, 23, 59, 0, DateTimeKind.Utc);

        (await h.Accruals.GetSummaryAsync(monthEnd)).OpenCount
            .Should().Be(1, "it was outstanding at the end of August");

        await h.Accruals.ReverseAsync(consignment, new ReverseAccrualRequest
        {
            Reason = "Billed on another consignment."
        }, User);

        (await h.Accruals.GetSummaryAsync()).OpenCount.Should().Be(0, "it is closed now");

        // The August balance does not change because something was released in September.
        (await h.Accruals.GetSummaryAsync(monthEnd)).OpenCount
            .Should().Be(1, "a closed period cannot be rewritten by a later release");
    }

    [Fact]
    public async Task An_accrual_made_after_the_date_asked_about_is_not_in_the_balance()
    {
        var h = NewHarness();
        await h.Accruals.AccrueAsync(await NewConsignment(h, await NewCarrier(h)), User);

        (await h.Accruals.GetSummaryAsync(T0.AddDays(-1))).OpenCount.Should().Be(0);
    }

    [Fact]
    public async Task The_balance_says_what_is_missing_from_it()
    {
        // Dispatched, never priced. A balance that did not say so would be understated by however
        // many of them there are, and look complete.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Accruals.AccrueAsync(await NewConsignment(h, carrier), User);
        await NewConsignment(h, carrier, freightCost: null);

        var summary = await h.Accruals.GetSummaryAsync();

        summary.OpenCount.Should().Be(1);
        summary.CouldNotAccrue.Should().ContainSingle();
        summary.Warnings.Should().ContainMatch("*understated by whatever they come to*");
    }

    // ── Writing one back ──────────────────────────────────────────────────────

    [Fact]
    public async Task An_accrual_can_be_written_back_with_a_reason()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, await NewCarrier(h));
        await h.Accruals.AccrueAsync(consignment, User);

        (await h.Accruals.ReverseAsync(consignment, new ReverseAccrualRequest
        {
            Reason = "Accrued in error — this moved on the customer's own account."
        }, User)).Should().BeTrue();

        var accrual = await h.Accruals.GetAsync(consignment);
        accrual!.Status.Should().Be("REVERSED");
        accrual.ReleaseReason.Should().Contain("customer's own account");
    }

    [Fact]
    public async Task An_accrual_cannot_vanish_without_a_reason()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, await NewCarrier(h));
        await h.Accruals.AccrueAsync(consignment, User);

        var act = async () => await h.Accruals.ReverseAsync(
            consignment, new ReverseAccrualRequest { Reason = "  " }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*hole in a ledger*");
    }

    [Fact]
    public async Task An_accrual_already_written_back_cannot_be_written_back_again()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, await NewCarrier(h));
        await h.Accruals.AccrueAsync(consignment, User);
        await h.Accruals.ReverseAsync(consignment, new ReverseAccrualRequest { Reason = "Wrong" }, User);

        var act = async () => await h.Accruals.ReverseAsync(
            consignment, new ReverseAccrualRequest { Reason = "Wrong again" }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*Only an open accrual*");
    }

    [Fact]
    public async Task A_written_back_accrual_does_not_block_the_consignment_being_accrued_again()
    {
        // The row is kept, so the sweep must not resurrect it — and it does not, because one
        // accrual per consignment is the rule whatever state it is in.
        var h = NewHarness();
        var consignment = await NewConsignment(h, await NewCarrier(h));
        await h.Accruals.AccrueAsync(consignment, User);
        await h.Accruals.ReverseAsync(consignment, new ReverseAccrualRequest { Reason = "Wrong" }, User);

        var result = await h.Accruals.SweepAsync(User);

        result.Accrued.Should().Be(0);
        (await h.Db.FreightAccruals.CountAsync()).Should().Be(1);
    }

    // ── Not found, and not yours ──────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_consignment_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.Accruals.GetAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Accruals.AccrueAsync(Guid.NewGuid(), User)).Should().BeNull();
        (await h.Accruals.ReverseAsync(
            Guid.NewGuid(), new ReverseAccrualRequest { Reason = "x" }, User)).Should().BeFalse();
    }

    [Fact]
    public async Task Another_organizations_accruals_do_not_exist_here()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, await NewCarrier(h));
        await h.Accruals.AccrueAsync(consignment, User);

        var otherDb = LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid());
        var other   = new FreightAccrualService(otherDb);

        (await other.GetAsync(consignment)).Should().BeNull();
        (await other.GetSummaryAsync()).OpenCount.Should().Be(0);
    }

    [Fact]
    public async Task The_consignments_current_status_travels_with_the_accrual()
    {
        // So a stale accrual — one whose consignment has moved on since — is visible as one.
        var h = NewHarness();
        var consignment = await NewConsignment(h, await NewCarrier(h));
        await h.Accruals.AccrueAsync(consignment, User);

        var row = await h.Db.Consignments.SingleAsync(c => c.UUID == consignment);
        row.Status = "DELIVERED";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        (await h.Accruals.GetAsync(consignment))!.ConsignmentStatus.Should().Be("DELIVERED");
        _ = await Reload(h, consignment);
    }
}
