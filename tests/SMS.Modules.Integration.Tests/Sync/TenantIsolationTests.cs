using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Sync;

/// <summary>
/// Plans S-10/S-11 read SMS's exchange rates and tax codes through SMS.Shared contracts that Finance scopes to the
/// CURRENT tenant (TenantContext.OrganizationId). Every path that validates or builds a record must therefore run as
/// the record's own organization: the jobs (HangfireTenantScope per connection) and the admin actions — including a
/// super admin's, whose JWT names their own organization while the tenant query filter is bypassed.
/// </summary>
public sealed class TenantIsolationTests : IAsyncLifetime
{
    private static readonly DateTime Date = TestPayloads.TxnDate;
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private readonly Dictionary<Guid, FakeExchangeRates> _rates = new()
    {
        [OrgA] = new FakeExchangeRates().Add("USD", "PKR", 278m, Date),
        [OrgB] = new FakeExchangeRates().Add("USD", "PKR", 300m, Date)
    };

    private readonly SyncHarness _h;

    public TenantIsolationTests() =>
        _h = new SyncHarness(OrgA, configure: s =>
            s.AddScoped<IExchangeRateProvider>(sp => new TenantExchangeRates(sp.GetRequiredService<ITenantContext>(), _rates)));

    public async Task InitializeAsync()
    {
        foreach (var (org, realm) in new[] { (OrgA, "9130000000000001"), (OrgB, "9130000000000002") })
        {
            var connection = await _h.ConnectAsync(organizationId: org, realmId: realm);
            await using var db = _h.DbAs(org);
            (await db.Connections.SingleAsync()).MultiCurrencyEnabled = true;
            foreach (var (kind, id) in new[] { (SyncKind.Customer, "C-1"), (SyncKind.Item, "I-1") })
                db.EntityMaps.Add(new EntityMap
                {
                    ConnectionId = connection.Id, Kind = kind, ExternalId = id, SourceSystem = QuickBooksSourceSystems.Scm,
                    DisplayLabel = id, RemoteId = $"R-{realm[^1]}-{id}", RemoteSyncToken = "0", State = SyncState.Synced
                });
            await db.SaveChangesAsync();
        }
        _h.Reference.Data = new RemoteReferenceData
        {
            Currencies  = [new RemoteCurrency("USD", "US Dollar")],
            Preferences = new RemotePreferences("PKR", true, true, true)
        };
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private async Task<T> As<T>(Guid org, Func<Task<T>> work, bool superAdmin = false)
    {
        var (before, beforeSuper) = (_h.Tenant.RequestOrganizationId, _h.Tenant.RequestIsSuperAdmin);
        _h.Tenant.RequestOrganizationId = org;
        _h.Tenant.RequestIsSuperAdmin   = superAdmin;
        try { return await work(); }
        finally { (_h.Tenant.RequestOrganizationId, _h.Tenant.RequestIsSuperAdmin) = (before, beforeSuper); }
    }

    private static SalesInvoicePayload UsdInvoice(string doc)
    {
        var p = TestPayloads.Invoice(id: "SI-" + doc, doc: doc);
        p.CurrencyCode = "USD";
        return p;
    }

    [Fact]
    public async Task The_outbox_job_sends_each_organizations_invoice_at_its_own_organizations_rate()
    {
        // The gateway only validates, stores and queues; the job builds and sends — as each record's own tenant.
        (await As(OrgA, () => _h.Send(UsdInvoice("A-1")))).Outcome.Should().Be(GatewayOutcome.Accepted);
        (await As(OrgB, () => _h.Send(UsdInvoice("B-1")))).Outcome.Should().Be(GatewayOutcome.Accepted);

        await _h.DrainAsync();

        var sent = _h.Provider.Calls.Where(c => c.Operation == "Create" && c.Kind == SyncKind.SalesInvoice)
                                    .Select(c => (RemoteInvoice)c.Entity!).ToList();
        sent.Should().HaveCount(2);
        sent.Single(i => i.DocNumber == "A-1").ExchangeRate.Should().Be(278m);
        sent.Single(i => i.DocNumber == "B-1").ExchangeRate.Should().Be(300m);
    }

    [Fact]
    public async Task The_jobs_re_validation_of_blocked_records_uses_each_organizations_own_rates()
    {
        _rates[OrgA].Clear();
        _rates[OrgB].Clear();
        (await As(OrgA, () => _h.Send(UsdInvoice("A-1")))).Errors.Should().ContainSingle(e => e.Code == "EXCHANGE_RATE_MISSING");
        (await As(OrgB, () => _h.Send(UsdInvoice("B-1")))).Errors.Should().ContainSingle(e => e.Code == "EXCHANGE_RATE_MISSING");

        _rates[OrgB].Add("USD", "PKR", 300m, Date);   // only organization B enters a rate
        await _h.RunOutboxAsync();

        var sent = _h.Provider.Calls.Where(c => c.Operation == "Create" && c.Kind == SyncKind.SalesInvoice)
                                    .Select(c => (RemoteInvoice)c.Entity!).ToList();
        sent.Should().ContainSingle().Which.Should().Match<RemoteInvoice>(i => i.DocNumber == "B-1" && i.ExchangeRate == 300m);
        (await _h.MapAsync(SyncKind.SalesInvoice, "SI-A-1", organizationId: OrgA)).State.Should().Be(SyncState.Blocked,
            "organization B's rate is no rate for organization A");
    }

    [Fact]
    public async Task A_super_admin_cannot_reach_another_organizations_sync_item_through_the_dashboard_actions()
    {
        // Org B's invoice, blocked for want of a rate in org B.
        _rates[OrgB].Clear();
        (await As(OrgB, () => _h.Send(UsdInvoice("B-1")))).Outcome.Should().Be(GatewayOutcome.Invalid);
        var itemB = (await _h.MapAsync(SyncKind.SalesInvoice, "SI-B-1", organizationId: OrgB)).Uuid;

        // A super admin of org A (tenant filter bypassed) asks for it by id. Retrying it would validate and build
        // org B's invoice with org A's exchange rates (Finance reads the CURRENT tenant's) — and send it.
        Task<object> Act(Func<IQuickBooksSyncAdminService, Task<object>> call) =>
            As(OrgA, () => _h.Scoped(sp => call(sp.GetRequiredService<IQuickBooksSyncAdminService>())), superAdmin: true);

        await FluentActions.Awaiting(() => Act(async s => await s.RetryAsync(itemB))).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => Act(async s => await s.GetLogAsync(itemB))).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => Act(async s => await s.ResolveAsync(itemB, new Models.ResolveSyncItemRequest { Action = "Requeue" }, 1)))
            .Should().ThrowAsync<NotFoundException>();

        _h.Provider.WriteCalls.Should().Be(0);
        (await _h.MapAsync(SyncKind.SalesInvoice, "SI-B-1", organizationId: OrgB)).State.Should().Be(SyncState.Blocked);

        // Their own organization's items still work for them.
        (await As(OrgB, () => _h.Scoped(sp => sp.GetRequiredService<IQuickBooksSyncAdminService>().GetLogAsync(itemB)))).Should().NotBeNull();
    }
}
