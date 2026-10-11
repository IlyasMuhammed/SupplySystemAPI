using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Models;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Pos.Tests.PosHarness;

namespace SMS.Modules.Pos.Tests;

/// <summary>A38 API-CONTRACT §3 — PIN sign-in (POS-CSH-07) and cashier sessions (POS-CSH-04).</summary>
public class PinSignInAndSessionTests
{
    private static PosPinSignInRequest Pin(Guid terminal, string pin, int? userId = null) => new() { TerminalUuid = terminal, Pin = pin, UserId = userId };

    [Fact]
    public async Task Pin_identifies_the_staff_member_and_returns_their_own_tokens()
    {
        var h = new PosHarness();
        var (_, terminal, _) = await h.OpenShiftAsync();

        var result = await h.Sessions.PinSignInAsync(Pin(terminal.Uuid, "2222"));
        result.AccessToken.Should().Be($"access-{Supervisor}");
        result.User.Should().BeEquivalentTo(new { UserId = Supervisor, Name = "Sue", PosRole = "SUPERVISOR" });
        result.User.Permissions.Should().Contain(PosPermissions.OrderCreate);

        (await h.Sessions.PinSignInAsync(Pin(terminal.Uuid, "1111", Cashier))).User.UserId.Should().Be(Cashier);
    }

    [Fact]
    public async Task Five_failures_lock_the_terminal_for_five_minutes_and_unlock_clears_it()
    {
        var h = new PosHarness();
        var (_, terminal, _) = await h.OpenShiftAsync();

        for (var i = 1; i <= 4; i++)
            await FluentActions.Awaiting(() => h.Sessions.PinSignInAsync(Pin(terminal.Uuid, "9999")))
                .Should().ThrowAsync<BadRequestException>().WithMessage("Invalid PIN.");
        await FluentActions.Awaiting(() => h.Sessions.PinSignInAsync(Pin(terminal.Uuid, "9999")))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Terminal locked until 09:05.");
        // Even the right PIN is refused while locked.
        await FluentActions.Awaiting(() => h.Sessions.PinSignInAsync(Pin(terminal.Uuid, "1111")))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Terminal locked until*");
        (await h.Stores.GetStatusAsync(terminal.Uuid)).PinLockedUntil.Should().Be(h.Clock.UtcNow.AddMinutes(5));
        h.Pins.TokensIssuedFor.Should().BeEmpty();

        await h.Stores.UnlockAsync(terminal.Uuid);
        (await h.Sessions.PinSignInAsync(Pin(terminal.Uuid, "1111"))).User.UserId.Should().Be(Cashier);
    }

    [Fact]
    public async Task Lock_expires_and_success_resets_the_failure_count()
    {
        var h = new PosHarness();
        var (_, terminal, _) = await h.OpenShiftAsync();
        for (var i = 0; i < 5; i++)
            await FluentActions.Awaiting(() => h.Sessions.PinSignInAsync(Pin(terminal.Uuid, "0000"))).Should().ThrowAsync<BadRequestException>();

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        (await h.Sessions.PinSignInAsync(Pin(terminal.Uuid, "1111"))).User.UserId.Should().Be(Cashier);
        var entity = await h.Db.Terminals.SingleAsync();
        entity.PinFailedAttempts.Should().Be(0);
        entity.PinLockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task Lock_time_is_shown_in_store_time()
    {
        var h = new PosHarness();
        var store = await h.StoreAsync("K1", "Asia/Karachi"); // UTC+5
        var terminal = await h.TerminalAsync(store.Uuid);
        await h.StaffAsync(store.Uuid, Cashier, pin: "1111");
        for (var i = 0; i < 4; i++)
            await FluentActions.Awaiting(() => h.Sessions.PinSignInAsync(Pin(terminal.Uuid, "0000"))).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Sessions.PinSignInAsync(Pin(terminal.Uuid, "0000")))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Terminal locked until 14:05.");
    }

    [Fact]
    public async Task Signing_in_needs_active_store_staff_with_pos_order_create()
    {
        var h = new PosHarness();
        var (store, terminal, _) = await h.OpenShiftAsync();
        await h.StaffAsync(store.Uuid, NoPos, pin: "3333");

        await FluentActions.Awaiting(() => h.Sessions.PinSignInAsync(Pin(terminal.Uuid, "3333")))
            .Should().ThrowAsync<BadRequestException>().WithMessage("You do not have permission to sell on POS.");
        await FluentActions.Awaiting(() => h.Sessions.PinSignInAsync(Pin(terminal.Uuid, "1111", Cashier2)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("You are not staff of this store.");

        await h.Stores.RemoveStaffAsync(store.Uuid, Cashier);
        await FluentActions.Awaiting(() => h.Sessions.PinSignInAsync(Pin(terminal.Uuid, "1111")))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Invalid PIN.");

        h.Pins.Users[Supervisor] = h.Pins.Users[Supervisor] with { IsActive = false };
        await FluentActions.Awaiting(() => h.Sessions.PinSignInAsync(Pin(terminal.Uuid, "2222")))
            .Should().ThrowAsync<BadRequestException>();
        h.Pins.TokensIssuedFor.Should().BeEmpty();
    }

    [Fact]
    public async Task Pin_sign_in_refuses_malformed_pins_without_counting_them_and_inactive_terminals()
    {
        var h = new PosHarness();
        var (_, terminal, _) = await h.OpenShiftAsync();
        await FluentActions.Awaiting(() => h.Sessions.PinSignInAsync(Pin(terminal.Uuid, "12"))).Should().ThrowAsync<BadRequestException>()
            .WithMessage("PIN must be 4 to 6 digits.");
        (await h.Db.Terminals.SingleAsync()).PinFailedAttempts.Should().Be(0);
        await FluentActions.Awaiting(() => h.Sessions.PinSignInAsync(Pin(Guid.NewGuid(), "1111"))).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Session_resumes_for_the_same_user_and_switching_ends_the_previous_cashier()
    {
        var h = new PosHarness();
        var (store, _, shift) = await h.OpenShiftAsync();
        await h.StaffAsync(store.Uuid, Cashier2, pin: "3434");
        var cathy = Caller(Cashier, PosPermissions.OrderCreate);
        var carl  = Caller(Cashier2, PosPermissions.OrderCreate);

        var first = await h.Sessions.StartAsync(new PosSessionStartRequest { ShiftUuid = shift.Uuid }, cathy);
        (await h.Sessions.StartAsync(new PosSessionStartRequest { ShiftUuid = shift.Uuid }, cathy)).Uuid.Should().Be(first.Uuid);

        var second = await h.Sessions.StartAsync(new PosSessionStartRequest { ShiftUuid = shift.Uuid, LocalId = "s-2" }, carl);
        second.IsActive.Should().BeTrue();
        second.UserName.Should().Be("Carl");
        var ended = await h.Db.CashierSessions.SingleAsync(s => s.Uuid == first.Uuid);
        ended.IsActive.Should().BeFalse();
        ended.EndReason.Should().Be(PosSessionEndReason.Switched);
        (await h.Db.CashierSessions.CountAsync(s => s.IsActive)).Should().Be(1);

        // localId replay answers with the same session.
        (await h.Sessions.StartAsync(new PosSessionStartRequest { ShiftUuid = shift.Uuid, LocalId = "s-2" }, carl)).Uuid.Should().Be(second.Uuid);
    }

    [Fact]
    public async Task Session_needs_an_open_shift_and_store_staff_and_ends_only_by_its_owner_or_a_closer()
    {
        var h = new PosHarness();
        var (_, _, shift) = await h.OpenShiftAsync();
        await FluentActions.Awaiting(() => h.Sessions.StartAsync(new PosSessionStartRequest { ShiftUuid = shift.Uuid }, Caller(Cashier2, PosPermissions.OrderCreate)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("You are not staff of this store.");

        var session = await h.Sessions.StartAsync(new PosSessionStartRequest { ShiftUuid = shift.Uuid }, Caller(Cashier, PosPermissions.OrderCreate));
        await FluentActions.Awaiting(() => h.Sessions.EndAsync(session.Uuid, Caller(Cashier2, PosPermissions.OrderCreate))).Should().ThrowAsync<ForbiddenException>();
        (await h.Sessions.EndAsync(session.Uuid, Caller(Cashier, PosPermissions.OrderCreate))).IsActive.Should().BeFalse();

        await h.Shifts.StartCloseAsync(shift.Uuid, AdminCaller);
        await FluentActions.Awaiting(() => h.Sessions.StartAsync(new PosSessionStartRequest { ShiftUuid = shift.Uuid }, Caller(Cashier, PosPermissions.OrderCreate)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The shift is not open.");
    }

    [Fact]
    public async Task Session_totals_come_from_its_orders()
    {
        var h = new PosHarness();
        var (_, _, shift) = await h.OpenShiftAsync();
        var session = await h.Sessions.StartAsync(new PosSessionStartRequest { ShiftUuid = shift.Uuid }, Caller(Cashier, PosPermissions.OrderCreate));
        var id = (await h.Db.CashierSessions.SingleAsync()).Id;
        await h.OrderAsync(shift.Uuid, PosOrderType.Sale, 80m, sessionId: id, tenders: [("CASH", 100m, 20m)]);
        await h.OrderAsync(shift.Uuid, PosOrderType.Sale, 50m, sessionId: id, tenders: [("CARD", 30m, 0m), ("ON_ACCOUNT", 20m, 0m)]);
        await h.OrderAsync(shift.Uuid, PosOrderType.Sale, 10m, PosOrderStatus.Voided, id, ("CASH", 10m, 0m));
        await h.OrderAsync(shift.Uuid, PosOrderType.Return, 15m, sessionId: id, tenders: [("CASH", 15m, 0m)]);

        var model = (await h.Shifts.GetAsync(shift.Uuid, AdminCaller)).Sessions.Single(s => s.Uuid == session.Uuid);
        model.Should().BeEquivalentTo(new { CashSales = 80m, CardSales = 30m, CreditSales = 20m, MobileSales = 0m, RefundTotal = 15m, OrderCount = 2 });
    }
}
