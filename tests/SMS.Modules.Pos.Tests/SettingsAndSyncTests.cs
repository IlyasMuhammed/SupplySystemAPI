using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Hubs;
using SMS.Modules.Pos.Models;
using SMS.Modules.Pos.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Pos.Tests.PosHarness;

namespace SMS.Modules.Pos.Tests;

/// <summary>A38 API-CONTRACT §6–§8 — POS settings, PWA sync pulls, change listener routing.</summary>
public class SettingsAndSyncTests
{
    // Master-data stamps in the past: POS settings rows are stamped with the real clock (StampModifiedAt).
    private static DateTime T(int minute) => new(2020, 1, 1, 0, minute, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Product_settings_defaults_upsert_validation_and_bulk()
    {
        var h = new PosHarness();
        var item = h.Catalog.Add("A", 10m);

        var defaults = await h.Settings.GetProductAsync(item.ProductUuid);
        (defaults.IsPosActive, defaults.ModifiedAt).Should().Be((true, null));
        await FluentActions.Awaiting(() => h.Settings.GetProductAsync(Guid.NewGuid())).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => h.Settings.PutProductAsync(item.ProductUuid, new PosProductSettingsModel { Color = "red" }, Admin))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Colour must be #RRGGBB.");
        await FluentActions.Awaiting(() => h.Settings.PutProductAsync(item.ProductUuid, new PosProductSettingsModel { PosCategoryId = 99 }, Admin))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Unknown POS category.");

        var saved = await h.Settings.PutProductAsync(item.ProductUuid,
            new PosProductSettingsModel { PosName = " Cola ", Color = "#ff0000", PosCategoryId = 2, IsFavorite = true, IsPosActive = false }, Admin);
        (saved.PosName, saved.Color, saved.IsFavorite, saved.IsPosActive).Should().Be(("Cola", "#FF0000", true, false));
        h.Hub.Sent.Should().Contain(e => e.Event == PosHubEvents.ProductDeactivated && e.Target == item.VariantUuid);

        var stamp = saved.ModifiedAt!.Value;
        await Task.Delay(20);
        var again = await h.Settings.PutProductAsync(item.ProductUuid,
            new PosProductSettingsModel { PosName = "Cola", Color = "#FF0000", PosCategoryId = 2, IsFavorite = true, IsPosActive = false }, Admin);
        again.ModifiedAt.Should().BeAfter(stamp); // bumped even without a change

        var b = h.Catalog.Add("B", 5m);
        var bulk = await h.Settings.BulkProductsAsync(new PosProductSettingsBulkRequest
        {
            Items = [new() { ProductUuid = item.ProductUuid, SortOrder = 1 }, new() { ProductUuid = b.ProductUuid, SortOrder = 2 }]
        }, Admin);
        bulk.Updated.Should().Be(2);
        (await h.Db.ProductSettings.CountAsync()).Should().Be(2);
        await FluentActions.Awaiting(() => h.Settings.BulkProductsAsync(new PosProductSettingsBulkRequest { Items = [new() { SortOrder = 1 }] }, Admin))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Customer_settings_and_the_walk_in_rule()
    {
        var h = new PosHarness();
        var acme = h.Customers.Add("ACME", 100m);
        (await h.Settings.GetCustomerAsync(acme.Uuid)).AllowPosCredit.Should().BeFalse();
        var saved = await h.Settings.PutCustomerAsync(acme.Uuid, new PosCustomerSettingsModel { AllowPosCredit = true, MaxSingleCreditOrder = 50m }, Admin);
        (saved.AllowPosCredit, saved.MaxSingleCreditOrder).Should().Be((true, 50m));

        await FluentActions.Awaiting(() => h.Settings.PutCustomerAsync(h.Customers.WalkIn.Uuid, new PosCustomerSettingsModel { AllowPosCredit = true }, Admin))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Walk-In customers cannot use credit.");
        (await h.Settings.PutCustomerAsync(h.Customers.WalkIn.Uuid, new PosCustomerSettingsModel { RequiresIdForCredit = true }, Admin))
            .RequiresIdForCredit.Should().BeTrue();
        await FluentActions.Awaiting(() => h.Settings.PutCustomerAsync(Guid.NewGuid(), new PosCustomerSettingsModel(), Admin))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Config_reports_tax_features_and_methods()
    {
        var h = new PosHarness();
        var (_, terminal, _) = await h.OpenShiftAsync();
        h.Gate.Off.Add(PosFeatureCodes.CreditSales);
        var config = await h.Sync.ConfigAsync(terminal.Uuid);
        (config.TaxCode.Code, config.TaxCode.Percent).Should().Be(("VAT17", 17m));
        config.Features.Should().Match<PosSyncFeaturesModel>(f => !f.CreditSales && f.Returns && f.Barcode && f.MultiStore);
        config.PaymentMethods.Should().NotContain(PosPaymentMethod.OnAccount).And.Contain(PosPaymentMethod.Mobile);
        config.Currency.Decimals.Should().Be(2);
        await FluentActions.Awaiting(() => h.Sync.ConfigAsync(Guid.NewGuid())).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Catalog_merges_settings_marks_usability_and_pages_without_skipping_ties()
    {
        var h = new PosHarness();
        var (_, terminal, _) = await h.OpenShiftAsync();
        var a = h.Catalog.Add("A", 1m, modifiedAt: T(1));
        var b = h.Catalog.Add("B", 2m, modifiedAt: T(2));
        var c = h.Catalog.Add("C", 3m, modifiedAt: T(2)); // tie with B
        var d = h.Catalog.Add("D", 4m, modifiedAt: T(3), saleable: false);

        var page1 = await h.Sync.CatalogAsync(terminal.Uuid, null, 2);
        page1.HasMore.Should().BeTrue();
        page1.Items.Select(i => i.Sku).Should().Equal("A", "B", "C"); // the tie run is completed
        page1.NextSince.Should().Be(T(2));
        page1.Categories.Select(x => x.Name).Should().Equal("Drinks", "Snacks");

        var page2 = await h.Sync.CatalogAsync(terminal.Uuid, page1.NextSince, 2);
        page2.HasMore.Should().BeFalse();
        page2.Items.Should().ContainSingle().Which.Should().Match<PosCatalogItemModel>(i => i.Sku == "D" && !i.IsActive);
        page2.NextSince.Should().Be(T(3));
        (await h.Sync.CatalogAsync(terminal.Uuid, page2.NextSince, 2)).Items.Should().BeEmpty();

        // A POS-settings change brings the product's variants back in the next delta, settings applied.
        await h.Settings.PutProductAsync(a.ProductUuid, new PosProductSettingsModel { PosName = "Apple", Color = "#00FF00", PosCategoryId = 2 }, Admin);
        var delta = await h.Sync.CatalogAsync(terminal.Uuid, page2.NextSince, 500);
        delta.Items.Should().ContainSingle().Which.Should().Match<PosCatalogItemModel>(i =>
            i.VariantUuid == a.VariantUuid && i.PosName == "Apple" && i.CategoryId == 2 && i.CategoryName == "Snacks" && i.IsActive && i.ModifiedAt > T(3));
        delta.NextSince.Should().Be(delta.Items[0].ModifiedAt);

        (await h.Db.Terminals.SingleAsync()).LastSyncAt.Should().Be(h.Clock.UtcNow);
        _ = (b, c, d);
    }

    [Fact]
    public async Task Stock_snapshot_is_the_store_warehouse()
    {
        var h = new PosHarness();
        var (store, terminal, _) = await h.OpenShiftAsync();
        var v = Guid.NewGuid();
        h.Stock.Levels[v] = -2m;
        var snapshot = await h.Sync.StockAsync(terminal.Uuid);
        snapshot.Items.Should().ContainSingle(i => i.VariantUuid == v && i.Available == -2m);
        h.Stock.WarehousesAsked.Should().Equal(store.WarehouseUuid);
    }

    [Fact]
    public async Task Customers_carry_settings_and_balance_and_page_by_either_source()
    {
        var h = new PosHarness();
        var (_, terminal, _) = await h.OpenShiftAsync();
        var acme = h.Customers.Add("ACME", 100m, modifiedAt: T(5));
        h.Balances.Balances[acme.Uuid] = 42m;

        var all = await h.Sync.CustomersAsync(terminal.Uuid, null, 500);
        all.Customers.Select(c => c.Code).Should().Equal("C-WALKIN", "ACME");
        all.Customers[0].Should().Match<PosSyncCustomerModel>(c => c.IsSystem && !c.AllowPosCredit);
        all.Customers[1].Should().Match<PosSyncCustomerModel>(c => c.Balance == 42m && c.CreditLimit == 100m);
        all.NextSince.Should().Be(T(5));

        await h.Settings.PutCustomerAsync(acme.Uuid, new PosCustomerSettingsModel { AllowPosCredit = true }, Admin);
        var delta = await h.Sync.CustomersAsync(terminal.Uuid, all.NextSince, 500);
        delta.Customers.Should().ContainSingle().Which.Should().Match<PosSyncCustomerModel>(c => c.Uuid == acme.Uuid && c.AllowPosCredit);

        var paged = await h.Sync.CustomersAsync(terminal.Uuid, null, 1);
        (paged.HasMore, paged.Customers.Count).Should().Be((true, 1));
    }

    [Fact]
    public async Task Users_only_for_the_bound_device_with_pin_holders_who_can_sell()
    {
        var h = new PosHarness();
        var (store, terminal, _) = await h.OpenShiftAsync(); // Cathy 1111 (cashier), Sue 2222 (supervisor)
        await h.StaffAsync(store.Uuid, Cashier2);           // no PIN
        var device = Guid.NewGuid();

        await FluentActions.Awaiting(() => h.Sync.UsersAsync(terminal.Uuid, device.ToString())).Should().ThrowAsync<ForbiddenException>(); // unbound
        await h.Stores.BindAsync(terminal.Uuid, device, AdminCaller);
        await FluentActions.Awaiting(() => h.Sync.UsersAsync(terminal.Uuid, null)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(() => h.Sync.UsersAsync(terminal.Uuid, Guid.NewGuid().ToString())).Should().ThrowAsync<ForbiddenException>();

        var users = await h.Sync.UsersAsync(terminal.Uuid, device.ToString());
        users.Select(u => (u.UserId, u.PosRole, u.PinHash)).Should().BeEquivalentTo(
            [(Cashier, "CASHIER", "hash:" + Cashier), (Supervisor, "SUPERVISOR", "hash:" + Supervisor)]);
        users.Single(u => u.UserId == Cashier).Permissions.Should().Contain(PosPermissions.OrderCreate);
    }

    [Fact]
    public async Task Change_listener_routes_org_wide_and_store_events()
    {
        var h = new PosHarness();
        var (store, _, _) = await h.OpenShiftAsync();
        var usable = h.Catalog.Add("U", 1m);
        var off = h.Catalog.Add("O", 1m, pos: false);
        var gone = Guid.NewGuid();

        await h.Changes.OnVariantsChangedAsync(h.Org, [usable.VariantUuid, off.VariantUuid, gone]);
        h.Hub.Sent.Should().Contain((PosHubEvents.ProductUpdated, usable.VariantUuid, ""))
            .And.Contain((PosHubEvents.ProductDeactivated, off.VariantUuid, ""))
            .And.Contain((PosHubEvents.ProductDeactivated, gone, ""));

        await h.Changes.OnPriceChangedAsync(h.Org, usable.VariantUuid, 1m, 2m);
        h.Hub.Sent.Should().Contain((PosHubEvents.PriceChanged, usable.VariantUuid, ""));

        h.Stock.Levels[usable.VariantUuid] = 7m;
        h.Hub.Sent.Clear();
        await h.Changes.OnStockChangedAsync(h.Org, store.WarehouseUuid, [usable.VariantUuid]);
        h.Hub.Sent.Should().ContainSingle(e => e.Event == PosHubEvents.StockUpdated && e.Target == store.Uuid);
        h.Hub.Sent.Clear();
        await h.Changes.OnStockChangedAsync(h.Org, Guid.NewGuid(), [usable.VariantUuid]); // no store on that warehouse
        await h.Changes.OnStockChangedAsync(Guid.NewGuid(), store.WarehouseUuid, [usable.VariantUuid]); // another org
        h.Hub.Sent.Should().BeEmpty();

        var acme = h.Customers.Add("ACME", 100m);
        await h.Changes.OnCustomerBalanceChangedAsync(h.Org, acme.Uuid);
        h.Hub.Sent.Should().ContainSingle(e => e.Event == PosHubEvents.CustomerBalanceChanged && e.Target == acme.Uuid);
    }

    [Fact]
    public async Task Real_notifier_sends_to_the_documented_groups()
    {
        var org = Guid.NewGuid();
        var store = Guid.NewGuid();
        var clients = new RecordingClients();
        var notifier = new PosHubNotifier(new RecordingHubContext(clients));
        await notifier.PriceChangedAsync(org, Guid.NewGuid(), 1m, 2m);
        await notifier.StockUpdatedAsync(org, store, Guid.NewGuid(), 3m);
        await notifier.CustomerBalanceChangedAsync(org, Guid.NewGuid(), 1m, 2m);
        clients.Groups.Should().Equal($"org_{org}_pos", $"org_{org}_store_{store}", $"org_{org}_pos");
    }

    private sealed class RecordingClients : Microsoft.AspNetCore.SignalR.IHubClients
    {
        public List<string> Groups { get; } = [];
        private sealed class Proxy : Microsoft.AspNetCore.SignalR.IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) => Task.CompletedTask;
        }
        public Microsoft.AspNetCore.SignalR.IClientProxy Group(string groupName) { Groups.Add(groupName); return new Proxy(); }
        public Microsoft.AspNetCore.SignalR.IClientProxy All => new Proxy();
        public Microsoft.AspNetCore.SignalR.IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new Proxy();
        public Microsoft.AspNetCore.SignalR.IClientProxy Client(string connectionId) => new Proxy();
        public Microsoft.AspNetCore.SignalR.IClientProxy Clients(IReadOnlyList<string> connectionIds) => new Proxy();
        public Microsoft.AspNetCore.SignalR.IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new Proxy();
        Microsoft.AspNetCore.SignalR.IClientProxy Microsoft.AspNetCore.SignalR.IHubClients<Microsoft.AspNetCore.SignalR.IClientProxy>.Groups(IReadOnlyList<string> groupNames) => new Proxy();
        public Microsoft.AspNetCore.SignalR.IClientProxy User(string userId) => new Proxy();
        public Microsoft.AspNetCore.SignalR.IClientProxy Users(IReadOnlyList<string> userIds) => new Proxy();
    }

    private sealed class RecordingHubContext(RecordingClients clients) : Microsoft.AspNetCore.SignalR.IHubContext<PosHub>
    {
        public Microsoft.AspNetCore.SignalR.IHubClients Clients => clients;
        public Microsoft.AspNetCore.SignalR.IGroupManager Groups => throw new NotSupportedException();
    }
}
