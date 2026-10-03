using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Gateway;

/// <summary>Plan §2.6 steps 1–6: the gateway validates, stores and queues — and never calls QuickBooks.</summary>
public class GatewayTests : IAsyncLifetime
{
    private readonly SyncHarness _h = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task No_connection_is_NotConnected_and_stores_nothing()
    {
        var result = await _h.Send(TestPayloads.Customer());

        result.Outcome.Should().Be(GatewayOutcome.NotConnected);
        await using var db = _h.SuperDb();
        (await db.EntityMaps.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Revoked_or_expired_connection_is_NotConnected()
    {
        await _h.ConnectAsync(status: ConnectionStatus.Revoked);
        (await _h.Send(TestPayloads.Customer())).Outcome.Should().Be(GatewayOutcome.NotConnected);
    }

    [Fact]
    public async Task Auto_push_off_for_the_kind_is_Disabled()
    {
        await _h.ConnectAsync(s => s.AutoPushCustomers = false);

        (await _h.Send(TestPayloads.Customer())).Outcome.Should().Be(GatewayOutcome.Disabled);
        (await _h.Send(TestPayloads.Vendor())).Outcome.Should().Be(GatewayOutcome.Accepted);
    }

    [Fact]
    public async Task Documents_before_the_start_date_are_Disabled_not_errors()
    {
        await _h.ConnectAsync(s => s.DocumentStartDate = TestPayloads.TxnDate.AddDays(1));

        var old = TestPayloads.Invoice();
        (await _h.Send(old)).Outcome.Should().Be(GatewayOutcome.Disabled);

        var recent = TestPayloads.Invoice(id: "SI-2", doc: "INV-2");
        recent.TxnDate = TestPayloads.TxnDate.AddDays(1);
        (await _h.Send(recent)).Outcome.Should().NotBe(GatewayOutcome.Disabled);
    }

    [Fact]
    public async Task Invalid_payload_is_refused_but_stored_as_Blocked_for_the_dashboard()
    {
        await _h.ConnectAsync();

        var result = await _h.Send(TestPayloads.Customer(name: "Acme: Branch"));

        result.Outcome.Should().Be(GatewayOutcome.Invalid);
        result.State.Should().Be(SyncState.Blocked);
        result.Errors.Should().ContainSingle(e => e.Code == "NAME_HAS_COLON");

        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.State.Should().Be(SyncState.Blocked);
        map.PayloadJson.Should().NotBeNull();
        map.DisplayLabel.Should().Be("Acme: Branch");
        map.LastErrorCode.Should().Be("NAME_HAS_COLON");
        map.LastError.Should().Contain("colon");
        (await _h.EntriesAsync(SyncKind.Customer, "C-1")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_fixed_payload_after_a_Blocked_one_clears_the_error_and_queues()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await _h.Send(TestPayloads.Customer(name: "Acme: Branch"));

        var result = await _h.Send(TestPayloads.Customer(name: "Acme Branch"));

        result.Should().BeEquivalentTo(GatewayResult.Accepted(SyncState.Pending));
        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.LastError.Should().BeNull();
        map.State.Should().Be(SyncState.Pending);
    }

    [Fact]
    public async Task Only_when_referenced_scope_stores_but_does_not_queue_parties_and_items()
    {
        await _h.ConnectAsync(); // default scope: OnlyWhenReferenced

        (await _h.Send(TestPayloads.Customer())).Should().BeEquivalentTo(GatewayResult.Accepted(SyncState.NotSynced));
        (await _h.Send(TestPayloads.Vendor())).State.Should().Be(SyncState.NotSynced);
        (await _h.Send(TestPayloads.Item())).State.Should().Be(SyncState.NotSynced);

        await using var db = _h.SuperDb();
        (await db.EntityMaps.CountAsync(m => m.PayloadJson != null)).Should().Be(3);
        (await db.Outbox.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task All_active_scope_queues_active_parties_but_not_inactive_ones()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);

        (await _h.Send(TestPayloads.Customer())).State.Should().Be(SyncState.Pending);

        var inactive = TestPayloads.Customer(id: "C-2", name: "Dormant Ltd");
        inactive.IsActive = false;
        (await _h.Send(inactive)).State.Should().Be(SyncState.NotSynced);

        (await _h.EntriesAsync(SyncKind.Customer, "C-1")).Should().ContainSingle(e => e.Status == OutboxStatus.Queued);
        (await _h.EntriesAsync(SyncKind.Customer, "C-2")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_requested_or_already_mapped_record_is_queued_whatever_the_scope()
    {
        await _h.ConnectAsync();
        await using (var db = _h.DbAs(_h.OrgId))
        {
            var connection = await db.Connections.SingleAsync();
            db.EntityMaps.Add(new EntityMap { ConnectionId = connection.Id, Kind = SyncKind.Customer, ExternalId = "C-REQ", DisplayLabel = "C-REQ", RequestedAt = _h.Now });
            db.EntityMaps.Add(new EntityMap { ConnectionId = connection.Id, Kind = SyncKind.Customer, ExternalId = "C-MAP", DisplayLabel = "C-MAP", RemoteId = "77", RemoteSyncToken = "0" });
            await db.SaveChangesAsync();
        }

        (await _h.Send(TestPayloads.Customer(id: "C-REQ", name: "Requested Co"))).State.Should().Be(SyncState.Pending);
        (await _h.Send(TestPayloads.Customer(id: "C-MAP", name: "Mapped Co"))).State.Should().Be(SyncState.Pending);
    }

    [Fact]
    public async Task Same_payload_as_last_pushed_is_a_no_op_and_a_changed_one_queues()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await _h.Send(TestPayloads.Customer());
        await _h.DrainAsync();
        (await _h.MapAsync(SyncKind.Customer, "C-1")).State.Should().Be(SyncState.Synced);
        var entriesBefore = (await _h.EntriesAsync(SyncKind.Customer, "C-1")).Count;

        var again = await _h.Send(TestPayloads.Customer());

        again.Should().BeEquivalentTo(GatewayResult.Accepted(SyncState.Synced));
        (await _h.EntriesAsync(SyncKind.Customer, "C-1")).Should().HaveCount(entriesBefore, "nothing is queued for an unchanged payload");

        var changed = TestPayloads.Customer();
        changed.Phone = "042-999-999";
        (await _h.Send(changed)).State.Should().Be(SyncState.Pending);
        (await _h.EntriesAsync(SyncKind.Customer, "C-1")).Should().ContainSingle(e => e.Status == OutboxStatus.Queued);
    }

    [Fact]
    public async Task Resending_merges_into_the_one_open_entry_and_resets_its_attempts()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await _h.Send(TestPayloads.Customer());

        await using (var db = _h.SuperDb())
        {
            var entry = await db.Outbox.SingleAsync();
            entry.AttemptCount  = 3;
            entry.NextAttemptAt = _h.Now.AddHours(1);
            await db.SaveChangesAsync();
        }

        var changed = TestPayloads.Customer();
        changed.Notes = "new note";
        await _h.Send(changed);

        var entries = await _h.EntriesAsync(SyncKind.Customer, "C-1");
        entries.Should().ContainSingle();
        entries[0].AttemptCount.Should().Be(0);
        entries[0].NextAttemptAt.Should().Be(_h.Now);
        entries[0].Status.Should().Be(OutboxStatus.Queued);
    }

    [Fact]
    public async Task Invoice_waits_on_its_customer_and_items_and_requests_them()
    {
        await _h.ConnectAsync();
        await _h.Send(TestPayloads.Customer());   // payload here, not in QuickBooks, out of scope

        var invoice = TestPayloads.Invoice(lines: [new TestPayloads.Line("I-1", 1, 100m), new TestPayloads.Line("I-2", 2, 50m)]);
        var result = await _h.Send(invoice);

        result.Outcome.Should().Be(GatewayOutcome.WaitingOnDependency);
        result.State.Should().Be(SyncState.WaitingOnDependency);
        result.MissingDependencies.Should().BeEquivalentTo(new[]
        {
            new GatewayDependency(SyncKind.Customer, "C-1"),
            new GatewayDependency(SyncKind.Item, "I-1"),
            new GatewayDependency(SyncKind.Item, "I-2")
        });

        var entry = await _h.OpenEntryAsync(SyncKind.SalesInvoice, "SI-1");
        entry.Status.Should().Be(OutboxStatus.WaitingOnDependency);
        entry.WaitingSince.Should().Be(_h.Now);
        entry.DependsOnJson.Should().Contain("C-1").And.Contain("I-1").And.Contain("I-2");

        // The customer's payload was already here: requested and queued at once.
        var customer = await _h.MapAsync(SyncKind.Customer, "C-1");
        customer.RequestedAt.Should().Be(_h.Now);
        customer.State.Should().Be(SyncState.Pending);
        (await _h.EntriesAsync(SyncKind.Customer, "C-1")).Should().ContainSingle(e => e.Status == OutboxStatus.Queued);

        // Items never sent: placeholders that ask for them.
        var item = await _h.MapAsync(SyncKind.Item, "I-2");
        item.PayloadJson.Should().BeNull();
        item.RequestedAt.Should().Be(_h.Now);
        (await _h.EntriesAsync(SyncKind.Item, "I-2")).Should().BeEmpty();
    }

    [Fact]
    public async Task Invoice_whose_dependencies_are_all_in_QuickBooks_is_queued_straight_away()
    {
        await _h.ConnectAsync();
        await using (var db = _h.DbAs(_h.OrgId))
        {
            var connection = await db.Connections.SingleAsync();
            db.EntityMaps.Add(new EntityMap { ConnectionId = connection.Id, Kind = SyncKind.Customer, ExternalId = "C-1", DisplayLabel = "c", RemoteId = "58" });
            db.EntityMaps.Add(new EntityMap { ConnectionId = connection.Id, Kind = SyncKind.Item, ExternalId = "I-1", DisplayLabel = "i", RemoteId = "12" });
            await db.SaveChangesAsync();
        }

        (await _h.Send(TestPayloads.Invoice())).Should().BeEquivalentTo(GatewayResult.Accepted(SyncState.Pending));
    }

    [Fact]
    public async Task External_callers_records_are_mapped_under_their_own_source_system()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);

        await _h.Send(TestPayloads.Customer(id: "123", name: "Scm Customer"));

        _h.Caller.SourceSystem = "POS";
        _h.Caller.IsExternalClient = true;
        await _h.Send(TestPayloads.Customer(id: "123", name: "Pos Customer"));

        (await _h.MapAsync(SyncKind.Customer, "123")).DisplayLabel.Should().Be("Scm Customer");
        (await _h.MapAsync(SyncKind.Customer, "123", "POS")).DisplayLabel.Should().Be("Pos Customer");

        // A POS invoice waits for the POS customer, never SCM's.
        var invoice = await _h.Send(TestPayloads.Invoice(id: "SI-9", customer: "123"));
        invoice.MissingDependencies.Should().Contain(new GatewayDependency(SyncKind.Customer, "123"));
        (await _h.MapAsync(SyncKind.Item, "I-1", "POS")).RequestedAt.Should().NotBeNull();
        (await _h.FindMapAsync(SyncKind.Item, "I-1")).Should().BeNull("SCM's item namespace is untouched");
    }

    [Fact]
    public async Task Two_organizations_with_the_same_ExternalId_are_isolated()
    {
        var orgB = Guid.NewGuid();
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive, organizationId: orgB, realmId: "9130000000000002");

        await _h.Send(TestPayloads.Customer(name: "Org A Customer"));
        _h.Tenant.RequestOrganizationId = orgB;
        await _h.Send(TestPayloads.Customer(name: "Org B Customer"));

        var statusB = await _h.Gateway(async g => { var s = await g.GetStatusAsync(SyncKind.Customer, ["C-1"]); return GatewayResult.Accepted(s.Single().State); });
        statusB.State.Should().Be(SyncState.Pending);

        await using var dbA = _h.DbAs(_h.OrgId);
        await using var dbB = _h.DbAs(orgB);
        (await dbA.EntityMaps.SingleAsync()).DisplayLabel.Should().Be("Org A Customer");
        (await dbB.EntityMaps.SingleAsync()).DisplayLabel.Should().Be("Org B Customer");
        (await dbA.Outbox.CountAsync()).Should().Be(1);
        (await dbB.Outbox.CountAsync()).Should().Be(1);
        (await dbB.Outbox.SingleAsync()).ConnectionId.Should().NotBe((await dbA.Outbox.SingleAsync()).ConnectionId);
    }

    [Fact]
    public async Task Void_of_an_unknown_invoice_is_a_no_op()
    {
        await _h.ConnectAsync();
        var result = await _h.Gateway(g => g.VoidSalesInvoiceAsync("NOPE"));
        result.Should().BeEquivalentTo(GatewayResult.Accepted(SyncState.Voided));
        await using var db = _h.SuperDb();
        (await db.EntityMaps.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Void_before_the_invoice_reached_QuickBooks_voids_it_locally_and_cancels_the_queue()
    {
        await _h.ConnectAsync();
        await _h.Send(TestPayloads.Invoice());    // waiting on dependencies

        var result = await _h.Gateway(g => g.VoidSalesInvoiceAsync("SI-1"));

        result.Should().BeEquivalentTo(GatewayResult.Accepted(SyncState.Voided));
        (await _h.MapAsync(SyncKind.SalesInvoice, "SI-1")).State.Should().Be(SyncState.Voided);
        (await _h.EntriesAsync(SyncKind.SalesInvoice, "SI-1")).Should().OnlyContain(e => e.Status == OutboxStatus.Done);

        // Later upserts of a voided invoice change nothing.
        (await _h.Send(TestPayloads.Invoice())).Should().BeEquivalentTo(GatewayResult.Accepted(SyncState.Voided));
    }

    [Fact]
    public async Task Void_of_a_pushed_invoice_queues_a_void_and_a_Cancelled_upsert_is_a_void()
    {
        await _h.ConnectAsync();
        await using (var db = _h.DbAs(_h.OrgId))
        {
            var connection = await db.Connections.SingleAsync();
            db.EntityMaps.Add(new EntityMap { ConnectionId = connection.Id, Kind = SyncKind.SalesInvoice, ExternalId = "SI-1", DisplayLabel = "INV-0001", RemoteId = "901", RemoteSyncToken = "0", State = SyncState.Synced });
            await db.SaveChangesAsync();
        }

        var cancelled = TestPayloads.Invoice();
        cancelled.Status = SalesInvoicePayloadStatus.Cancelled;
        var result = await _h.Send(cancelled);

        result.Should().BeEquivalentTo(GatewayResult.Accepted(SyncState.Pending));
        var entry = await _h.OpenEntryAsync(SyncKind.SalesInvoice, "SI-1");
        entry.Operation.Should().Be(OutboxOperation.Void);
    }

    [Fact]
    public async Task Skipped_in_matching_is_Disabled()
    {
        var connection = await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await _h.Send(TestPayloads.Customer());
        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        await using (var db = _h.DbAs(_h.OrgId))
        {
            db.MatchCandidates.Add(new MatchCandidate { ConnectionId = connection.Id, EntityMapId = map.Id, Kind = SyncKind.Customer, Decision = MatchDecision.Skip });
            await db.SaveChangesAsync();
        }

        (await _h.Send(TestPayloads.Customer(name: "Changed"))).Outcome.Should().Be(GatewayOutcome.Disabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Missing_ExternalId_is_Invalid(string id)
    {
        await _h.ConnectAsync();
        var result = await _h.Send(TestPayloads.Customer(id: id));
        result.Outcome.Should().Be(GatewayOutcome.Invalid);
        result.Errors.Should().ContainSingle(e => e.Code == "EXTERNAL_ID_REQUIRED");
    }

    [Fact]
    public async Task ExternalId_is_trimmed_into_one_key()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await _h.Send(TestPayloads.Customer(id: "  C-7 "));
        await _h.Send(TestPayloads.Customer(id: "C-7"));

        await using var db = _h.SuperDb();
        (await db.EntityMaps.CountAsync()).Should().Be(1);
        (await db.EntityMaps.SingleAsync()).ExternalId.Should().Be("C-7");
    }

    [Fact]
    public async Task Status_lookup_returns_state_and_a_deep_link_for_mapped_records_only()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await _h.Send(TestPayloads.Customer());
        await _h.Send(TestPayloads.Customer(id: "C-2", name: "Second"));
        await _h.DrainAsync();

        var statuses = await _h.Scoped(sp => sp.GetRequiredService<IQuickBooksGateway>()
            .GetStatusAsync(SyncKind.Customer, ["C-2", "C-1", "UNKNOWN"]));

        statuses.Select(s => s.ExternalId).Should().Equal("C-2", "C-1");
        statuses.Should().OnlyContain(s => s.State == SyncState.Synced && s.RemoteId != null && s.DeepLink!.Contains(s.RemoteId!));
    }

    [Fact]
    public async Task The_gateway_never_calls_QuickBooks()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await _h.Send(TestPayloads.Customer());
        await _h.Send(TestPayloads.Item());
        await _h.Send(TestPayloads.Invoice());
        await _h.Gateway(g => g.VoidSalesInvoiceAsync("SI-1"));

        _h.Provider.Calls.Should().BeEmpty();
    }
}
