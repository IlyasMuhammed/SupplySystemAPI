using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Jobs;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Modules.Integration.Tests.Setup;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Sync;

/// <summary>
/// A record Blocked for a reason outside its payload — a tax code not mapped, an account not chosen, a currency not
/// active in QuickBooks, no SMS exchange rate — goes by itself once that is fixed: re-validated right after tax
/// mappings, term mappings or settings are saved and after a reference refresh, and by the outbox job every run
/// (so an exchange rate entered in SMS is picked up without Retry). What passes is queued exactly as a fresh upsert
/// would queue it: scope, dependencies, dry run.
/// <para>
/// The whole engine with the real settings service and the real reference-data service (snapshots in the
/// database, read by the currency rules too). Harness company: home PKR; 0% → TAX0 and 17% → TAX17 mapped.
/// </para>
/// </summary>
public sealed class BlockedRevalidationTests : IAsyncLifetime
{
    private static readonly DateTime Date = TestPayloads.TxnDate;

    private readonly SyncHarness _h = new(configure: s =>
    {
        s.AddScoped<ReferenceDataService>();
        s.AddScoped<IReferenceDataService>(sp => sp.GetRequiredService<ReferenceDataService>());
        s.AddScoped<IReferenceDataStore>(sp => sp.GetRequiredService<ReferenceDataService>());
        s.AddScoped<IReferenceDataReader>(sp => sp.GetRequiredService<ReferenceDataService>());
        s.AddScoped<IBaseCurrencyResolver>(_ => new FixedBaseCurrency(true, "PKR"));
        s.AddScoped<IPreflightService, PreflightService>();
        s.AddScoped<IIntegrationSettingsService, IntegrationSettingsService>();
        s.AddScoped<ReferenceRefreshJob>();
    });

    private IntegrationConnection _connection = null!;

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _h.DisposeAsync();

    // ── Setup ────────────────────────────────────────────────────────────────

    private static RemoteReferenceData Reference(bool multiCurrency = false, params string[] currencies) => new()
    {
        Accounts =
        [
            new RemoteAccount("ACC-INC", "Sales",      "Income",  null, "Revenue", "PKR", true),
            new RemoteAccount("ACC-EXP", "Purchases",  "Expense", null, "Expense", "PKR", true),
            new RemoteAccount("ACC-FRT", "Freight in", "Expense", null, "Expense", "PKR", true),
            new RemoteAccount("ACC-DSC", "Discounts",  "Expense", null, "Expense", "PKR", true)
        ],
        TaxCodes =
        [
            new RemoteTaxCode("TAX0",    "Zero",     null, false, 0m,  true),
            new RemoteTaxCode("TAX17",   "GST 17%",  null, true,  17m, true),
            new RemoteTaxCode("TAX-PUR", "Purchase", null, true,  17m, true),
            new RemoteTaxCode("TAX-GST", "GST code", null, true,  17m, true)
        ],
        Currencies  = currencies.Select(c => new RemoteCurrency(c, c)).ToList(),
        Preferences = new RemotePreferences("PKR", multiCurrency, true, true),
        CompanyInfo = new RemoteCompanyInfo("Sandbox Company", null, "PK", null)
    };

    private Task ConnectAsync(bool multiCurrency, params string[] currencies) => ConnectAsync(null, multiCurrency, currencies);

    private async Task ConnectAsync(Action<IntegrationSettings>? configure = null, bool multiCurrency = false, params string[] currencies)
    {
        _connection = await _h.ConnectAsync(configure);
        var data = Reference(multiCurrency, ["PKR", .. currencies]);
        await using var db = _h.DbAs(_h.OrgId);
        (await db.Connections.SingleAsync()).MultiCurrencyEnabled = multiCurrency;
        void Put(string kind, object? value) => db.ReferenceSnapshots.Add(new ReferenceSnapshot
        {
            ConnectionId = _connection.Id, Kind = kind, Json = JsonSerializer.Serialize(value, ReferenceDataService.Json), FetchedAt = _h.Now
        });
        Put(ReferenceKinds.Accounts, data.Accounts);
        Put(ReferenceKinds.TaxCodes, data.TaxCodes);
        Put(ReferenceKinds.Terms, data.Terms);
        Put(ReferenceKinds.Currencies, data.Currencies);
        Put(ReferenceKinds.Preferences, data.Preferences);
        Put(ReferenceKinds.CompanyInfo, data.CompanyInfo);
        await db.SaveChangesAsync();
    }

    /// <summary>A customer/vendor/item we created in QuickBooks, as the executor leaves it.</summary>
    private async Task InQuickBooksAsync(SyncKind kind, string externalId, object? payload = null)
    {
        var json = payload is null ? null : SyncPayloads.Serialize(payload);
        var fp   = json is null ? null : SyncPayloads.Fingerprint(json);
        await using var db = _h.DbAs(_h.OrgId);
        db.EntityMaps.Add(new EntityMap
        {
            ConnectionId = _connection.Id, Kind = kind, ExternalId = externalId, SourceSystem = QuickBooksSourceSystems.Scm,
            DisplayLabel = externalId, RemoteId = "R-" + externalId, RemoteSyncToken = "0", State = SyncState.Synced,
            LinkOrigin = LinkOrigin.Created, PayloadJson = json, PayloadFingerprint = fp, LastPushedFingerprint = fp
        });
        await db.SaveChangesAsync();
    }

    private async Task CustomerAndItemInQuickBooksAsync(string? customerCurrency = "PKR")
    {
        var customer = TestPayloads.Customer();
        customer.CurrencyCode = customerCurrency;
        await InQuickBooksAsync(SyncKind.Customer, "C-1", customer);
        await InQuickBooksAsync(SyncKind.Item, "I-1", TestPayloads.Item());
    }

    private Task<T> Settings<T>(Func<IIntegrationSettingsService, Task<T>> call) =>
        _h.Scoped(sp => call(sp.GetRequiredService<IIntegrationSettingsService>()));

    private static SaveTaxCodeMappingsRequest MapCode(string code, decimal rate, string? qbo) =>
        new() { Mappings = [new TaxCodeMappingItem { SourceTaxCode = code, TaxPercent = rate, QboTaxCodeId = qbo }] };

    private static SalesInvoicePayload CodedInvoice(string id = "SI-1", string doc = "INV-0001", string code = "GST17")
    {
        var p = TestPayloads.Invoice(id: id, doc: doc);
        p.Lines[0].TaxCode = code;
        return p;
    }

    private static SalesInvoicePayload UsdInvoice(string id = "SI-1", string doc = "INV-0001")
    {
        var p = TestPayloads.Invoice(id: id, doc: doc);
        p.CurrencyCode = "USD";
        return p;
    }

    private Task<EntityMap> Map(SyncKind kind = SyncKind.SalesInvoice, string id = "SI-1") => _h.MapAsync(kind, id);

    private List<RemoteInvoice> CreatedInvoices() =>
        _h.Provider.Calls.Where(c => c.Operation == "Create" && c.Kind == SyncKind.SalesInvoice).Select(c => (RemoteInvoice)c.Entity!).ToList();

    private Task<RevalidationSummary> PassAsync(RevalidationTrigger trigger, Guid? asOrganization = null) =>
        _h.Scoped(sp => sp.GetRequiredService<IBlockedRecordRevalidator>()
            .RevalidateAsync(_connection.Id, asOrganization ?? _h.OrgId, trigger));

    // ── Trigger 1: right after a change is saved ─────────────────────────────

    [Fact]
    public async Task Saving_the_missing_tax_code_mapping_queues_the_blocked_invoice_which_then_goes_with_that_code()
    {
        await ConnectAsync();
        await CustomerAndItemInQuickBooksAsync();
        (await _h.Send(CodedInvoice())).Outcome.Should().Be(GatewayOutcome.Invalid);
        (await Map()).Should().Match<EntityMap>(m => m.State == SyncState.Blocked && m.LastErrorCode == "TAX_UNMAPPED");

        await Settings(s => s.SaveTaxMappingsAsync(MapCode("GST17", 17m, "TAX-GST"), 7));

        var map = await Map();
        map.State.Should().Be(SyncState.Pending, "re-validated and queued by the save — nobody pressed Retry");
        map.LastErrorCode.Should().BeNull();
        map.LastError.Should().BeNull();
        (await _h.EntriesAsync(SyncKind.SalesInvoice, "SI-1")).Should().ContainSingle(e => e.Status == OutboxStatus.Queued);

        await _h.DrainAsync();

        CreatedInvoices().Should().ContainSingle().Which.Lines.Single().TaxCodeId.Should().Be("TAX-GST");
        (await Map()).State.Should().Be(SyncState.Synced);
    }

    [Fact]
    public async Task Choosing_the_missing_account_in_the_settings_queues_the_blocked_bill()
    {
        await ConnectAsync(s => s.FreightExpenseAccountId = null);
        await InQuickBooksAsync(SyncKind.Vendor, "V-1", TestPayloads.Vendor());
        await InQuickBooksAsync(SyncKind.Item, "I-1", TestPayloads.Item());
        (await _h.Send(TestPayloads.Bill())).Errors.Should().ContainSingle(e => e.Code == "SETTINGS_ACCOUNT_MISSING");

        await Settings(s => s.UpdateSettingsAsync(new UpdateIntegrationSettingsRequest
        {
            DefaultIncomeAccountId = "ACC-INC", DefaultExpenseAccountId = "ACC-EXP", FreightExpenseAccountId = "ACC-FRT",
            DiscountAccountId = "ACC-DSC", DefaultPurchaseTaxCodeId = "TAX-PUR"
        }, 7));

        (await Map(SyncKind.Bill, "B-1")).State.Should().Be(SyncState.Pending);
        await _h.DrainAsync();
        (await Map(SyncKind.Bill, "B-1")).State.Should().Be(SyncState.Synced);
    }

    [Fact]
    public async Task Saving_term_mappings_runs_a_pass_too()
    {
        await ConnectAsync();
        await CustomerAndItemInQuickBooksAsync();
        await _h.Send(CodedInvoice());
        // Mapped behind the services' back: only a pass notices.
        await using (var db = _h.DbAs(_h.OrgId))
        {
            db.TaxCodeMappings.Add(new TaxCodeMapping { ConnectionId = _connection.Id, SourceTaxCode = "GST17", TaxPercent = 17m, QboTaxCodeId = "TAX-GST" });
            await db.SaveChangesAsync();
        }

        await Settings(s => s.SaveTermMappingsAsync(new SaveTermMappingsRequest(), 7));

        (await Map()).State.Should().Be(SyncState.Pending);
    }

    [Fact]
    public async Task A_reference_refresh_that_brings_the_currency_releases_records_blocked_for_want_of_it()
    {
        await ConnectAsync(multiCurrency: true);                // USD not active in QuickBooks yet
        await CustomerAndItemInQuickBooksAsync("USD");
        _h.Rates.Add("USD", "PKR", 278.5m, Date);
        (await _h.Send(UsdInvoice())).Errors.Should().ContainSingle(e => e.Code == "CURRENCY_NOT_ACTIVE");

        _h.Provider.ReferenceData = Reference(true, "PKR", "USD");   // the accountant added USD in QuickBooks
        await _h.Scoped(sp => sp.GetRequiredService<IReferenceDataService>().RefreshAsync(7));

        (await Map()).State.Should().Be(SyncState.Pending);
        await _h.DrainAsync();
        CreatedInvoices().Single().ExchangeRate.Should().Be(278.5m);
    }

    [Fact]
    public async Task The_daily_reference_refresh_job_releases_them_too_as_the_connections_own_organization()
    {
        await ConnectAsync(multiCurrency: false);
        await CustomerAndItemInQuickBooksAsync("USD");
        _h.Rates.Add("USD", "PKR", 280m, Date);
        (await _h.Send(UsdInvoice())).Errors.Should().ContainSingle(e => e.Code == "CURRENCY_NOT_HOME");

        _h.Provider.ReferenceData = Reference(true, "PKR", "USD");   // multicurrency switched on in QuickBooks
        await _h.AsJob(sp => sp.GetRequiredService<ReferenceRefreshJob>().RefreshAllAsync());

        (await Map()).State.Should().Be(SyncState.Pending);
    }

    // ── Trigger 2: the outbox job ────────────────────────────────────────────

    [Fact]
    public async Task An_exchange_rate_entered_in_SMS_is_picked_up_by_the_outbox_job_without_Retry()
    {
        await ConnectAsync(multiCurrency: true, "USD");
        await CustomerAndItemInQuickBooksAsync("USD");
        (await _h.Send(UsdInvoice())).Errors.Should().ContainSingle(e => e.Code == "EXCHANGE_RATE_MISSING");

        _h.Rates.Add("USD", "PKR", 281.25m, Date);
        await _h.RunOutboxAsync();

        CreatedInvoices().Should().ContainSingle().Which.ExchangeRate.Should().Be(281.25m, "re-validated, queued and sent in one run");
        (await Map()).State.Should().Be(SyncState.Synced);
    }

    [Fact]
    public async Task While_nothing_changed_the_job_writes_nothing_however_often_it_runs()
    {
        await ConnectAsync(multiCurrency: true, "USD");
        await CustomerAndItemInQuickBooksAsync("USD");
        await _h.Send(UsdInvoice());
        var before = await Map();

        for (var i = 0; i < 3; i++)
        {
            _h.Clock.Advance(TimeSpan.FromMinutes(1));
            await _h.RunOutboxAsync();
        }

        var after = await Map();
        after.State.Should().Be(SyncState.Blocked);
        after.LastErrorCode.Should().Be("EXCHANGE_RATE_MISSING");
        after.LastError.Should().Be(before.LastError);
        after.ModifiedDate.Should().Be(before.ModifiedDate);
        after.RowVersion.Should().Equal(before.RowVersion);
        (await _h.EntriesAsync(SyncKind.SalesInvoice, "SI-1")).Should().BeEmpty();
        _h.Provider.WriteCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_job_works_through_a_long_blocked_list_in_rotating_batches()
    {
        await ConnectAsync(multiCurrency: true, "USD");
        await CustomerAndItemInQuickBooksAsync("USD");
        var count = BlockedRecordRevalidator.PeriodicBatch + 10;
        for (var i = 1; i <= count; i++) await _h.Send(UsdInvoice($"SI-{i}", $"INV-{i:0000}"));
        _h.Rates.Add("USD", "PKR", 278m, Date);

        async Task<int> BlockedAsync()
        {
            await using var db = _h.SuperDb();
            return await db.EntityMaps.CountAsync(m => m.Kind == SyncKind.SalesInvoice && m.State == SyncState.Blocked);
        }

        (await BlockedAsync()).Should().Be(count);
        await _h.RunOutboxAsync();
        (await BlockedAsync()).Should().Be(10, "one batch per run");
        await _h.RunOutboxAsync();
        (await BlockedAsync()).Should().Be(0, "the next run carries on where the last stopped");
    }

    // ── Exactly as a fresh upsert: dependencies, scope, dry run, start date ──

    [Fact]
    public async Task A_released_invoice_whose_customer_is_not_in_QuickBooks_waits_for_it_and_asks_for_it()
    {
        await ConnectAsync();
        await InQuickBooksAsync(SyncKind.Item, "I-1", TestPayloads.Item());
        _h.Source.Has(TestPayloads.Customer());
        await _h.Send(CodedInvoice());

        await Settings(s => s.SaveTaxMappingsAsync(MapCode("GST17", 17m, "TAX-GST"), 7));

        (await Map()).State.Should().Be(SyncState.WaitingOnDependency);
        (await _h.FindMapAsync(SyncKind.Customer, "C-1"))!.RequestedAt.Should().NotBeNull("the customer is asked for");

        await _h.DrainAsync();
        (await Map()).State.Should().Be(SyncState.Synced);
        (await Map(SyncKind.Customer, "C-1")).State.Should().Be(SyncState.Synced);
    }

    [Fact]
    public async Task A_released_customer_out_of_the_partner_scope_is_held_not_sent()
    {
        await ConnectAsync(multiCurrency: true);
        (await _h.Send(new Func<CustomerPayload>(() => { var c = TestPayloads.Customer(); c.CurrencyCode = "USD"; return c; })()))
            .Errors.Should().ContainSingle(e => e.Code == "CURRENCY_NOT_ACTIVE");

        _h.Provider.ReferenceData = Reference(true, "PKR", "USD");
        await _h.Scoped(sp => sp.GetRequiredService<IReferenceDataService>().RefreshAsync(7));

        var map = await Map(SyncKind.Customer, "C-1");
        map.State.Should().Be(SyncState.NotSynced, "only when a document needs it (OnlyWhenReferenced)");
        map.LastErrorCode.Should().BeNull();
        (await _h.EntriesAsync(SyncKind.Customer, "C-1")).Should().NotContain(e => e.Status == OutboxStatus.Queued);
    }

    [Fact]
    public async Task In_dry_run_a_released_record_dry_runs_and_nothing_is_written()
    {
        await ConnectAsync(s => s.Mode = SyncMode.DryRun);
        await CustomerAndItemInQuickBooksAsync();
        await _h.Send(CodedInvoice());

        await Settings(s => s.SaveTaxMappingsAsync(MapCode("GST17", 17m, "TAX-GST"), 7));
        await _h.DrainAsync();

        (await Map()).State.Should().Be(SyncState.DryRunOk);
        _h.Provider.WriteCalls.Should().Be(0);
    }

    [Fact]
    public async Task Records_a_fresh_upsert_would_turn_away_stay_blocked()
    {
        await ConnectAsync();
        await CustomerAndItemInQuickBooksAsync();
        await _h.Send(CodedInvoice());
        await _h.UpdateSettingsAsync(s => s.AutoPushSalesInvoices = false);

        await Settings(s => s.SaveTaxMappingsAsync(MapCode("GST17", 17m, "TAX-GST"), 7));

        (await Map()).State.Should().Be(SyncState.Blocked, "invoices are not pushed any more");
    }

    // ── What is and is not re-validated ──────────────────────────────────────

    [Fact]
    public async Task A_record_with_several_reasons_keeps_only_the_ones_still_true()
    {
        await ConnectAsync();
        await CustomerAndItemInQuickBooksAsync();
        var p = CodedInvoice();
        p.ExpectedTotal += 5m;
        (await _h.Send(p)).Errors.Select(e => e.Code).Should().BeEquivalentTo(["TAX_UNMAPPED", "TOTAL_MISMATCH"]);
        (await Map()).LastErrorCode.Should().Be("VALIDATION_FAILED");

        await Settings(s => s.SaveTaxMappingsAsync(MapCode("GST17", 17m, "TAX-GST"), 7));

        var map = await Map();
        map.State.Should().Be(SyncState.Blocked);
        map.LastErrorCode.Should().Be("TOTAL_MISMATCH", "the mapping is there now; the arithmetic is still wrong");
        map.LastError.Should().NotContain("tax code");
    }

    [Fact]
    public async Task Refusals_about_the_payload_itself_are_not_re_validated()
    {
        await ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        (await _h.Send(TestPayloads.Customer(name: "Bad: Name"))).Errors.Should().ContainSingle(e => e.Code == "NAME_HAS_COLON");
        // Pretend the stored payload were fine now: only a new payload can fix such a refusal, so no pass looks.
        await using (var db = _h.DbAs(_h.OrgId))
        {
            var stored = await db.EntityMaps.SingleAsync(m => m.ExternalId == "C-1");
            stored.PayloadJson = SyncPayloads.Serialize(TestPayloads.Customer());
            await db.SaveChangesAsync();
        }

        var summary = await PassAsync(RevalidationTrigger.AfterChange);
        await _h.RunOutboxAsync();

        summary.Checked.Should().Be(0);
        (await Map(SyncKind.Customer, "C-1")).State.Should().Be(SyncState.Blocked);
    }

    [Fact]
    public async Task Deleting_a_code_mapping_blocks_new_invoices_but_leaves_a_synced_one_synced_and_unsent()
    {
        await ConnectAsync();
        await CustomerAndItemInQuickBooksAsync();
        await Settings(s => s.SaveTaxMappingsAsync(MapCode("GST17", 17m, "TAX-GST"), 7));
        await _h.Send(CodedInvoice());
        await _h.DrainAsync();
        (await Map()).State.Should().Be(SyncState.Synced);
        var writes = _h.Provider.WriteCalls;

        await Settings(s => s.SaveTaxMappingsAsync(MapCode("GST17", 17m, null), 7));      // mapping deleted

        (await Map()).State.Should().Be(SyncState.Synced, "nothing about what QuickBooks holds changed");
        (await _h.Send(CodedInvoice())).State.Should().Be(SyncState.Synced, "the same payload again: QuickBooks has it");
        (await _h.Send(CodedInvoice("SI-2", "INV-0002"))).Errors.Should().ContainSingle(e => e.Code == "TAX_UNMAPPED");

        // A Retry of the synced invoice is refused by validation now (it would be re-sent)…
        var item = (await Map()).Uuid;
        await _h.Scoped(sp => sp.GetRequiredService<IQuickBooksSyncAdminService>().RetryAsync(item));
        (await Map()).Should().Match<EntityMap>(m => m.State == SyncState.Blocked && m.LastErrorCode == "TAX_UNMAPPED" && m.RemoteId != null);

        // …and once the code is mapped again it is Synced again — QuickBooks already has exactly it — while the new one is queued.
        await Settings(s => s.SaveTaxMappingsAsync(MapCode("GST17", 17m, "TAX-GST"), 7));

        (await Map()).Should().Match<EntityMap>(m => m.State == SyncState.Synced && m.LastErrorCode == null);
        (await Map(SyncKind.SalesInvoice, "SI-2")).State.Should().Be(SyncState.Pending);
        await _h.DrainAsync();
        _h.Provider.WriteCalls.Should().Be(writes + 1, "only INV-0002 is written");
    }

    // ── Tenant safety ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_pass_started_as_another_organization_does_nothing()
    {
        await ConnectAsync();
        await CustomerAndItemInQuickBooksAsync();
        await _h.Send(CodedInvoice());
        await using (var db = _h.DbAs(_h.OrgId))
        {
            db.TaxCodeMappings.Add(new TaxCodeMapping { ConnectionId = _connection.Id, SourceTaxCode = "GST17", TaxPercent = 17m, QboTaxCodeId = "TAX-GST" });
            await db.SaveChangesAsync();
        }

        // The current tenant is this harness's organization; the pass claims to be for another one.
        (await PassAsync(RevalidationTrigger.AfterChange, asOrganization: Guid.NewGuid())).Should().Be(RevalidationSummary.None);
        (await Map()).State.Should().Be(SyncState.Blocked);

        (await PassAsync(RevalidationTrigger.AfterChange)).Released.Should().Be(1);
        (await PassAsync(RevalidationTrigger.AfterChange)).Checked.Should().Be(0, "idempotent: nothing is Blocked any more");
    }
}
