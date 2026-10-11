using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Models;
using SMS.Modules.Pos.Services;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Pos.Tests.PosHarness;

namespace SMS.Modules.Pos.Tests;

/// <summary>A38 API-CONTRACT §4 / D-5 — shift lifecycle, totals and cash movements.</summary>
public class ShiftTests
{
    private static PosCashMovementRequest Move(string direction, decimal amount, string reason = PosCashReason.Other, string? localId = null) =>
        new() { Direction = direction, Amount = amount, Reason = reason, LocalId = localId };

    [Fact]
    public async Task Open_numbers_shifts_per_store_and_store_local_day()
    {
        var h = new PosHarness();
        h.Clock.UtcNow = new DateTime(2026, 10, 10, 20, 30, 0, DateTimeKind.Utc); // 01:30 on the 11th in Karachi
        var store = await h.StoreAsync("KHI", "Asia/Karachi");
        var t1 = await h.TerminalAsync(store.Uuid, "T1");
        var t2 = await h.TerminalAsync(store.Uuid, "T2");
        var open = new PosOpenShiftRequest { OpeningFloat = 50m };

        var a = await h.Shifts.OpenAsync(t1.Uuid, open, AdminCaller);
        var b = await h.Shifts.OpenAsync(t2.Uuid, open, AdminCaller);
        a.ShiftNumber.Should().Be("SH-KHI-20261011-01");
        b.ShiftNumber.Should().Be("SH-KHI-20261011-02");
        a.Status.Should().Be("OPEN");
        a.ExpectedCashClosing.Should().Be(50m);
        a.VarianceThreshold.Should().Be(5m); // store default
        a.OpenedByName.Should().Be("Admin");

        // Next store-local day restarts the sequence.
        await h.Shifts.CloseAsync(a.Uuid, new PosCloseShiftRequest { ActualCashClosing = 50m }, AdminCaller);
        h.Clock.Advance(TimeSpan.FromDays(1));
        (await h.Shifts.OpenAsync(t1.Uuid, open, AdminCaller)).ShiftNumber.Should().Be("SH-KHI-20261012-01");
    }

    [Fact]
    public async Task One_active_shift_per_terminal()
    {
        var h = new PosHarness();
        var (_, terminal, shift) = await h.OpenShiftAsync();
        await FluentActions.Awaiting(() => h.Shifts.OpenAsync(terminal.Uuid, new PosOpenShiftRequest(), AdminCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Terminal already has an active shift.");
        await h.Shifts.StartCloseAsync(shift.Uuid, AdminCaller);
        await FluentActions.Awaiting(() => h.Shifts.OpenAsync(terminal.Uuid, new PosOpenShiftRequest(), AdminCaller))
            .Should().ThrowAsync<BadRequestException>();
        await h.Shifts.CloseAsync(shift.Uuid, new PosCloseShiftRequest { ActualCashClosing = 100m }, AdminCaller);
        (await h.Shifts.OpenAsync(terminal.Uuid, new PosOpenShiftRequest(), AdminCaller)).ShiftNumber.Should().EndWith("-02");
    }

    [Fact]
    public async Task Open_requires_store_staff_and_valid_amounts()
    {
        var h = new PosHarness();
        var store = await h.StoreAsync();
        var terminal = await h.TerminalAsync(store.Uuid);
        var opener = Caller(Supervisor, PosPermissions.ShiftOpen);
        await FluentActions.Awaiting(() => h.Shifts.OpenAsync(terminal.Uuid, new PosOpenShiftRequest(), opener))
            .Should().ThrowAsync<BadRequestException>().WithMessage("You are not staff of this store.");
        await FluentActions.Awaiting(() => h.Shifts.OpenAsync(terminal.Uuid, new PosOpenShiftRequest { OpeningFloat = -1 }, AdminCaller))
            .Should().ThrowAsync<BadRequestException>();
        (await h.Shifts.OpenAsync(terminal.Uuid, new PosOpenShiftRequest { VarianceThreshold = 0m }, AdminCaller)).VarianceThreshold.Should().Be(0m);
    }

    [Fact]
    public async Task Lifecycle_open_closing_resume_close_within_threshold_is_reconciled()
    {
        var h = new PosHarness();
        var (_, terminal, shift) = await h.OpenShiftAsync(100m);
        var closer = Caller(Supervisor, PosPermissions.ShiftClose);

        (await h.Shifts.StartCloseAsync(shift.Uuid, closer)).Status.Should().Be("CLOSING");
        await FluentActions.Awaiting(() => h.Shifts.StartCloseAsync(shift.Uuid, closer)).Should().ThrowAsync<BadRequestException>();
        (await h.Shifts.ResumeAsync(shift.Uuid, closer)).Status.Should().Be("OPEN");
        await FluentActions.Awaiting(() => h.Shifts.ResumeAsync(shift.Uuid, closer)).Should().ThrowAsync<BadRequestException>();
        await h.Shifts.StartCloseAsync(shift.Uuid, closer);

        await FluentActions.Awaiting(() => h.Shifts.CloseAsync(shift.Uuid, new PosCloseShiftRequest(), closer))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Counted cash is required.");
        var closed = await h.Shifts.CloseAsync(shift.Uuid, new PosCloseShiftRequest { ActualCashClosing = 96m, VarianceNote = "coins" }, closer);
        closed.Status.Should().Be("RECONCILED");     // |−4| ≤ 5
        closed.CashVariance.Should().Be(-4m);
        closed.ClosedByName.Should().Be("Sue");
        closed.ReconciledByName.Should().Be("Sue");
        closed.VarianceNote.Should().Be("coins");
        (await h.Shifts.GetCurrentAsync(terminal.Uuid)).Should().BeNull();
    }

    [Fact]
    public async Task Variance_over_threshold_is_disputed_until_reconciled_with_a_note()
    {
        var h = new PosHarness();
        var (_, _, shift) = await h.OpenShiftAsync(100m);
        var closed = await h.Shifts.CloseAsync(shift.Uuid, new PosCloseShiftRequest { ActualCashClosing = 90m }, AdminCaller);
        closed.Status.Should().Be("DISPUTED");
        closed.CashVariance.Should().Be(-10m);
        closed.ReconciledAt.Should().BeNull();

        await FluentActions.Awaiting(() => h.Shifts.ReconcileAsync(shift.Uuid, " ", AdminCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("A reconciliation note is required.");
        var reconciled = await h.Shifts.ReconcileAsync(shift.Uuid, "Paid a supplier from the till", AdminCaller);
        reconciled.Status.Should().Be("RECONCILED");
        reconciled.ReconcileNotes.Should().Contain("Admin: Paid a supplier from the till");
        await FluentActions.Awaiting(() => h.Shifts.ReconcileAsync(shift.Uuid, "again", AdminCaller)).Should().ThrowAsync<BadRequestException>();

        var noted = await h.Shifts.AddNoteAsync(shift.Uuid, "Receipt filed", AdminCaller);
        noted.ReconcileNotes!.Split('\n').Should().HaveCount(2);
    }

    [Fact]
    public async Task Closed_shifts_are_immutable_except_for_notes()
    {
        var h = new PosHarness();
        var (_, _, shift) = await h.OpenShiftAsync(100m);
        await FluentActions.Awaiting(() => h.Shifts.AddNoteAsync(shift.Uuid, "early", AdminCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Notes can only be added*");
        await h.Shifts.CloseAsync(shift.Uuid, new PosCloseShiftRequest { ActualCashClosing = 100m }, AdminCaller);

        await FluentActions.Awaiting(() => h.Shifts.CloseAsync(shift.Uuid, new PosCloseShiftRequest { ActualCashClosing = 1m }, AdminCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The shift is already RECONCILED.");
        await FluentActions.Awaiting(() => h.Shifts.ResumeAsync(shift.Uuid, AdminCaller)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Shifts.StartCloseAsync(shift.Uuid, AdminCaller)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Shifts.AddCashMovementAsync(shift.Uuid, Move("IN", 5m), AdminCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Cash can only be moved on an open shift.");

        // Cash movement rows themselves cannot be edited.
        var h2 = new PosHarness();
        var (_, _, open) = await h2.OpenShiftAsync();
        await h2.Shifts.AddCashMovementAsync(open.Uuid, Move("IN", 5m), AdminCaller);
        (await h2.Db.CashMovements.SingleAsync()).Amount = 6m;
        await FluentActions.Awaiting(() => h2.Db.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Close_ends_the_shifts_active_sessions()
    {
        var h = new PosHarness();
        var (_, _, shift) = await h.OpenShiftAsync();
        await h.Sessions.StartAsync(new PosSessionStartRequest { ShiftUuid = shift.Uuid }, Caller(Cashier, PosPermissions.OrderCreate));
        await h.Shifts.CloseAsync(shift.Uuid, new PosCloseShiftRequest { ActualCashClosing = 100m }, AdminCaller);
        var session = await h.Db.CashierSessions.SingleAsync();
        session.IsActive.Should().BeFalse();
        session.EndReason.Should().Be(PosSessionEndReason.ShiftClosed);
    }

    [Fact]
    public async Task Expected_cash_is_recomputed_from_payments_and_movements()
    {
        var h = new PosHarness();
        var (_, _, shift) = await h.OpenShiftAsync(100m);
        await h.OrderAsync(shift.Uuid, PosOrderType.Sale, 80m, tenders: [("CASH", 100m, 20m)]);           // cash 80
        await h.OrderAsync(shift.Uuid, PosOrderType.Sale, 60m, tenders: [("CARD", 40m, 0m), ("CASH", 20m, 0m)]); // card 40, cash 20
        await h.OrderAsync(shift.Uuid, PosOrderType.Sale, 30m, PosOrderStatus.Voided, null, ("CASH", 30m, 0m));   // excluded
        await h.OrderAsync(shift.Uuid, PosOrderType.Sale, 25m, tenders: [("ON_ACCOUNT", 25m, 0m)]);         // credit
        await h.OrderAsync(shift.Uuid, PosOrderType.Sale, 12m, tenders: [("MOBILE", 7m, 0m), ("CHEQUE", 5m, 0m)]);
        await h.OrderAsync(shift.Uuid, PosOrderType.Return, 15m, tenders: [("CASH", 15m, 0m)]);           // cash refund
        await h.OrderAsync(shift.Uuid, PosOrderType.Return, 4m, tenders: [("CARD", 4m, 0m)]);

        await h.Shifts.AddCashMovementAsync(shift.Uuid, Move("IN", 50m, PosCashReason.FloatTopUp), AdminCaller);
        await h.Shifts.AddCashMovementAsync(shift.Uuid, Move("OUT", 70m, PosCashReason.SafeDrop), AdminCaller);

        var s = await h.Shifts.GetAsync(shift.Uuid, AdminCaller);
        s.Should().BeEquivalentTo(new
        {
            TotalCashSales = 100m, TotalCardSales = 40m, TotalMobileSales = 7m, TotalOtherSales = 5m, TotalCreditSales = 25m,
            TotalCashRefunds = 15m, TotalOtherRefunds = 4m, TotalCashIn = 50m, TotalCashOut = 70m,
            ExpectedCashClosing = 100m + 100m + 50m - 70m - 15m, OrderCount = 4, RefundCount = 2, GrossTotal = 177m, NetTotal = 158m
        }, o => o.ExcludingMissingMembers());
        s.CashMovements.Should().HaveCount(2);

        var summary = await h.Shifts.GetSummaryAsync(shift.Uuid, AdminCaller);
        summary.ByMethod.Single(m => m.Method == "CASH").Should().BeEquivalentTo(new { Sales = 100m, Refunds = 15m, Net = 85m });
        summary.ByMethod.Single(m => m.Method == "CARD").Net.Should().Be(36m);

        var closed = await h.Shifts.CloseAsync(shift.Uuid, new PosCloseShiftRequest { ActualCashClosing = 165m }, AdminCaller);
        closed.CashVariance.Should().Be(0m);
        closed.Status.Should().Be("RECONCILED");
    }

    [Fact]
    public async Task Late_orders_recalculate_a_closed_shifts_variance_without_changing_its_status()
    {
        var h = new PosHarness();
        var (_, _, shift) = await h.OpenShiftAsync(100m);
        await h.Shifts.CloseAsync(shift.Uuid, new PosCloseShiftRequest { ActualCashClosing = 120m }, AdminCaller); // DISPUTED (+20)
        await h.OrderAsync(shift.Uuid, PosOrderType.Sale, 20m, tenders: [("CASH", 20m, 0m)]);              // offline sale arrives
        await h.Totals.RecalculateAsync((await h.Db.Shifts.SingleAsync()).Id);
        var s = await h.Shifts.GetAsync(shift.Uuid, AdminCaller);
        s.CashVariance.Should().Be(0m);
        s.Status.Should().Be("DISPUTED");
    }

    [Fact]
    public async Task Cash_movement_validation_and_local_id_idempotency()
    {
        var h = new PosHarness();
        var (_, _, shift) = await h.OpenShiftAsync();
        await FluentActions.Awaiting(() => h.Shifts.AddCashMovementAsync(shift.Uuid, Move("SIDEWAYS", 5m), AdminCaller)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Shifts.AddCashMovementAsync(shift.Uuid, Move("IN", 0m), AdminCaller)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Shifts.AddCashMovementAsync(shift.Uuid, Move("IN", 5m, "TIPS"), AdminCaller)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Shifts.AddCashMovementAsync(shift.Uuid, Move("IN", 5.555m), AdminCaller)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Shifts.AddCashMovementAsync(shift.Uuid, Move("IN", 5m), Caller(Cashier2, PosPermissions.CashMovement)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("You are not staff of this store.");

        var first = await h.Shifts.AddCashMovementAsync(shift.Uuid, Move("out", 30m, "safe_drop", "m-1"), Caller(Supervisor, PosPermissions.CashMovement));
        var again = await h.Shifts.AddCashMovementAsync(shift.Uuid, Move("OUT", 30m, PosCashReason.SafeDrop, "m-1"), Caller(Supervisor, PosPermissions.CashMovement));
        again.Uuid.Should().Be(first.Uuid);
        first.PerformedByName.Should().Be("Sue");
        (await h.Db.CashMovements.CountAsync()).Should().Be(1);
        (await h.Shifts.GetCurrentAsync((await h.Db.Terminals.SingleAsync()).Uuid))!.ExpectedCashClosing.Should().Be(70m);

        // The same local id on another shift is a conflict, not a silent replay.
        var store2 = await h.StoreAsync("S2");
        var t2 = await h.TerminalAsync(store2.Uuid);
        var other = await h.Shifts.OpenAsync(t2.Uuid, new PosOpenShiftRequest(), AdminCaller);
        await FluentActions.Awaiting(() => h.Shifts.AddCashMovementAsync(other.Uuid, Move("IN", 1m, localId: "m-1"), AdminCaller)).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Totals_formula_on_its_own()
    {
        var shift = new PosShift { OpeningFloat = 200m, ActualCashClosing = 300m };
        PosShiftTotals.Compute(shift,
            [new(1, "SALE", "COMPLETED", 110m), new(2, "SALE", "VOIDED", 50m), new(3, "RETURN", "COMPLETED", 10m)],
            [new("SALE", "COMPLETED", "CASH", 120m, 10m, false), new("SALE", "VOIDED", "CASH", 50m, 0m, false),
             new("RETURN", "COMPLETED", "CASH", 10m, 0m, true), new("SALE", "COMPLETED", "BANK_TRANSFER", 0m, 0m, false)],
            [new("IN", 20m), new("OUT", 5m)]);
        shift.TotalCashSales.Should().Be(110m);
        shift.ExpectedCashClosing.Should().Be(200m + 110m + 20m - 5m - 10m);
        shift.CashVariance.Should().Be(300m - 315m);
        shift.OrderCount.Should().Be(1);
        shift.NetTotal.Should().Be(100m);
    }

    [Fact]
    public async Task Shift_reads_need_shift_view_or_store_staff_with_order_create()
    {
        var h = new PosHarness();
        var (_, _, shift) = await h.OpenShiftAsync();
        (await h.Shifts.GetAsync(shift.Uuid, Caller(Cashier, PosPermissions.OrderCreate))).Uuid.Should().Be(shift.Uuid);
        await FluentActions.Awaiting(() => h.Shifts.GetAsync(shift.Uuid, Caller(Cashier2, PosPermissions.OrderCreate))).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(() => h.Shifts.GetSummaryAsync(shift.Uuid, Caller(Cashier2, PosPermissions.OrderCreate))).Should().ThrowAsync<ForbiddenException>();
        (await h.Shifts.ListCashMovementsAsync(shift.Uuid, Caller(Cashier2, PosPermissions.ShiftView))).Should().BeEmpty();
    }

    [Fact]
    public async Task Shift_list_filters_and_orders_newest_first()
    {
        var h = new PosHarness();
        var (store, terminal, first) = await h.OpenShiftAsync();
        await h.Shifts.CloseAsync(first.Uuid, new PosCloseShiftRequest { ActualCashClosing = 0m }, AdminCaller); // DISPUTED
        h.Clock.Advance(TimeSpan.FromHours(1));
        var second = await h.Shifts.OpenAsync(terminal.Uuid, new PosOpenShiftRequest(), AdminCaller);

        var all = await h.Shifts.ListAsync(new PosShiftListFilter { StoreUuid = store.Uuid });
        all.Data.Select(s => s.Uuid).Should().Equal(second.Uuid, first.Uuid);
        (await h.Shifts.ListAsync(new PosShiftListFilter { Status = "DISPUTED" })).Data.Single().Uuid.Should().Be(first.Uuid);
        (await h.Shifts.ListAsync(new PosShiftListFilter { TerminalUuid = Guid.NewGuid() })).TotalRecords.Should().Be(0);
    }
}
