using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Controllers.Admin;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Matching;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway;
using SMS.Modules.Integration.Gateway.Validation;
using SMS.Modules.Integration.Jobs;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Sync;

/// <summary>The recurring jobs: per-connection tenant scoping, isolation of failures, sweep, dependency pulls, reconciliation, retention.</summary>
public class JobTests : IAsyncLifetime
{
    private readonly SyncHarness _h = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task The_outbox_job_runs_every_organization_each_inside_its_own_tenant()
    {
        var orgB = Guid.NewGuid();
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive, organizationId: orgB, realmId: "REALM-B");

        await _h.Send(TestPayloads.Customer(name: "Org A Customer"));
        _h.Tenant.RequestOrganizationId = orgB;
        await _h.Send(TestPayloads.Customer(name: "Org B Customer"));
        _h.Tenant.RequestOrganizationId = _h.OrgId;

        (await _h.RunOutboxAsync()).Should().Be(2);

        _h.Provider.Calls.Should().HaveCount(2);
        _h.Provider.Calls.Single(c => c.RealmId == SyncHarness.Realm).Entity.Should().BeOfType<RemoteCustomer>()
            .Which.DisplayName.Should().Be("Org A Customer");
        _h.Provider.Calls.Single(c => c.RealmId == "REALM-B").Entity.Should().BeOfType<RemoteCustomer>()
            .Which.DisplayName.Should().Be("Org B Customer");

        // Every row the job wrote landed under its own organization.
        await using var db = _h.SuperDb();
        foreach (var claim in await db.CommandClaims.ToListAsync())
            (await db.EntityMaps.SingleAsync(m => m.Id == claim.EntityMapId)).OrganizationId.Should().Be(claim.OrganizationId);
        (await db.SyncLog.ToListAsync()).Select(l => l.OrganizationId).Should().BeEquivalentTo(new[] { _h.OrgId, orgB });
        HangfireTenantScope.OrganizationId.Should().BeNull("the job restores the tenant scope");
    }

    [Fact]
    public async Task One_broken_company_does_not_stop_the_others()
    {
        var orgB = Guid.NewGuid();
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive, organizationId: orgB, realmId: "REALM-B");
        await _h.Send(TestPayloads.Customer());
        _h.Tenant.RequestOrganizationId = orgB;
        await _h.Send(TestPayloads.Customer());
        _h.Tenant.RequestOrganizationId = _h.OrgId;

        _h.Provider.ThrowForRealms.Add(SyncHarness.Realm);
        await _h.RunOutboxAsync();

        (await _h.MapAsync(SyncKind.Customer, "C-1", organizationId: orgB)).State.Should().Be(SyncState.Synced);
        (await _h.MapAsync(SyncKind.Customer, "C-1")).State.Should().Be(SyncState.Pending);
    }

    [Fact]
    public async Task Only_usable_connections_are_worked_and_the_batch_size_is_respected()
    {
        _h.Jobs.OutboxBatchSize = 2;
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        for (var i = 1; i <= 3; i++) await _h.Send(TestPayloads.Customer(id: $"C-{i}", name: $"Customer {i}"));

        (await _h.RunOutboxAsync()).Should().Be(2);
        (await _h.RunOutboxAsync()).Should().Be(1);

        await _h.ConnectAsync(status: ConnectionStatus.Expired, organizationId: Guid.NewGuid(), realmId: "REALM-X");
        (await _h.RunOutboxAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_sweep_expires_quiet_claims_and_requeues_stuck_entries_but_not_live_ones()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await _h.Send(TestPayloads.Customer());
        await _h.Send(TestPayloads.Customer(id: "C-2", name: "Two"));
        var one = await _h.MapAsync(SyncKind.Customer, "C-1");
        var two = await _h.MapAsync(SyncKind.Customer, "C-2");

        await using (var db = _h.SuperDb())
        {
            db.CommandClaims.Add(new SyncCommandClaim { OrganizationId = _h.OrgId, EntityMapId = one.Id, CommandKey = "old", Status = ClaimStatus.InFlight, LeaseExpiresAt = _h.Now.AddMinutes(-3) });
            db.CommandClaims.Add(new SyncCommandClaim { OrganizationId = _h.OrgId, EntityMapId = two.Id, CommandKey = "live", Status = ClaimStatus.InFlight, LeaseExpiresAt = _h.Now.AddMinutes(-1) });
            var entries = await db.Outbox.OrderBy(e => e.Id).ToListAsync();
            entries[0].Status = OutboxStatus.Running; entries[0].NextAttemptAt = _h.Now.AddMinutes(-8);
            entries[1].Status = OutboxStatus.Running; entries[1].NextAttemptAt = _h.Now.AddMinutes(-2);
            await db.SaveChangesAsync();
        }

        (await _h.RunSweepAsync()).Should().Be((1, 1));

        await using var check = _h.SuperDb();
        (await check.CommandClaims.SingleAsync(c => c.CommandKey == "old")).Status.Should().Be(ClaimStatus.Unknown);
        (await check.CommandClaims.SingleAsync(c => c.CommandKey == "live")).Status.Should().Be(ClaimStatus.InFlight, "still within lease + grace");
        (await check.Outbox.SingleAsync(e => e.EntityMapId == one.Id)).Status.Should().Be(OutboxStatus.Queued);
        (await check.Outbox.SingleAsync(e => e.EntityMapId == two.Id)).Status.Should().Be(OutboxStatus.Running);
    }

    [Fact]
    public async Task Dependency_job_pulls_placeholders_from_the_source_and_not_again_within_fifteen_minutes()
    {
        await _h.ConnectAsync();
        await _h.Send(TestPayloads.Invoice());           // needs C-1 and I-1; the source has neither

        await _h.RunDependenciesAsync();
        _h.Source.PushCalls.Should().BeEquivalentTo(new[] { (SyncKind.Customer, new[] { "C-1" }), (SyncKind.Item, new[] { "I-1" }) });

        await _h.RunDependenciesAsync();
        _h.Source.PushCalls.Should().HaveCount(2, "asked again only after the re-pull interval");

        _h.Clock.Advance(DependencyJob.RepullAfter);
        await _h.RunDependenciesAsync();
        _h.Source.PushCalls.Should().HaveCount(4);
    }

    [Fact]
    public async Task External_callers_placeholders_are_never_pulled()
    {
        await _h.ConnectAsync();
        _h.Caller.SourceSystem = "POS";
        _h.Caller.IsExternalClient = true;
        await _h.Send(TestPayloads.Invoice());

        await _h.RunDependenciesAsync();

        _h.Source.PushCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Waiting_documents_fail_with_DEPENDENCY_TIMEOUT_after_the_wait_and_are_released_when_ready()
    {
        await _h.ConnectAsync();
        await _h.Send(TestPayloads.Invoice(id: "SI-1", doc: "INV-1"));
        await _h.Send(TestPayloads.Invoice(id: "SI-2", doc: "INV-2", customer: "C-2", lines: [new TestPayloads.Line("I-2", 1, 10m)]));

        // SI-2's dependencies arrive in QuickBooks by another route (e.g. linked in matching).
        await using (var db = _h.SuperDb())
        {
            foreach (var m in await db.EntityMaps.Where(m => m.ExternalId == "C-2" || m.ExternalId == "I-2").ToListAsync())
                m.RemoteId = "R-" + m.ExternalId;
            await db.SaveChangesAsync();
        }

        _h.Clock.Advance(TimeSpan.FromDays(7));
        await _h.RunDependenciesAsync();

        var timedOut = await _h.MapAsync(SyncKind.SalesInvoice, "SI-1");
        timedOut.State.Should().Be(SyncState.Failed);
        timedOut.LastErrorCode.Should().Be("DEPENDENCY_TIMEOUT");
        timedOut.LastError.Should().Contain("Customer C-1").And.Contain("Item I-1");
        (await _h.EntriesAsync(SyncKind.SalesInvoice, "SI-1")).Single().Status.Should().Be(OutboxStatus.Failed);

        (await _h.OpenEntryAsync(SyncKind.SalesInvoice, "SI-2")).Status.Should().Be(OutboxStatus.Queued, "released, not timed out");
    }

    [Fact]
    public async Task Reconciliation_asks_every_enabled_kind_since_the_last_complete_run()
    {
        var connection = await _h.ConnectAsync(s => s.AutoPushBills = false);
        var connectedAt = connection.ConnectedAt;

        await _h.RunReconciliationAsync();

        _h.Source.PushAllCalls.Select(c => c.Kind).Should().Equal(SyncKind.Customer, SyncKind.Vendor, SyncKind.Item, SyncKind.SalesInvoice);
        _h.Source.PushAllCalls.Where(c => c.Kind != SyncKind.Item)
            .Should().OnlyContain(c => c.Since == connectedAt, "the first run starts from when the company was connected");
        _h.Source.PushAllCalls.Single(c => c.Kind == SyncKind.Item).Since
            .Should().BeNull("variants have no modified timestamp, so the first run of a day is a full item pass");
        var firstRun = _h.Now;
        await using (var db = _h.SuperDb())
            (await db.Settings.SingleAsync()).LastReconciledAt.Should().Be(firstRun);

        // An hour later on the same UTC day (the harness clock starts at 09:00): everything since the last run.
        _h.Clock.Advance(TimeSpan.FromHours(1));
        _h.Source.PushAllCalls.Clear();
        await _h.RunReconciliationAsync();
        _h.Source.PushAllCalls.Should().OnlyContain(c => c.Since == firstRun);
    }

    [Fact]
    public async Task Items_get_one_full_reconciliation_pass_on_the_first_run_of_each_day()
    {
        await _h.ConnectAsync();
        await _h.RunReconciliationAsync();
        var firstRun = _h.Now;

        _h.Clock.Advance(TimeSpan.FromDays(1));
        _h.Source.PushAllCalls.Clear();
        await _h.RunReconciliationAsync();

        _h.Source.PushAllCalls.Single(c => c.Kind == SyncKind.Item).Since.Should().BeNull();
        _h.Source.PushAllCalls.Where(c => c.Kind != SyncKind.Item).Should().OnlyContain(c => c.Since == firstRun);
    }

    [Fact]
    public async Task A_failing_source_does_not_advance_the_reconciliation_point()
    {
        await _h.ConnectAsync();
        await _h.RunReconciliationAsync();
        var first = _h.Now;

        _h.Clock.Advance(TimeSpan.FromHours(1));
        _h.Source.ThrowOnPushAll.Add(SyncKind.Item);
        await _h.RunReconciliationAsync();

        _h.Source.PushAllCalls.Should().Contain(c => c.Kind == SyncKind.SalesInvoice && c.Since == first, "one kind failing does not stop the rest");
        await using var db = _h.SuperDb();
        (await db.Settings.SingleAsync()).LastReconciledAt.Should().Be(first);
    }

    [Fact]
    public async Task Reconciliation_resends_through_the_gateway_so_changed_records_are_queued()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        _h.Source.Has(TestPayloads.Customer());

        await _h.RunReconciliationAsync();

        (await _h.MapAsync(SyncKind.Customer, "C-1")).State.Should().Be(SyncState.Pending);
    }

    [Fact]
    public async Task Log_retention_deletes_only_rows_older_than_the_retention_period()
    {
        var connection = await _h.ConnectAsync();
        await using (var db = _h.SuperDb())
        {
            db.SyncLog.Add(new SyncLogEntry { OrganizationId = _h.OrgId, ConnectionId = connection.Id, Operation = "Create", Outcome = "Succeeded", CreatedAt = _h.Now.AddDays(-91) });
            db.SyncLog.Add(new SyncLogEntry { OrganizationId = Guid.NewGuid(), ConnectionId = 999, Operation = "Create", Outcome = "Succeeded", CreatedAt = _h.Now.AddDays(-120) });
            db.SyncLog.Add(new SyncLogEntry { OrganizationId = _h.OrgId, ConnectionId = connection.Id, Operation = "Update", Outcome = "Succeeded", CreatedAt = _h.Now.AddDays(-89) });
            await db.SaveChangesAsync();
        }

        var deleted = await _h.AsJob(sp => sp.GetRequiredService<SyncLogRetentionJob>().PurgeAsync());

        deleted.Should().Be(2, "every organization's old rows go");
        await using var check = _h.SuperDb();
        (await check.SyncLog.SingleAsync()).Operation.Should().Be("Update");
    }

    [Fact]
    public void Every_service_the_gateway_package_registers_resolves()
    {
        using var scope = _h.Services.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<IQuickBooksGateway>().Should().BeOfType<QuickBooksGateway>();
        sp.GetRequiredService<IPayloadValidator>().Should().NotBeNull();
        sp.GetRequiredService<IRemoteNameResolver>().Should().NotBeNull();
        sp.GetRequiredService<IQboObjectBuilder>().Should().NotBeNull();
        sp.GetRequiredService<ISyncOutbox>().Should().NotBeNull();
        sp.GetRequiredService<ISyncLedger>().Should().NotBeNull();
        sp.GetRequiredService<ISyncDependencies>().Should().NotBeNull();
        sp.GetRequiredService<ISyncExecutor>().Should().NotBeNull();
        sp.GetRequiredService<IOutboxControl>().Should().BeOfType<OutboxControl>();
        sp.GetRequiredService<IQuickBooksSyncAdminService>().Should().NotBeNull();
        sp.GetRequiredService<IQuickBooksMatchingService>().Should().NotBeNull();
        sp.GetRequiredService<SyncOutboxJob>().Should().NotBeNull();
        sp.GetRequiredService<SyncSweepJob>().Should().NotBeNull();
        sp.GetRequiredService<ReconciliationJob>().Should().NotBeNull();
        sp.GetRequiredService<DependencyJob>().Should().NotBeNull();
        sp.GetRequiredService<SyncLogRetentionJob>().Should().NotBeNull();
        new SyncController(sp.GetRequiredService<IQuickBooksSyncAdminService>()).Should().NotBeNull();
        new MatchingController(sp.GetRequiredService<IQuickBooksMatchingService>()).Should().NotBeNull();
    }

    [Fact]
    public void The_real_caller_context_maps_API_clients_to_their_name_and_never_to_SCM()
    {
        static IGatewayCallerContext For(params (string type, string value)[] claims)
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            if (claims.Length > 0)
                http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    claims.Select(c => new System.Security.Claims.Claim(c.type, c.value)), "IntegrationApiKey"));
            return new HttpGatewayCallerContext(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = http });
        }

        var scm = For();
        scm.SourceSystem.Should().Be(QuickBooksSourceSystems.Scm);
        scm.IsExternalClient.Should().BeFalse();

        var jwtUser = For(("organizationId", Guid.NewGuid().ToString()), ("sub", "7"));
        jwtUser.SourceSystem.Should().Be(QuickBooksSourceSystems.Scm, "an SCM user acting through the admin screens is SCM");

        var pos = For(("api_client_id", "12"), ("api_client_name", "POS"));
        pos.SourceSystem.Should().Be("POS");
        pos.IsExternalClient.Should().BeTrue();
        pos.ApiClientId.Should().Be("12");

        For(("api_client_id", "13"), ("api_client_name", "scm")).SourceSystem.Should().Be("api:scm");
        For(("api_client_id", "14")).SourceSystem.Should().Be("api:14");
    }
}
