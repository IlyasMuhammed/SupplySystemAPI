using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Integration;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Suppliers.Tests.QuickBooks;

/// <summary>Records each entity EF materializes, into the same list the gateway writes its calls to.</summary>
internal sealed class MaterializationRecorder : IMaterializationInterceptor
{
    private readonly List<string> _events;
    public MaterializationRecorder(List<string> events) => _events = events;

    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        if (entity is BusinessPartner p) _events.Add($"load:{p.UUID}");
        return entity;
    }
}

internal sealed class PartnerSourceRig
{
    public required SuppliersDbContext             Db;
    public required RecordingQuickBooksGateway     Gateway;
    public required FixedCurrencyCodes             Currencies;
    public required ListLogger<PartnerQuickBooksSource> Log;
    public required PartnerQuickBooksSource        Source;
    public required Guid                           OrgId;

    internal static PartnerSourceRig New(Dictionary<Guid, string>? codes = null, IInterceptor? interceptor = null, Guid? orgId = null, string? dbName = null)
    {
        var org = orgId ?? Guid.NewGuid();
        var options = new DbContextOptionsBuilder<SuppliersDbContext>().UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString());
        if (interceptor is not null) options.AddInterceptors(interceptor);
        var db = new SuppliersDbContext(options.Options, new StaticTenantContext { OrganizationId = org });

        var gateway    = new RecordingQuickBooksGateway();
        var currencies = new FixedCurrencyCodes(codes);
        var log        = new ListLogger<PartnerQuickBooksSource>();

        return new PartnerSourceRig
        {
            Db = db, Gateway = gateway, Currencies = currencies, Log = log, OrgId = org,
            Source = new PartnerQuickBooksSource(db, gateway, currencies, log)
        };
    }

    internal BusinessPartner Add(
        string code, bool customer = false, bool vendor = false, bool carrier = false, bool serviceProvider = false,
        string status = "ACTIVE", bool active = true, bool deleted = false,
        DateTime? created = null, DateTime? modified = null, DateTime? statusChanged = null, Guid? currency = null)
    {
        var p = new BusinessPartner
        {
            UUID = Guid.NewGuid(), SupplierName = $"Partner {code}", SupplierCode = code,
            IsCustomer = customer, IsVendor = vendor, IsCarrier = carrier, IsServiceProvider = serviceProvider,
            Status = status, IsActive = active, IsDelete = deleted, PreferredCurrency = currency,
            CreatedBy = 1, CreatedDate = created ?? new DateTime(2026, 1, 1), ModifiedDate = modified, StatusChangedAt = statusChanged
        };
        Db.BusinessPartners.Add(p);
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
        return p;
    }
}

public class PartnerQuickBooksSourceTests
{
    [Fact]
    public void It_serves_customers_and_vendors()
    {
        PartnerSourceRig.New().Source.Kinds.Should().BeEquivalentTo([SyncKind.Customer, SyncKind.Vendor]);
    }

    [Theory]
    [InlineData(SyncKind.Item)]
    [InlineData(SyncKind.SalesInvoice)]
    [InlineData(SyncKind.Bill)]
    public async Task Any_other_kind_is_a_programming_error(SyncKind kind)
    {
        var rig = PartnerSourceRig.New();

        await rig.Invoking(r => r.Source.PushAsync(kind, [Guid.NewGuid().ToString()])).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await rig.Invoking(r => r.Source.PushAllAsync(kind, null)).Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ── PushAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PushAsync_sends_each_asked_for_partner_as_the_kind_asked_for_with_its_currency_code()
    {
        var eur = Guid.NewGuid();
        var rig = PartnerSourceRig.New(new Dictionary<Guid, string> { [eur] = "EUR" });
        var a = rig.Add("A", customer: true, currency: eur);
        var b = rig.Add("B", customer: true, currency: Guid.NewGuid()); // a currency the catalog has no code for

        await rig.Source.PushAsync(SyncKind.Customer, [a.UUID.ToString(), b.UUID.ToString()]);

        rig.Gateway.Customers.Select(c => c.ExternalId).Should().BeEquivalentTo([a.UUID.ToString(), b.UUID.ToString()]);
        rig.Gateway.Customers.Single(c => c.ExternalId == a.UUID.ToString()).CurrencyCode.Should().Be("EUR");
        rig.Gateway.Customers.Single(c => c.ExternalId == b.UUID.ToString()).CurrencyCode.Should().BeNull();
        rig.Gateway.Vendors.Should().BeEmpty();
        rig.Currencies.Loads.Should().Be(1, "the catalog is read once per push, not once per partner");
    }

    [Fact]
    public async Task PushAsync_does_not_second_guess_the_gateway_about_the_role()
    {
        // The gateway asks for a vendor because a bill names it. A carrier-only partner (or one never
        // flagged as a vendor at all) must still go, or that bill would wait for good.
        var rig = PartnerSourceRig.New();
        var customerOnly = rig.Add("C", customer: true);

        await rig.Source.PushAsync(SyncKind.Vendor, [customerOnly.UUID.ToString()]);

        rig.Gateway.Vendors.Should().ContainSingle().Which.AccountNumber.Should().Be("C");
    }

    [Fact]
    public async Task PushAsync_skips_malformed_unknown_duplicate_and_deleted_ids()
    {
        var rig = PartnerSourceRig.New();
        var live    = rig.Add("LIVE", vendor: true);
        var deleted = rig.Add("GONE", vendor: true, status: "PENDING", deleted: true);

        await rig.Source.PushAsync(SyncKind.Vendor,
            ["not-a-guid", "", Guid.NewGuid().ToString(), live.UUID.ToString(), live.UUID.ToString().ToUpperInvariant(), $" {live.UUID} ", deleted.UUID.ToString()]);

        rig.Gateway.Vendors.Should().ContainSingle().Which.ExternalId.Should().Be(live.UUID.ToString());
    }

    [Fact]
    public async Task PushAsync_with_nothing_usable_reads_nothing_and_sends_nothing()
    {
        var rig = PartnerSourceRig.New();

        await rig.Source.PushAsync(SyncKind.Customer, []);
        await rig.Source.PushAsync(SyncKind.Customer, ["x", "y"]);

        rig.Gateway.Calls.Should().BeEmpty();
        rig.Currencies.Loads.Should().Be(0);
    }

    [Fact]
    public async Task PushAsync_sees_only_the_current_organization()
    {
        var dbName = Guid.NewGuid().ToString();
        var mine   = PartnerSourceRig.New(dbName: dbName);
        var theirs = PartnerSourceRig.New(dbName: dbName);
        var other  = theirs.Add("THEIRS", customer: true);

        await mine.Source.PushAsync(SyncKind.Customer, [other.UUID.ToString()]);

        mine.Gateway.Calls.Should().BeEmpty("the tenant filter applies — the job runs under the requesting organization");
    }

    // ── PushAllAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task PushAllAsync_customers_sends_only_active_undeleted_unbarred_customers()
    {
        var rig = PartnerSourceRig.New();
        var ok        = rig.Add("OK", customer: true);
        var pending   = rig.Add("PEND", customer: true, status: "PENDING");
        var suspended = rig.Add("SUSP", customer: true, status: "SUSPENDED");
        var both      = rig.Add("BOTH", customer: true, vendor: true);
        rig.Add("VEND", vendor: true);
        rig.Add("OFF", customer: true, active: false);
        rig.Add("DEL", customer: true, deleted: true);
        rig.Add("BL", customer: true, status: "BLACKLISTED");
        rig.Add("REJ", customer: true, status: "REJECTED");
        rig.Add("INA", customer: true, status: "INACTIVE");
        rig.Add("blk", customer: true, status: "blacklisted");

        var sent = await rig.Source.PushAllAsync(SyncKind.Customer, null);

        sent.Should().Be(4);
        rig.Gateway.Customers.Select(c => c.Code).Should().BeEquivalentTo(["OK", "PEND", "SUSP", "BOTH"]);
        rig.Gateway.Vendors.Should().BeEmpty();
        _ = (ok, pending, suspended, both);
    }

    [Fact]
    public async Task PushAllAsync_vendors_includes_carriers_and_service_providers()
    {
        var rig = PartnerSourceRig.New();
        rig.Add("V", vendor: true);
        rig.Add("CAR", carrier: true);
        rig.Add("SP", serviceProvider: true);
        rig.Add("CUST", customer: true);
        rig.Add("VOFF", vendor: true, active: false);

        var sent = await rig.Source.PushAllAsync(SyncKind.Vendor, null);

        sent.Should().Be(3);
        rig.Gateway.Vendors.Select(v => v.Code).Should().BeEquivalentTo(["V", "CAR", "SP"]);
        rig.Gateway.Customers.Should().BeEmpty();
    }

    [Fact]
    public async Task PushAllAsync_since_takes_what_was_created_edited_or_moved_through_the_workflow_since()
    {
        var since = new DateTime(2026, 9, 1);
        var rig = PartnerSourceRig.New();
        rig.Add("OLD", customer: true, created: since.AddDays(-30));
        rig.Add("OLD-EDITED-BEFORE", customer: true, created: since.AddDays(-30), modified: since.AddSeconds(-1));
        rig.Add("NEW", customer: true, created: since);
        rig.Add("EDITED", customer: true, created: since.AddDays(-30), modified: since.AddDays(2));
        // Approve/reject/blacklist/suspend stamp StatusChangedAt, not ModifiedDate.
        rig.Add("APPROVED", customer: true, created: since.AddDays(-30), statusChanged: since.AddHours(3));

        await rig.Source.PushAllAsync(SyncKind.Customer, since);

        rig.Gateway.Customers.Select(c => c.Code).Should().BeEquivalentTo(["NEW", "EDITED", "APPROVED"]);
    }

    [Fact]
    public async Task PushAllAsync_loads_and_sends_in_batches_so_memory_stays_bounded()
    {
        var events = new List<string>();
        var rig = PartnerSourceRig.New(interceptor: new MaterializationRecorder(events));
        var total = QuickBooksSupport.BatchSize * 2 + 50;
        for (var i = 0; i < total; i++) rig.Add($"C{i:D4}", customer: true);
        events.Clear();
        rig.Gateway.OnCall = call => events.Add($"send:{call}");

        var sent = await rig.Source.PushAllAsync(SyncKind.Customer, null);

        sent.Should().Be(total);
        rig.Gateway.Customers.Select(c => c.ExternalId).Should().OnlyHaveUniqueItems();

        // Load 200, send 200, load 200, send 200, load 50, send 50 — never everything loaded at once.
        var runs = Runs(events);
        runs.Should().Equal(
            ("load", 200), ("send", 200), ("load", 200), ("send", 200), ("load", 50), ("send", 50));
    }

    [Fact]
    public async Task PushAllAsync_when_the_count_is_an_exact_multiple_of_the_batch_ends_cleanly()
    {
        var rig = PartnerSourceRig.New();
        for (var i = 0; i < QuickBooksSupport.BatchSize; i++) rig.Add($"C{i:D4}", customer: true);

        (await rig.Source.PushAllAsync(SyncKind.Customer, null)).Should().Be(QuickBooksSupport.BatchSize);
    }

    [Fact]
    public async Task PushAllAsync_carries_on_past_a_failing_partner_and_counts_only_what_reached_the_gateway()
    {
        var rig = PartnerSourceRig.New();
        var a = rig.Add("A", customer: true);
        var b = rig.Add("B", customer: true);
        var c = rig.Add("C", customer: true);
        rig.Gateway.FailWhen = id => id == b.UUID.ToString();

        var sent = await rig.Source.PushAllAsync(SyncKind.Customer, null);

        sent.Should().Be(2);
        rig.Gateway.Customers.Select(x => x.Code).Should().Equal("A", "B", "C");
        var warning = rig.Log.At(LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Message.Should().Contain(b.UUID.ToString());
        warning.Exception.Should().BeOfType<InvalidOperationException>();
        _ = (a, c);
    }

    [Fact]
    public async Task A_refusal_is_logged_at_information_not_as_a_warning_or_error()
    {
        var rig = PartnerSourceRig.New();
        var p = rig.Add("X", customer: true);
        rig.Gateway.Result = GatewayResult.Invalid([new GatewayError("CurrencyCode", "CURRENCY_NOT_HOME", "Only home-currency partners.")]);

        await rig.Source.PushAsync(SyncKind.Customer, [p.UUID.ToString()]);

        rig.Log.At(LogLevel.Information).Should().ContainSingle()
            .Which.Message.Should().Contain("CurrencyCode").And.Contain(p.UUID.ToString());
        rig.Log.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_requested_cancellation_is_not_swallowed()
    {
        var rig = PartnerSourceRig.New();
        rig.Add("X", customer: true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await rig.Invoking(r => r.Source.PushAllAsync(SyncKind.Customer, null, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    internal static List<(string Kind, int Count)> Runs(IEnumerable<string> events)
    {
        var runs = new List<(string, int)>();
        foreach (var kind in events.Select(e => e.Split(':')[0]))
        {
            if (runs.Count > 0 && runs[^1].Item1 == kind) runs[^1] = (kind, runs[^1].Item2 + 1);
            else runs.Add((kind, 1));
        }
        return runs;
    }
}
