using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using SMS.Modules.Integration.Core.Matching;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Providers.QuickBooks;
using SMS.Modules.Integration.Providers.QuickBooks.Mapping;
using SMS.Modules.Integration.Tests.Connections;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Modules.Integration.Tests.Setup;
using SMS.Shared.Exceptions;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Sync;

/// <summary>
/// "Sync POS sales" (A38): POS invoices are held while the switch is off, sent once it is turned on — by the settings
/// change and by the hourly reconciliation — exactly once; back-office invoices never notice. Plus service products,
/// which sync and match like any other product.
/// </summary>
public class PosSalesTests : IAsyncLifetime
{
    private readonly SyncHarness _h = new(configure: s =>
    {
        // The settings service and what it needs, over the harness's database (the harness itself has only the gateway).
        s.AddScoped<ReferenceDataService>();
        s.AddScoped<IPreflightService, PreflightService>();
        s.AddSingleton<IBaseCurrencyResolver>(new FixedBaseCurrency(true, "PKR"));
        s.AddScoped<IIntegrationSettingsService, IntegrationSettingsService>();
    });

    private IntegrationConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = await _h.ConnectAsync();
        await SeedReferenceAsync();

        // What SCM has: the customer and item every test invoice names.
        _h.Source.Has(TestPayloads.Customer());
        _h.Source.Has(TestPayloads.Item());
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SalesInvoicePayload Pos(string id = "POS-1", string doc = "INV-P-0001")
    {
        var p = TestPayloads.Invoice(id: id, doc: doc);
        p.SourceType  = SalesInvoicePayloadSources.Pos;
        p.PrivateNote = "POS " + id;
        return p;
    }

    /// <summary>A POS sale SCM has and sends now (the publisher's path).</summary>
    private async Task<GatewayResult> SellAsync(SalesInvoicePayload payload)
    {
        _h.Source.Has(payload);
        return await _h.Send(payload);
    }

    /// <summary>Saves the settings screen as an admin would: everything as it is, the switches as given.</summary>
    private Task<IntegrationSettingsModel> SaveSettingsAsync(bool? pos, bool invoices = true) =>
        _h.Scoped(sp => sp.GetRequiredService<IIntegrationSettingsService>().UpdateSettingsAsync(new UpdateIntegrationSettingsRequest
        {
            AutoPushCustomers        = true,
            AutoPushVendors          = true,
            AutoPushItems            = true,
            AutoPushSalesInvoices    = invoices,
            AutoPushBills            = true,
            AutoPushPosSales         = pos,
            ItemTypeDefault          = "NonInventory",
            PartnerScope             = "OnlyWhenReferenced",
            DefaultIncomeAccountId   = "1",
            DefaultExpenseAccountId  = "4",
            FreightExpenseAccountId  = "5",
            DiscountAccountId        = "2",
            DefaultPurchaseTaxCodeId = "10"
        }, 7));

    private Task<IntegrationSettingsModel> GetSettingsAsync() =>
        _h.Scoped(sp => sp.GetRequiredService<IIntegrationSettingsService>().GetSettingsAsync());

    private async Task SeedReferenceAsync()
    {
        var data = SampleReference.Data();
        await using var db = _h.DbAs(_h.OrgId);

        void Put(string kind, object? value) => db.ReferenceSnapshots.Add(new ReferenceSnapshot
        {
            OrganizationId = _h.OrgId,
            ConnectionId   = _connection.Id,
            Kind           = kind,
            Json           = JsonSerializer.Serialize(value, ReferenceDataService.Json),
            FetchedAt      = _h.Now
        });

        Put(ReferenceKinds.Accounts,    data.Accounts);
        Put(ReferenceKinds.TaxCodes,    data.TaxCodes);
        Put(ReferenceKinds.Terms,       data.Terms);
        Put(ReferenceKinds.Currencies,  data.Currencies);
        Put(ReferenceKinds.Preferences, data.Preferences);
        Put(ReferenceKinds.CompanyInfo, data.CompanyInfo);
        await db.SaveChangesAsync();
    }

    private int InvoiceCreates(string docNumber) =>
        _h.Provider.Calls.Count(c => c.Operation == "Create" && c.Entity is RemoteInvoice i && i.DocNumber == docNumber);

    // ── Held while off ───────────────────────────────────────────────────────

    [Fact]
    public async Task New_and_existing_organizations_start_with_POS_sales_off()
    {
        new IntegrationSettings().AutoPushPosSales.Should().BeFalse();
        (await GetSettingsAsync()).AutoPushPosSales.Should().BeFalse();
    }

    [Fact]
    public async Task While_off_a_POS_sale_is_held_stored_and_counted_but_nothing_is_queued_or_requested()
    {
        var result = await SellAsync(Pos());

        (result.Outcome, result.State).Should().Be((GatewayOutcome.Accepted, SyncState.NotSynced));
        var map = await _h.MapAsync(SyncKind.SalesInvoice, "POS-1");
        map.State.Should().Be(SyncState.NotSynced);
        map.LastErrorCode.Should().Be(PosSales.HeldCode);
        map.PayloadJson.Should().Contain("\"sourceType\":\"POS\"");
        (await _h.EntriesAsync(SyncKind.SalesInvoice, "POS-1")).Should().BeEmpty();
        (await _h.FindMapAsync(SyncKind.Customer, "C-1")).Should().BeNull("a held sale requests none of its dependencies");

        await _h.DrainAsync();
        _h.Provider.WriteCalls.Should().Be(0);
        (await GetSettingsAsync()).PosSalesWaiting.Should().Be(1);
    }

    [Fact]
    public async Task Back_office_invoices_are_unaffected_by_the_POS_switch()
    {
        _h.Source.Has(TestPayloads.Invoice(id: "SI-1", doc: "INV-0001"));
        var result = await _h.Send(TestPayloads.Invoice(id: "SI-1", doc: "INV-0001"));

        result.State.Should().Be(SyncState.WaitingOnDependency);
        (await _h.MapAsync(SyncKind.SalesInvoice, "SI-1")).PayloadJson.Should().NotContain("sourceType", "back-office payloads and fingerprints are unchanged");

        await _h.DrainAsync();
        InvoiceCreates("INV-0001").Should().Be(1);
    }

    [Fact]
    public async Task With_sales_invoices_off_a_POS_sale_does_not_go_even_with_POS_on()
    {
        await _h.UpdateSettingsAsync(s => { s.AutoPushPosSales = true; s.AutoPushSalesInvoices = false; });

        (await SellAsync(Pos())).Outcome.Should().Be(GatewayOutcome.Disabled);
        (await _h.FindMapAsync(SyncKind.SalesInvoice, "POS-1")).Should().BeNull();
    }

    [Fact]
    public async Task A_POS_sale_dated_before_the_document_start_date_is_never_held_or_sent()
    {
        await _h.UpdateSettingsAsync(s => s.DocumentStartDate = TestPayloads.TxnDate.AddDays(1));

        (await SellAsync(Pos())).Outcome.Should().Be(GatewayOutcome.Disabled);
        (await GetSettingsAsync()).PosSalesWaiting.Should().Be(0);
    }

    // ── Turning it on ────────────────────────────────────────────────────────

    [Fact]
    public async Task Turning_it_on_queues_every_held_sale_once_and_sends_it_once()
    {
        await SellAsync(Pos("POS-1", "INV-P-0001"));
        await SellAsync(Pos("POS-2", "INV-P-0002"));
        _h.Source.PushCalls.Should().BeEmpty();

        var model = await SaveSettingsAsync(pos: true);

        model.AutoPushPosSales.Should().BeTrue();
        model.PosSalesWaiting.Should().Be(0);
        _h.Source.PushCalls.Should().ContainSingle(c => c.Kind == SyncKind.SalesInvoice)
            .Which.Ids.Should().BeEquivalentTo("POS-1", "POS-2");
        foreach (var id in new[] { "POS-1", "POS-2" })
        {
            var map = await _h.MapAsync(SyncKind.SalesInvoice, id);
            map.LastErrorCode.Should().BeNull();
            map.State.Should().Be(SyncState.WaitingOnDependency);
            (await _h.EntriesAsync(SyncKind.SalesInvoice, id)).Should().ContainSingle();
        }

        // Saving again (and again) while on finds nothing held: no second entry.
        await SaveSettingsAsync(pos: true);
        await SaveSettingsAsync(pos: null);
        (await _h.EntriesAsync(SyncKind.SalesInvoice, "POS-1")).Should().ContainSingle();

        await _h.DrainAsync();
        InvoiceCreates("INV-P-0001").Should().Be(1);
        InvoiceCreates("INV-P-0002").Should().Be(1);
        (await _h.MapAsync(SyncKind.SalesInvoice, "POS-1")).State.Should().Be(SyncState.Synced);

        // Off and on again: already in QuickBooks, so nothing more is sent.
        await SaveSettingsAsync(pos: false);
        await SaveSettingsAsync(pos: true);
        await _h.RunReconciliationAsync();
        await _h.DrainAsync();
        InvoiceCreates("INV-P-0001").Should().Be(1);
        _h.Provider.CallsOf("Update").Should().Be(0);
    }

    [Fact]
    public async Task The_settings_change_is_audited_with_the_POS_switch()
    {
        await SaveSettingsAsync(pos: true);

        await using var db = _h.SuperDb();
        var audit = await db.SettingsAudit.OrderByDescending(a => a.Id).FirstAsync();
        audit.BeforeJson.Should().Contain("\"autoPushPosSales\":false");
        audit.AfterJson.Should().Contain("\"autoPushPosSales\":true");
    }

    [Fact]
    public async Task A_client_that_does_not_send_the_switch_leaves_it_as_it_is()
    {
        await SaveSettingsAsync(pos: true);
        (await SaveSettingsAsync(pos: null)).AutoPushPosSales.Should().BeTrue();
    }

    [Fact]
    public async Task Turning_sales_invoices_on_with_POS_already_on_releases_the_held_sales_too()
    {
        await _h.UpdateSettingsAsync(s => { s.AutoPushPosSales = true; s.AutoPushSalesInvoices = true; });
        await _h.UpdateSettingsAsync(s => s.AutoPushPosSales = false);
        await SellAsync(Pos());

        await SaveSettingsAsync(pos: true, invoices: false);
        (await _h.MapAsync(SyncKind.SalesInvoice, "POS-1")).LastErrorCode.Should().Be(PosSales.HeldCode, "sales invoices are off");

        await SaveSettingsAsync(pos: true, invoices: true);
        (await _h.EntriesAsync(SyncKind.SalesInvoice, "POS-1")).Should().ContainSingle();
    }

    [Fact]
    public async Task The_hourly_reconciliation_releases_held_sales_while_the_switch_is_on_and_not_while_off()
    {
        await SellAsync(Pos());

        await _h.RunReconciliationAsync();
        (await _h.MapAsync(SyncKind.SalesInvoice, "POS-1")).LastErrorCode.Should().Be(PosSales.HeldCode, "still off");
        (await _h.EntriesAsync(SyncKind.SalesInvoice, "POS-1")).Should().BeEmpty();

        // Switched on without the settings screen's own release (e.g. it failed): the job finds them.
        await _h.UpdateSettingsAsync(s => s.AutoPushPosSales = true);
        _h.Clock.Advance(TimeSpan.FromHours(1));
        await _h.RunReconciliationAsync();

        (await _h.MapAsync(SyncKind.SalesInvoice, "POS-1")).LastErrorCode.Should().BeNull();
        (await _h.EntriesAsync(SyncKind.SalesInvoice, "POS-1")).Should().ContainSingle();

        await _h.RunReconciliationAsync();
        (await _h.EntriesAsync(SyncKind.SalesInvoice, "POS-1")).Should().ContainSingle("released once");
    }

    // ── Voids and turning it off ─────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_a_held_sale_that_was_never_sent_sends_nothing_now_or_later()
    {
        await SellAsync(Pos());

        (await _h.Gateway(g => g.VoidSalesInvoiceAsync("POS-1"))).State.Should().Be(SyncState.Voided);

        var map = await _h.MapAsync(SyncKind.SalesInvoice, "POS-1");
        map.State.Should().Be(SyncState.Voided);
        map.LastErrorCode.Should().BeNull();

        var cancelled = Pos();
        cancelled.Status = SalesInvoicePayloadStatus.Cancelled;
        _h.Source.Has(cancelled);
        await SaveSettingsAsync(pos: true);
        await _h.DrainAsync();

        _h.Provider.WriteCalls.Should().Be(0);
        (await GetSettingsAsync()).PosSalesWaiting.Should().Be(0);
    }

    [Fact]
    public async Task Turning_it_off_holds_queued_POS_sales_but_leaves_back_office_invoices_queued()
    {
        await SaveSettingsAsync(pos: true);
        await SellAsync(Pos());
        _h.Source.Has(TestPayloads.Invoice(id: "SI-1", doc: "INV-0001"));
        await _h.Send(TestPayloads.Invoice(id: "SI-1", doc: "INV-0001"));

        var model = await SaveSettingsAsync(pos: false);

        model.PosSalesWaiting.Should().Be(1);
        var pos = await _h.MapAsync(SyncKind.SalesInvoice, "POS-1");
        (pos.State, pos.LastErrorCode).Should().Be((SyncState.NotSynced, PosSales.HeldCode));
        (await _h.EntriesAsync(SyncKind.SalesInvoice, "POS-1")).Should().OnlyContain(e => e.Status == OutboxStatus.Done);
        (await _h.OpenEntryAsync(SyncKind.SalesInvoice, "SI-1")).Status.Should().Be(OutboxStatus.WaitingOnDependency);

        await _h.DrainAsync();
        InvoiceCreates("INV-P-0001").Should().Be(0);
        InvoiceCreates("INV-0001").Should().Be(1);
    }

    [Fact]
    public async Task Retry_of_a_held_sale_is_refused_while_the_switch_is_off()
    {
        await SellAsync(Pos());
        var map = await _h.MapAsync(SyncKind.SalesInvoice, "POS-1");

        var act = () => _h.Scoped(sp => sp.GetRequiredService<IQuickBooksSyncAdminService>().RetryAsync(map.Uuid));

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*Sync POS sales*");
        (await _h.EntriesAsync(SyncKind.SalesInvoice, "POS-1")).Should().BeEmpty();
    }

    [Fact]
    public async Task Push_now_of_a_held_sale_keeps_it_held_while_the_switch_is_off()
    {
        await SellAsync(Pos());

        await _h.Scoped(sp => sp.GetRequiredService<IQuickBooksSyncAdminService>()
            .PushAsync(new ManualPushRequest { Kind = "SalesInvoice", ExternalIds = ["POS-1"] }));

        (await _h.MapAsync(SyncKind.SalesInvoice, "POS-1")).LastErrorCode.Should().Be(PosSales.HeldCode);
        (await _h.EntriesAsync(SyncKind.SalesInvoice, "POS-1")).Should().BeEmpty();
    }

    // ── Service products ─────────────────────────────────────────────────────

    private static ItemPayload ServiceItem(string id = "I-SVC", string name = "Installation", string sku = "SVC-1")
    {
        var item = TestPayloads.Item(id: id, name: name, sku: sku);
        item.Kind         = ItemPayloadKind.Service;
        item.SalesPrice   = null;    // a service may carry no price
        item.PurchaseCost = null;
        item.IsPurchased  = false;
        return item;
    }

    [Fact]
    public async Task A_service_product_an_invoice_needs_is_created_as_a_QuickBooks_service_item_and_used_on_the_line()
    {
        _h.Source.Has(ServiceItem());
        var invoice = TestPayloads.Invoice(id: "SI-S", doc: "INV-S-1", lines: [new TestPayloads.Line("I-SVC", 1, 500m)]);
        _h.Source.Has(invoice);
        await _h.Send(invoice);

        await _h.DrainAsync();

        var created = _h.Provider.Calls.Single(c => c.Operation == "Create" && c.Kind == SyncKind.Item).Entity.Should().BeOfType<RemoteItem>().Subject;
        created.Type.Should().Be(RemoteItemType.Service);
        created.KeepRemoteType.Should().BeFalse();
        created.UnitPrice.Should().BeNull();
        var item = await _h.MapAsync(SyncKind.Item, "I-SVC");
        _h.Provider.Calls.Single(c => c.Operation == "Create" && c.Kind == SyncKind.SalesInvoice).Entity.Should().BeOfType<RemoteInvoice>()
            .Which.Lines.Single(l => l.ItemId is not null).ItemId.Should().Be(item.RemoteId);
    }

    [Fact]
    public async Task A_service_product_matched_to_an_existing_QuickBooks_item_is_used_on_lines_and_keeps_that_items_type()
    {
        var remote = _h.Provider.Seed(SyncKind.Item, "Installation Labour", sku: "SVC-1");   // any QuickBooks item type
        await _h.Send(ServiceItem());
        (await _h.MapAsync(SyncKind.Item, "I-SVC")).State.Should().Be(SyncState.NotSynced, "held for matching until referenced");

        var scan = await _h.Scoped(sp => sp.GetRequiredService<IQuickBooksMatchingService>().ScanAsync(SyncKind.Item));
        scan.Exact.Should().Be(1);
        var candidate = (await _h.Scoped(sp => sp.GetRequiredService<IQuickBooksMatchingService>().ListAsync(SyncKind.Item, null))).Single();
        candidate.Reason.Should().Be("SKU");
        await _h.Scoped(sp => sp.GetRequiredService<IQuickBooksMatchingService>().ConfirmAsync(SyncKind.Item,
            new ConfirmMatchesRequest { Decisions = [new MatchDecisionItem { CandidateId = candidate.Id, Decision = "Link" }] }, 1));

        var invoice = TestPayloads.Invoice(id: "SI-S", doc: "INV-S-1", lines: [new TestPayloads.Line("I-SVC", 1, 500m)]);
        _h.Source.Has(invoice);
        await _h.Send(invoice);
        await _h.DrainAsync();

        _h.Provider.Calls.Should().NotContain(c => c.Kind == SyncKind.Item && (c.Operation == "Create" || c.Operation == "Update"),
            "the accountant's item is used as it is");
        _h.Provider.Calls.Single(c => c.Operation == "Create" && c.Kind == SyncKind.SalesInvoice).Entity.Should().BeOfType<RemoteInvoice>()
            .Which.Lines.Single(l => l.ItemId is not null).ItemId.Should().Be(remote.Id);

        // A later change in SCM updates the linked item — without sending a type, so QuickBooks keeps the accountant's.
        var renamed = ServiceItem(name: "Installation (on site)");
        await _h.Send(renamed);
        await _h.DrainAsync();

        var update = _h.Provider.Calls.Single(c => c.Operation == "Update" && c.Kind == SyncKind.Item).Entity.Should().BeOfType<RemoteItem>().Subject;
        update.KeepRemoteType.Should().BeTrue();
        var wire = JObject.Parse(QboJson.Entity(QboMap.AsSparseUpdate(QboItemMapper.ToSdk(update), remote.Id, "0"))!);
        wire.Should().NotContainKey("Type");
        wire["Name"]!.Value<string>().Should().Be("Installation (on site)");
    }

    [Fact]
    public void An_item_SCM_created_still_sends_its_type()
    {
        var json = JObject.Parse(QboJson.Entity(QboItemMapper.ToSdk(new RemoteItem { Name = "Svc", Type = RemoteItemType.Service }))!);
        json["Type"]!.Value<string>().Should().Be("Service");
    }
}
