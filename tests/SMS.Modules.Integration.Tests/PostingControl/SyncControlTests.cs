using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Exceptions;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.PostingControl;

/// <summary>A44 D-14 sync control — T-62..T-68: defaults, reader, hold / release, the old switches kept in step.</summary>
public class SyncControlTests : IAsyncLifetime
{
    private readonly FakeReleaser _releaser = new();
    private readonly SyncHarness  _h;
    private IntegrationConnection _connection = null!;

    public SyncControlTests() => _h = PostingControlKit.Harness(releaser: _releaser);

    public async Task InitializeAsync() => _connection = await _h.ConnectAsync();
    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<T> Control<T>(Func<IQuickBooksSyncControlService, Task<T>> call) =>
        _h.Scoped(sp => call(sp.GetRequiredService<IQuickBooksSyncControlService>()));

    private Task<SyncControlDecision> CheckAsync(string type) =>
        _h.Scoped(sp => sp.GetRequiredService<ISyncControlReader>().CheckAsync(_connection.Id, type));

    private async Task<IntegrationSettings> SettingsAsync()
    {
        await using var db = _h.SuperDb();
        return await db.Settings.AsNoTracking().SingleAsync(s => s.ConnectionId == _connection.Id);
    }

    [Fact]
    public async Task T62_a_new_connection_is_seeded_with_the_defaults_and_its_old_switches_match()
    {
        // A second organization connecting for the first time: its settings row is created by the accessor.
        var org = Guid.NewGuid();
        await using (var db = _h.DbAs(org))
        {
            db.Connections.Add(new IntegrationConnection { OrganizationId = org, RealmId = "222", Status = ConnectionStatus.NeedsSetup });
            await db.SaveChangesAsync();
        }
        _h.Tenant.RequestOrganizationId = org;
        await _h.Scoped(async sp =>
        {
            var accessor = sp.GetRequiredService<IConnectionAccessor>();
            await accessor.GetOrCreateSettingsAsync((await accessor.GetCurrentAsync())!);
        });

        var rows = (await Control(c => c.GetAsync())).Rows.ToDictionary(r => r.DocumentType);
        _h.Tenant.RequestOrganizationId = _h.OrgId;

        rows.Should().HaveCount(11).And.OnlyContain(r => !r.Value.IsDefault);
        foreach (var on in new[] { "Customer", "Vendor", "Item", "SalesInvoice", "CreditNote", "Payment" })
            rows[on].Action.Should().Be("Process", on);
        foreach (var held in new[] { "POSSale", "POSReturn" })
            rows[held].Action.Should().Be("Hold", held);
        foreach (var off in new[] { "PurchaseBill", "BillPayment", "VendorCredit" })
            rows[off].Action.Should().Be("Skip", off);

        await using var check = _h.SuperDb();
        var settings = await check.Settings.AsNoTracking().SingleAsync(s => s.OrganizationId == org);
        (settings.AutoPushBills, settings.AutoPushPosSales, settings.AutoPushSalesInvoices).Should().Be((false, false, true));
    }

    [Fact]
    public async Task T63_without_a_row_a_type_follows_the_old_switch_and_a_row_wins()
    {
        await _h.UpdateSettingsAsync(s => s.AutoPushBills = false);

        (await CheckAsync("PurchaseBill")).Action.Should().Be(SyncControlAction.Skip);
        (await CheckAsync("VendorCredit")).IsEnabled.Should().BeFalse("vendor credits follow bills while they have no row");
        (await CheckAsync("Payment")).IsEnabled.Should().BeTrue();
        (await CheckAsync("POSSale")).Action.Should().Be(SyncControlAction.Hold, "POS sales are off by default and held");
        (await CheckAsync("purchasebill")).IsEnabled.Should().BeFalse("any case");

        await Control(c => c.UpdateAsync("PurchaseBill", new UpdateSyncControlRequest { IsEnabled = true }, 7));

        (await CheckAsync("PurchaseBill")).Action.Should().Be(SyncControlAction.Process);
        (await SettingsAsync()).AutoPushBills.Should().BeTrue("the old switch is kept in step (R-1)");
    }

    [Fact]
    public async Task T64_disabling_with_hold_holds_what_is_queued_and_without_hold_does_not()
    {
        var held = await Control(c => c.UpdateAsync("Payment", new UpdateSyncControlRequest { IsEnabled = false, HoldWhenDisabled = true }, 7));
        held.Row.Action.Should().Be("Hold");
        held.HeldNow.Should().Be(2);
        _releaser.HeldQueued.Should().Equal("Payment");

        var skipped = await Control(c => c.UpdateAsync("CreditNote", new UpdateSyncControlRequest { IsEnabled = false }, 7));
        skipped.Row.Action.Should().Be("Skip");
        skipped.HeldNow.Should().Be(0);
        _releaser.HeldQueued.Should().ContainSingle();
    }

    [Fact]
    public async Task T65_enabling_a_type_with_held_records_asks_first_and_sends_them_once_confirmed()
    {
        await Control(c => c.UpdateAsync("POSSale", new UpdateSyncControlRequest { IsEnabled = false, HoldWhenDisabled = true }, 7));
        _releaser.Held["POSSale"] = 3;

        var refused = async () => await Control(c => c.UpdateAsync("POSSale", new UpdateSyncControlRequest { IsEnabled = true }, 7));
        var conflict = (await refused.Should().ThrowAsync<IntegrationConflictException>()).Which;
        conflict.Detail.Should().BeEquivalentTo(new ReleaseConfirmationModel { DocumentType = "POSSale", HeldCount = 3 });
        (await CheckAsync("POSSale")).IsEnabled.Should().BeFalse("nothing changes until it is confirmed");

        var result = await Control(c => c.UpdateAsync("POSSale", new UpdateSyncControlRequest { IsEnabled = true, ConfirmRelease = true }, 7));

        result.Released.Should().Be(3);
        result.Row.HeldCount.Should().Be(0);
        (await SettingsAsync()).AutoPushPosSales.Should().BeTrue();
    }

    [Fact]
    public async Task T66_GET_shows_held_counts_per_type_and_in_total()
    {
        _releaser.Held["POSSale"]   = 4;
        _releaser.Held["POSReturn"] = 1;

        var model = await Control(c => c.GetAsync());

        model.Rows.Single(r => r.DocumentType == "POSSale").HeldCount.Should().Be(4);
        model.TotalHeld.Should().Be(5);
        model.Rows.Should().OnlyContain(r => r.IsDefault, "this connection has no rows: the old switches are shown");
    }

    [Fact]
    public async Task T67_release_is_refused_while_a_type_is_off_and_sends_while_it_is_on()
    {
        var off = async () => await Control(c => c.ReleaseAsync("POSSale", 7));
        await off.Should().ThrowAsync<ConflictException>();

        await Control(c => c.UpdateAsync("Payment", new UpdateSyncControlRequest { IsEnabled = true }, 7));
        _releaser.Held["Payment"] = 2;
        var result = await Control(c => c.ReleaseAsync("payment", 7));

        result.Should().BeEquivalentTo(new SyncControlReleaseResult { DocumentType = "Payment", Released = 2, StillHeld = 0 });
    }

    [Fact]
    public async Task T68_an_unknown_document_type_is_refused_and_every_change_is_audited()
    {
        var unknown = async () => await Control(c => c.UpdateAsync("Cheese", new UpdateSyncControlRequest(), 7));
        await unknown.Should().ThrowAsync<BadRequestException>();

        await Control(c => c.UpdateAsync("Item", new UpdateSyncControlRequest { IsEnabled = false }, 7));

        await using var db = _h.SuperDb();
        var audit = await db.SettingsAudit.AsNoTracking().SingleAsync(a => a.Area == AuditAreas.SyncControl);
        audit.BeforeJson.Should().Contain("\"isEnabled\":true");
        audit.AfterJson.Should().Contain("\"isEnabled\":false");
        audit.UserId.Should().Be(7);
    }

    [Fact]
    public async Task Turning_a_type_on_later_asks_only_for_changes_since_the_last_reconciliation_never_the_history()
    {
        _h.Source.Kinds.Add(SyncKind.Payment);
        await Control(c => c.UpdateAsync("Payment", new UpdateSyncControlRequest { IsEnabled = false }, 7));

        await _h.RunReconciliationAsync();
        _h.Source.PushAllCalls.Should().NotContain(c => c.Kind == SyncKind.Payment, "off: not asked at all");
        var firstRun = (await SettingsAsync()).LastReconciledAt;
        firstRun.Should().NotBeNull();

        await Control(c => c.UpdateAsync("Payment", new UpdateSyncControlRequest { IsEnabled = true }, 7));
        _h.Clock.Advance(TimeSpan.FromHours(1));
        await _h.RunReconciliationAsync();

        _h.Source.PushAllCalls.Should().ContainSingle(c => c.Kind == SyncKind.Payment)
            .Which.Since.Should().Be(firstRun, "no historic backfill: only what changed since the last complete run");
        _releaser.Released.Should().Contain("Payment");
    }

    [Fact]
    public async Task The_old_settings_save_writes_only_the_switches_it_changed_onto_their_rows()
    {
        // The new tab turned customer payments off; the old screen then saves with bills turned off.
        await Control(c => c.UpdateAsync("Payment", new UpdateSyncControlRequest { IsEnabled = false }, 7));

        await _h.Scoped(sp => sp.GetRequiredService<IIntegrationSettingsService>().UpdateSettingsAsync(new UpdateIntegrationSettingsRequest
        {
            AutoPushCustomers = true, AutoPushVendors = true, AutoPushItems = true, AutoPushSalesInvoices = true,
            AutoPushBills = false, ItemTypeDefault = "NonInventory", PartnerScope = "OnlyWhenReferenced"
        }, 7));

        (await CheckAsync("PurchaseBill")).IsEnabled.Should().BeFalse();
        (await CheckAsync("Payment")).IsEnabled.Should().BeFalse("a switch the old screen does not know is left alone");
        await using var db = _h.SuperDb();
        (await db.SyncControlConfigs.AsNoTracking().Select(c => c.DocumentType).ToListAsync())
            .Should().BeEquivalentTo("Payment", "PurchaseBill");
    }
}
