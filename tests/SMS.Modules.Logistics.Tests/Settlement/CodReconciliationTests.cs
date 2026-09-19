using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Settlement;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Settlement;

// T-57 — cash a carrier collects on delivery, and whether it came back. The half of decision G2
// that was never built.
public class CodReconciliationTests
{
    private const int User = 42;
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(LogisticsDbContext Db, CodReconciliationService Cod, string DbName);

    private static Harness NewHarness()
    {
        var (db, _, dbName) = LogisticsTestDb.New();
        return new Harness(db, new CodReconciliationService(db), dbName);
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

    private static async Task<Guid> NewConsignment(
        Harness h, Guid carrier, decimal? cod = 5000m, string currency = "PKR",
        string status = "DELIVERED")
    {
        var carrierRow = await h.Db.Carriers.AsNoTracking().SingleAsync(c => c.UUID == carrier);

        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CarrierId = carrierRow.Id, CarrierName = carrierRow.Name,
            MasterAwb = $"AWB-{Random.Shared.Next(1, 99_999)}",
            Status = status,
            CodAmount = cod, CodCurrency = cod is null ? null : currency,
            CreatedBy = User, CreatedDate = T0
        };

        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return consignment.UUID;
    }

    /// <summary>A consignment carrying cash, with its record opened.</summary>
    private static async Task<Guid> WithCod(
        Harness h, Guid carrier, decimal cod = 5000m, string currency = "PKR",
        string status = "DELIVERED")
    {
        var consignment = await NewConsignment(h, carrier, cod, currency, status);
        await h.Cod.OpenOutstandingAsync(User);
        return consignment;
    }

    // ── Opening ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_dispatched_consignment_carrying_cash_gets_a_record()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, cod: 5000m);

        (await h.Cod.OpenOutstandingAsync(User)).Should().Be(1);

        var record = await h.Cod.GetAsync(consignment);

        record!.ExpectedAmount.Should().Be(5000m);
        record.Currency.Should().Be("PKR");
        record.Status.Should().Be("EXPECTED");
        record.OutstandingAmount.Should().Be(5000m);
        record.CarrierName.Should().Be("Beta Road");
    }

    [Fact]
    public async Task A_consignment_carrying_no_cash_gets_nothing()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, cod: null);

        (await h.Cod.OpenOutstandingAsync(User)).Should().Be(0);
        (await h.Cod.GetAsync(consignment)).Should().BeNull();
    }

    [Fact]
    public async Task Nothing_is_opened_before_the_carrier_has_the_goods()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier, status: "BOOKED");

        (await h.Cod.OpenOutstandingAsync(User)).Should().Be(0);
    }

    [Fact]
    public async Task Opening_twice_does_not_count_the_same_cash_twice()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        await NewConsignment(h, carrier);

        await h.Cod.OpenOutstandingAsync(User);
        (await h.Cod.OpenOutstandingAsync(User)).Should().Be(0);

        (await h.Db.CodCollections.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_expected_amount_is_fixed_when_the_record_opens()
    {
        // Editing the consignment afterwards must not silently change what a carrier owes us.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier, cod: 5000m);

        var row = await h.Db.Consignments.SingleAsync(c => c.UUID == consignment);
        row.CodAmount = 9999m;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        (await h.Cod.GetAsync(consignment))!.ExpectedAmount.Should().Be(5000m);
    }

    // ── What the carrier took ─────────────────────────────────────────────────

    [Fact]
    public async Task A_collection_moves_the_record_on_and_keeps_the_carriers_reference()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier);

        var record = await h.Cod.RecordCollectionAsync(consignment, new RecordCodCollectionRequest
        {
            Amount = 5000m, CollectedAt = T0.AddDays(2), Reference = " RCPT-8821 "
        }, User);

        record!.Status.Should().Be("COLLECTED");
        record.CollectedAmount.Should().Be(5000m);
        record.CollectionReference.Should().Be("RCPT-8821");
        record.OutstandingAmount.Should().Be(5000m, "the carrier has it, we do not");
    }

    [Fact]
    public async Task A_carrier_cannot_be_recorded_as_taking_more_than_the_consignment_asked_for()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier, cod: 5000m);

        var act = async () => await h.Cod.RecordCollectionAsync(
            consignment, new RecordCodCollectionRequest { Amount = 6000m }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*Correct the consignment if the carrier really took more*");
    }

    [Fact]
    public async Task A_short_collection_names_whose_shortfall_it_is()
    {
        // The customer underpaid — the carrier has not kept anything. Chasing the wrong party is
        // the mistake this warning exists to prevent.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier, cod: 5000m);

        var record = await h.Cod.RecordCollectionAsync(
            consignment, new RecordCodCollectionRequest { Amount = 4500m }, User);

        record!.Warnings.Should().ContainMatch("*customer's underpayment, not the carrier's*");
    }

    [Fact]
    public async Task A_collection_of_nothing_is_refused()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier);

        var act = async () => await h.Cod.RecordCollectionAsync(
            consignment, new RecordCodCollectionRequest { Amount = 0m }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    // ── What reached us ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_full_remittance_settles_the_record()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier, cod: 5000m);
        await h.Cod.RecordCollectionAsync(consignment, new RecordCodCollectionRequest { Amount = 5000m }, User);

        var record = await h.Cod.RecordRemittanceAsync(consignment, new RecordCodRemittanceRequest
        {
            Amount = 5000m, Reference = "TT-20260915"
        }, User);

        record!.Status.Should().Be("SETTLED");
        record.OutstandingAmount.Should().Be(0m);
        record.SettledAt.Should().NotBeNull();
        record.Remittances.Should().ContainSingle().Which.Reference.Should().Be("TT-20260915");
    }

    [Fact]
    public async Task Carriers_remit_in_parts_and_a_part_payment_is_seen_as_one()
    {
        // Recording remittances as a list rather than a single figure is what lets a part payment
        // be a part payment rather than a settlement.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier, cod: 5000m);

        await h.Cod.RecordRemittanceAsync(consignment, new RecordCodRemittanceRequest
        {
            Amount = 3000m, Reference = "TT-1"
        }, User);

        var record = await h.Cod.GetAsync(consignment);
        record!.Status.Should().Be("COLLECTED");
        record.RemittedAmount.Should().Be(3000m);
        record.OutstandingAmount.Should().Be(2000m);

        await h.Cod.RecordRemittanceAsync(consignment, new RecordCodRemittanceRequest
        {
            Amount = 2000m, Reference = "TT-2"
        }, User);

        record = await h.Cod.GetAsync(consignment);
        record!.Status.Should().Be("SETTLED");
        record.Remittances.Should().HaveCount(2);
    }

    [Fact]
    public async Task More_than_is_outstanding_is_refused_so_money_is_not_mis_attributed()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier, cod: 5000m);
        await h.Cod.RecordRemittanceAsync(consignment, new RecordCodRemittanceRequest { Amount = 3000m }, User);

        var act = async () => await h.Cod.RecordRemittanceAsync(
            consignment, new RecordCodRemittanceRequest { Amount = 2500m }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*Only PKR 2,000.00 is outstanding*")
            .WithMessage("*split the remittance across the ones it covers*");
    }

    [Fact]
    public async Task Money_arriving_without_a_recorded_collection_is_flagged()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier, cod: 5000m);

        var record = await h.Cod.RecordRemittanceAsync(
            consignment, new RecordCodRemittanceRequest { Amount = 2000m }, User);

        record!.Warnings.Should().ContainMatch("*without the carrier ever confirming it collected*");
    }

    [Fact]
    public async Task Nothing_more_can_be_recorded_once_everything_has_arrived()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier, cod: 5000m);
        await h.Cod.RecordRemittanceAsync(consignment, new RecordCodRemittanceRequest { Amount = 5000m }, User);

        var act = async () => await h.Cod.RecordRemittanceAsync(
            consignment, new RecordCodRemittanceRequest { Amount = 1m }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*already reached us*");
    }

    // ── Writing it off ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_shortfall_can_be_written_off_with_a_reason()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier, cod: 5000m);
        await h.Cod.RecordRemittanceAsync(consignment, new RecordCodRemittanceRequest { Amount = 4800m }, User);

        (await h.Cod.WriteOffAsync(consignment, new WriteOffCodRequest
        {
            Reason = "Carrier deducted its COD handling fee at source, per contract."
        }, User)).Should().BeTrue();

        var record = await h.Cod.GetAsync(consignment);
        record!.Status.Should().Be("WRITTEN_OFF");
        record.WriteOffReason.Should().Contain("handling fee");
    }

    [Fact]
    public async Task Cash_cannot_be_written_off_without_a_reason()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier);

        var act = async () => await h.Cod.WriteOffAsync(
            consignment, new WriteOffCodRequest { Reason = "  " }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*nobody can account for afterwards*");
    }

    [Fact]
    public async Task A_settled_record_has_nothing_to_write_off()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier, cod: 5000m);
        await h.Cod.RecordRemittanceAsync(consignment, new RecordCodRemittanceRequest { Amount = 5000m }, User);

        var act = async () => await h.Cod.WriteOffAsync(
            consignment, new WriteOffCodRequest { Reason = "Anything" }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*nothing to write off*");
    }

    [Fact]
    public async Task Nothing_can_be_recorded_against_a_written_off_shortfall()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier);
        await h.Cod.WriteOffAsync(consignment, new WriteOffCodRequest { Reason = "Bad debt" }, User);

        var act = async () => await h.Cod.RecordRemittanceAsync(
            consignment, new RecordCodRemittanceRequest { Amount = 100m }, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*the two records will disagree about what happened*");
    }

    // ── A returned consignment ────────────────────────────────────────────────

    [Fact]
    public async Task A_consignment_that_came_back_is_told_there_was_probably_no_cash()
    {
        // Otherwise it sits outstanding forever, quietly overstating what carriers owe.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier, status: "RETURNED_TO_ORIGIN");

        (await h.Cod.GetAsync(consignment))!.Warnings
            .Should().ContainMatch("*write it off rather than leaving it outstanding forever*");
    }

    // ── The balance ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_summary_totals_what_each_carrier_is_still_holding()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        var air  = await NewCarrier(h, "Alpha Air");

        await WithCod(h, road, cod: 5000m);
        await WithCod(h, road, cod: 3000m);
        await WithCod(h, air,  cod: 1000m);

        var summary = await h.Cod.GetSummaryAsync();

        summary.OpenCount.Should().Be(3);
        summary.OutstandingTotal.Should().Be(9000m);
        summary.Currency.Should().Be("PKR");
        summary.ByCarrier[0].CarrierName.Should().Be("Beta Road");
        summary.ByCarrier[0].Outstanding.Should().Be(8000m);
    }

    [Fact]
    public async Task A_part_remittance_reduces_what_is_outstanding()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier, cod: 5000m);

        await h.Cod.RecordRemittanceAsync(consignment, new RecordCodRemittanceRequest { Amount = 2000m }, User);

        (await h.Cod.GetSummaryAsync()).OutstandingTotal.Should().Be(3000m);
    }

    [Fact]
    public async Task Cash_in_two_currencies_is_not_added_together()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await WithCod(h, carrier, cod: 5000m, currency: "PKR");
        await WithCod(h, carrier, cod: 100m,  currency: "USD");

        var summary = await h.Cod.GetSummaryAsync();

        summary.OutstandingTotal.Should().Be(0m);
        summary.Currency.Should().BeNull();
        summary.ByCarrier.Should().HaveCount(2);
        summary.Warnings.Should().ContainMatch("*no exchange rate here*");
    }

    [Fact]
    public async Task Delivered_with_cash_and_silence_about_it_is_reported()
    {
        // The analogue of F44 on the other side of the ledger: money that should have come in, and
        // nothing saying whether it did.
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var quiet = await WithCod(h, carrier, cod: 5000m, status: "DELIVERED");

        var known = await WithCod(h, carrier, cod: 3000m, status: "DELIVERED");
        await h.Cod.RecordCollectionAsync(known, new RecordCodCollectionRequest { Amount = 3000m }, User);

        var summary = await h.Cod.GetSummaryAsync();

        summary.NeverCollected.Should().ContainSingle()
            .Which.ConsignmentUuid.Should().Be(quiet);
        summary.Warnings.Should().ContainMatch("*before the trail goes cold*");
    }

    [Fact]
    public async Task A_settled_record_leaves_the_balance_but_not_a_past_one()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier, cod: 5000m);

        var row = await h.Db.CodCollections.SingleAsync();
        row.CreatedDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var monthEnd = new DateTime(2026, 8, 31, 23, 59, 0, DateTimeKind.Utc);

        (await h.Cod.GetSummaryAsync(monthEnd)).OpenCount.Should().Be(1);

        await h.Cod.RecordRemittanceAsync(consignment, new RecordCodRemittanceRequest { Amount = 5000m }, User);

        (await h.Cod.GetSummaryAsync()).OpenCount.Should().Be(0);
        (await h.Cod.GetSummaryAsync(monthEnd)).OpenCount
            .Should().Be(1, "a closed period cannot be rewritten by a later payment");
    }

    // ── Listing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_shows_what_is_still_owed_largest_first()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await WithCod(h, carrier, cod: 1000m);
        await WithCod(h, carrier, cod: 9000m);
        var settled = await WithCod(h, carrier, cod: 2000m);
        await h.Cod.RecordRemittanceAsync(settled, new RecordCodRemittanceRequest { Amount = 2000m }, User);

        var list = await h.Cod.GetListAsync(new CodFilter());

        list.TotalRecords.Should().Be(2, "settled records are not what anybody chases");
        list.Data[0].ExpectedAmount.Should().Be(9000m);

        var all = await h.Cod.GetListAsync(new CodFilter { Status = "SETTLED" });
        all.TotalRecords.Should().Be(1);
    }

    // ── Not found, and not yours ──────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_consignment_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.Cod.GetAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Cod.RecordCollectionAsync(
            Guid.NewGuid(), new RecordCodCollectionRequest { Amount = 1m }, User)).Should().BeNull();
        (await h.Cod.RecordRemittanceAsync(
            Guid.NewGuid(), new RecordCodRemittanceRequest { Amount = 1m }, User)).Should().BeNull();
        (await h.Cod.WriteOffAsync(
            Guid.NewGuid(), new WriteOffCodRequest { Reason = "x" }, User)).Should().BeFalse();
    }

    [Fact]
    public async Task Another_organizations_cash_does_not_exist_here()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await WithCod(h, carrier);

        var otherDb = LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid());
        var other   = new CodReconciliationService(otherDb);

        (await other.GetAsync(consignment)).Should().BeNull();
        (await other.GetSummaryAsync()).OpenCount.Should().Be(0);
    }
}
