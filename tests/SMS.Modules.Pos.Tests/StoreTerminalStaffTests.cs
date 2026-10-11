using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Hubs;
using SMS.Modules.Pos.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Pos.Tests.PosHarness;

namespace SMS.Modules.Pos.Tests;

/// <summary>A38 API-CONTRACT §2 — stores, terminals, staff and PINs.</summary>
public class StoreTerminalStaffTests
{
    // ── stores ──

    [Fact]
    public async Task Create_store_snapshots_warehouse_and_finance_default_tax_code()
    {
        var h = new PosHarness();
        var store = await h.StoreAsync("s1", "Asia/Karachi");

        store.Code.Should().Be("S1");
        store.WarehouseName.Should().Be("Warehouse Ws1");
        store.DefaultTaxCodeUuid.Should().Be(h.VatUuid);
        store.DefaultTaxCode.Should().Be("VAT17");
        store.DefaultTaxPercent.Should().Be(17m);
        store.Timezone.Should().Be("Asia/Karachi");
        store.IsActive.Should().BeTrue();
        store.AutoCloseShiftHours.Should().Be(12);
    }

    [Fact]
    public async Task Store_rules_refuse_unknown_warehouse_timezone_duplicate_code_and_purchase_tax_code()
    {
        var h = new PosHarness();
        await h.StoreAsync("S1");
        var purchase = Guid.NewGuid();
        h.TaxCodes.Codes.Add(new TaxCodeInfo(purchase, "IN5", "Input", 5m, TaxCodeUsage.Purchase, false, true));

        await FluentActions.Awaiting(() => h.Stores.CreateStoreAsync(h.StoreRequest("S2", Guid.NewGuid()), Admin))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Warehouse not found.");
        await FluentActions.Awaiting(() => h.Stores.CreateStoreAsync(h.StoreRequest("S2", timezone: "Mars/Olympus"), Admin))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Unknown time zone*");
        await FluentActions.Awaiting(() => h.Stores.CreateStoreAsync(h.StoreRequest("S1"), Admin))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Store code S1 already exists.");
        var req = h.StoreRequest("S3");
        req.DefaultTaxCodeUuid = purchase;
        await FluentActions.Awaiting(() => h.Stores.CreateStoreAsync(req, Admin))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Default tax code must be an active sales tax code.");
    }

    [Fact]
    public async Task A_warehouse_backs_only_one_active_store()
    {
        var h = new PosHarness();
        var s1 = await h.StoreAsync("S1");

        await FluentActions.Awaiting(() => h.Stores.CreateStoreAsync(h.StoreRequest("S2", s1.WarehouseUuid), Admin))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Warehouse is already linked to store S1.");

        // Inactive stores do not hold the warehouse; activating one that clashes is refused.
        var req = h.StoreRequest("S2", s1.WarehouseUuid);
        req.IsActive = false;
        var s2 = await h.Stores.CreateStoreAsync(req, Admin);
        await FluentActions.Awaiting(() => h.Stores.SetStoreStatusAsync(s2.Uuid, true, Admin))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Warehouse is already linked to store S1.");

        await h.Stores.SetStoreStatusAsync(s1.Uuid, false, Admin);
        (await h.Stores.SetStoreStatusAsync(s2.Uuid, true, Admin)).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Multi_store_off_allows_one_active_store()
    {
        var h = new PosHarness();
        h.Gate.Off.Add(PosFeatureCodes.MultiStore);
        var s1 = await h.StoreAsync("S1");

        await FluentActions.Awaiting(() => h.StoreAsync("S2"))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Multi-store is not enabled*");

        var req = h.StoreRequest("S2");
        req.IsActive = false;
        var s2 = await h.Stores.CreateStoreAsync(req, Admin);
        await FluentActions.Awaiting(() => h.Stores.SetStoreStatusAsync(s2.Uuid, true, Admin))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Multi-store is not enabled*");

        h.Gate.Off.Clear();
        (await h.Stores.SetStoreStatusAsync(s2.Uuid, true, Admin)).IsActive.Should().BeTrue();
        (await h.Stores.ListStoresAsync(new PosStoreListFilter { IsActive = true })).TotalRecords.Should().Be(2);
        _ = s1;
    }

    [Fact]
    public async Task Deactivating_a_store_is_refused_with_an_open_shift_and_deactivates_its_terminals()
    {
        var h = new PosHarness();
        var (store, terminal, shift) = await h.OpenShiftAsync();
        await h.TerminalAsync(store.Uuid, "T2");

        await FluentActions.Awaiting(() => h.Stores.SetStoreStatusAsync(store.Uuid, false, Admin))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Close all shifts before deactivating the store.");

        await h.Shifts.StartCloseAsync(shift.Uuid, AdminCaller);
        await FluentActions.Awaiting(() => h.Stores.SetStoreStatusAsync(store.Uuid, false, Admin))
            .Should().ThrowAsync<BadRequestException>(); // CLOSING still counts
        await h.Shifts.CloseAsync(shift.Uuid, new PosCloseShiftRequest { ActualCashClosing = 100m }, AdminCaller);

        var result = await h.Stores.SetStoreStatusAsync(store.Uuid, false, Admin);
        result.IsActive.Should().BeFalse();
        result.TerminalCount.Should().Be(0);
        (await h.Db.Terminals.CountAsync(t => t.IsActive)).Should().Be(0);
        h.Hub.Sent.Should().Contain(e => e.Event == PosHubEvents.ConfigChanged && e.Detail == "STORE");
        _ = terminal;
    }

    [Fact]
    public async Task Store_update_with_a_stale_row_version_is_a_conflict()
    {
        var h = new PosHarness();
        var store = await h.StoreAsync();
        var req = h.StoreRequest("S1", store.WarehouseUuid);
        req.RowVersion = [1, 2, 3, 4, 5, 6, 7, 8];
        await FluentActions.Awaiting(() => h.Stores.UpdateStoreAsync(store.Uuid, req, Admin)).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Store_warehouse_cannot_change_once_it_has_shifts()
    {
        var h = new PosHarness();
        var (store, _, _) = await h.OpenShiftAsync();
        await FluentActions.Awaiting(() => h.Stores.UpdateStoreAsync(store.Uuid, h.StoreRequest("S1"), Admin))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The warehouse cannot be changed*");
    }

    // ── terminals ──

    [Fact]
    public async Task Terminal_code_is_unique_within_a_store_only()
    {
        var h = new PosHarness();
        var s1 = await h.StoreAsync("S1");
        var s2 = await h.StoreAsync("S2");
        await h.TerminalAsync(s1.Uuid, "T1");

        await FluentActions.Awaiting(() => h.TerminalAsync(s1.Uuid, "t1"))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Terminal code already exists in this store.");
        (await h.TerminalAsync(s2.Uuid, "T1")).Code.Should().Be("T1");
    }

    [Fact]
    public async Task Bind_unbind_and_device_rules()
    {
        var h = new PosHarness();
        var store = await h.StoreAsync();
        var t1 = await h.TerminalAsync(store.Uuid, "T1");
        var t2 = await h.TerminalAsync(store.Uuid, "T2");
        Guid devA = Guid.NewGuid(), devB = Guid.NewGuid();

        var config = await h.Stores.BindAsync(t1.Uuid, devA, AdminCaller);
        config.Terminal.DeviceBound.Should().BeTrue();
        config.Store.Code.Should().Be("S1");
        (await h.Stores.BindAsync(t1.Uuid, devA, AdminCaller)).Terminal.DeviceUuid.Should().Be(devA); // idempotent

        await FluentActions.Awaiting(() => h.Stores.BindAsync(t1.Uuid, devB, AdminCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*Unbind first.");
        await FluentActions.Awaiting(() => h.Stores.BindAsync(t2.Uuid, devA, AdminCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("This device is already bound to terminal T1*");

        (await h.Stores.GetByDeviceAsync(devA))!.Terminal.Uuid.Should().Be(t1.Uuid);
        (await h.Stores.UnbindAsync(t1.Uuid)).DeviceBound.Should().BeFalse();
        h.Hub.Sent.Should().Contain(e => e.Event == PosHubEvents.ForceLogout && e.Target == t1.Uuid);
        (await h.Stores.GetByDeviceAsync(devA)).Should().BeNull();
        (await h.Stores.BindAsync(t1.Uuid, devB, AdminCaller)).Terminal.DeviceUuid.Should().Be(devB);
    }

    [Fact]
    public async Task Binding_with_shift_open_only_needs_store_staff()
    {
        var h = new PosHarness();
        var store = await h.StoreAsync();
        var t1 = await h.TerminalAsync(store.Uuid);
        var supervisor = Caller(Supervisor, PosPermissions.ShiftOpen);

        await FluentActions.Awaiting(() => h.Stores.BindAsync(t1.Uuid, Guid.NewGuid(), supervisor)).Should().ThrowAsync<ForbiddenException>();
        await h.StaffAsync(store.Uuid, Supervisor, PosRole.Supervisor);
        (await h.Stores.BindAsync(t1.Uuid, Guid.NewGuid(), supervisor)).Terminal.DeviceBound.Should().BeTrue();
    }

    [Fact]
    public async Task Terminal_row_version_ignores_presence_and_detects_edits()
    {
        var h = new PosHarness();
        var store = await h.StoreAsync();
        var t = await h.TerminalAsync(store.Uuid);
        var entity = await h.Db.Terminals.FirstAsync(x => x.Uuid == t.Uuid);
        entity.LastSeenAt = h.Clock.UtcNow;
        entity.PinFailedAttempts = 2;
        await h.Db.SaveChangesAsync();

        var updated = await h.Stores.UpdateTerminalAsync(t.Uuid, new PosTerminalUpdateRequest { Code = "T1", Name = "Front till", IsActive = true, RowVersion = t.RowVersion });
        updated.Name.Should().Be("Front till");
        updated.IsOnline.Should().BeTrue();
        await FluentActions.Awaiting(() => h.Stores.UpdateTerminalAsync(t.Uuid, new PosTerminalUpdateRequest { Code = "T1", Name = "x", RowVersion = t.RowVersion }))
            .Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Terminal_with_an_active_shift_cannot_be_deactivated_and_status_reports_it()
    {
        var h = new PosHarness();
        var (_, terminal, shift) = await h.OpenShiftAsync();
        await FluentActions.Awaiting(() => h.Stores.UpdateTerminalAsync(terminal.Uuid, new PosTerminalUpdateRequest { Code = "T1", Name = "x", IsActive = false }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Close the terminal's shift*");
        var status = await h.Stores.GetStatusAsync(terminal.Uuid);
        status.ActiveShiftUuid.Should().Be(shift.Uuid);
        status.IsOnline.Should().BeFalse();
        status.DeviceBound.Should().BeFalse();
    }

    [Fact]
    public async Task Available_terminals_are_those_of_the_callers_stores_unless_store_view()
    {
        var h = new PosHarness();
        var s1 = await h.StoreAsync("S1");
        var s2 = await h.StoreAsync("S2");
        await h.TerminalAsync(s1.Uuid, "A");
        await h.TerminalAsync(s2.Uuid, "B");
        await h.StaffAsync(s1.Uuid, Cashier);

        (await h.Stores.ListAvailableAsync(Caller(Cashier, PosPermissions.OrderCreate))).Select(t => t.Code).Should().Equal("A");
        (await h.Stores.ListAvailableAsync(AdminCaller)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Force_sync_and_force_logout_push_to_the_terminal_and_end_its_session()
    {
        var h = new PosHarness();
        var (_, terminal, shift) = await h.OpenShiftAsync();
        await h.Sessions.StartAsync(new PosSessionStartRequest { ShiftUuid = shift.Uuid }, Caller(Cashier, PosPermissions.OrderCreate));

        await h.Stores.ForceSyncAsync(terminal.Uuid, "delta");
        await FluentActions.Awaiting(() => h.Stores.ForceSyncAsync(terminal.Uuid, "ALL")).Should().ThrowAsync<BadRequestException>();
        await h.Stores.ForceLogoutAsync(terminal.Uuid, null);

        h.Hub.Sent.Should().Contain(e => e.Event == PosHubEvents.ForceSync && e.Detail == "DELTA");
        h.Hub.Sent.Should().Contain(e => e.Event == PosHubEvents.ForceLogout && e.Target == terminal.Uuid);
        (await h.Db.CashierSessions.SingleAsync()).EndReason.Should().Be(PosSessionEndReason.ForcedLogout);
    }

    // ── staff & PINs ──

    [Theory]
    [InlineData("123")]
    [InlineData("1234567")]
    [InlineData("12a4")]
    [InlineData("12 34")]
    public async Task Pin_must_be_4_to_6_digits(string pin)
    {
        var h = new PosHarness();
        var store = await h.StoreAsync();
        await FluentActions.Awaiting(() => h.StaffAsync(store.Uuid, Cashier, pin: pin))
            .Should().ThrowAsync<BadRequestException>().WithMessage("PIN must be 4 to 6 digits.");
    }

    [Fact]
    public async Task Pin_is_unique_within_the_store_and_set_through_auth()
    {
        var h = new PosHarness();
        var s1 = await h.StoreAsync("S1");
        var s2 = await h.StoreAsync("S2");
        await h.StaffAsync(s1.Uuid, Cashier, pin: "4321");
        h.Pins.PinsByUser[Cashier].Should().Be("4321");

        await FluentActions.Awaiting(() => h.StaffAsync(s1.Uuid, Cashier2, pin: "4321"))
            .Should().ThrowAsync<BadRequestException>().WithMessage("This PIN is already used by another staff member of this store.");
        h.Pins.PinsByUser.Should().NotContainKey(Cashier2);

        // Another store, other people: the same PIN is fine.
        await h.StaffAsync(s2.Uuid, Cashier2, pin: "4321");
        h.Pins.PinsByUser[Cashier2].Should().Be("4321");
        // Re-saving a member's own PIN is not a clash.
        (await h.Stores.UpsertStaffAsync(s1.Uuid, Cashier, new PosStaffUpsertRequest { PosRole = "SUPERVISOR", Pin = "4321" }, Admin)).PosRole.Should().Be("SUPERVISOR");
    }

    [Fact]
    public async Task Pin_uniqueness_covers_every_store_the_user_works_in()
    {
        var h = new PosHarness();
        var s1 = await h.StoreAsync("S1");
        var s2 = await h.StoreAsync("S2");
        await h.StaffAsync(s1.Uuid, Cashier, pin: "5555");
        await h.StaffAsync(s2.Uuid, Cashier2);
        await h.StaffAsync(s1.Uuid, Cashier2);
        // Carl works in S1 too, where Cathy already has 5555 — editing him from S2 must still refuse it.
        await FluentActions.Awaiting(() => h.StaffAsync(s2.Uuid, Cashier2, pin: "5555")).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Staff_list_candidates_and_removal()
    {
        var h = new PosHarness();
        var store = await h.StoreAsync();
        await h.StaffAsync(store.Uuid, Cashier, pin: "1111");
        await FluentActions.Awaiting(() => h.StaffAsync(store.Uuid, Cashier2, "MANAGER")).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.StaffAsync(store.Uuid, 999)).Should().ThrowAsync<NotFoundException>();

        var staff = await h.Stores.ListStaffAsync(store.Uuid);
        staff.Should().ContainSingle().Which.Should().BeEquivalentTo(new { UserId = Cashier, Name = "Cathy", PosRole = "CASHIER", HasPin = true, IsActive = true });
        (await h.Stores.ListCandidatesAsync(store.Uuid, null)).Select(c => c.UserId).Should().NotContain(Cashier).And.Contain(Cashier2);

        await h.Stores.RemoveStaffAsync(store.Uuid, Cashier);
        (await h.Stores.ListStaffAsync(store.Uuid)).Single().IsActive.Should().BeFalse();
        (await h.Stores.ListCandidatesAsync(store.Uuid, "cat")).Select(c => c.UserId).Should().Contain(Cashier);
        h.Hub.Sent.Count(e => e.Detail == "STAFF").Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Another_organizations_store_is_not_found()
    {
        var h = new PosHarness();
        var store = await h.StoreAsync();
        var other = new PosHarness(dbName: h.DbName);
        await FluentActions.Awaiting(() => other.Stores.GetStoreAsync(store.Uuid)).Should().ThrowAsync<NotFoundException>();
    }
}
