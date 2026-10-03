using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Matching;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Jobs;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Integration.QuickBooks;
using Script = SMS.Modules.Integration.Tests.Fakes.ScriptedAccountingProvider.Script;

namespace SMS.Modules.Integration.Tests.Sync;

/// <summary>
/// The same engine on SQL Server (a throwaway LocalDB database per test, dropped afterwards): what
/// the in-memory provider cannot show — the unique index as the ledger's race guard, conditional
/// takeovers, RowVersion on maps, set-based sweeps and deletes, and that every query translates.
/// </summary>
[Trait("Category", "LocalDb")]
public class LocalDbTests
{
    private static SyncHarness NewHarness() => new(sqlConnectionString: SyncHarness.LocalDb());

    private static async Task StartTogether(int count, Func<int, Task> work)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count).Select(async i => { await gate.Task; await work(i); }).ToList();
        gate.SetResult();
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Concurrent_claims_of_one_command_let_exactly_one_worker_proceed()
    {
        await using var h = NewHarness();
        await h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await h.Send(TestPayloads.Customer());
        var map = await h.MapAsync(SyncKind.Customer, "C-1");

        var decisions = new System.Collections.Concurrent.ConcurrentBag<SyncClaimDecisionKind>();
        await StartTogether(8, async _ =>
        {
            var decision = await h.Scoped(sp => sp.GetRequiredService<ISyncLedger>()
                .ClaimAsync(map, OutboxOperation.Upsert, map.PayloadFingerprint!, h.Now));
            decisions.Add(decision.Kind);
        });

        decisions.Count(d => d == SyncClaimDecisionKind.Proceed).Should().Be(1);
        decisions.Count(d => d == SyncClaimDecisionKind.InFlight).Should().Be(7);
        (await h.ClaimsAsync(map.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Concurrent_takeovers_of_an_unknown_claim_let_exactly_one_worker_look_it_up()
    {
        await using var h = NewHarness();
        await h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await h.Send(TestPayloads.Customer());
        var map = await h.MapAsync(SyncKind.Customer, "C-1");
        await using (var db = h.SuperDb())
        {
            db.CommandClaims.Add(new SyncCommandClaim
            {
                OrganizationId = h.OrgId, EntityMapId = map.Id, Status = ClaimStatus.Unknown, AttemptCount = 1,
                CommandKey = SyncPayloads.CommandKey(map.ConnectionId, map.SourceSystem, map.Kind, map.ExternalId, OutboxOperation.Upsert, map.PayloadFingerprint!)
            });
            await db.SaveChangesAsync();
        }

        var decisions = new System.Collections.Concurrent.ConcurrentBag<SyncClaimDecisionKind>();
        await StartTogether(8, async _ =>
        {
            var decision = await h.Scoped(sp => sp.GetRequiredService<ISyncLedger>()
                .ClaimAsync(map, OutboxOperation.Upsert, map.PayloadFingerprint!, h.Now));
            decisions.Add(decision.Kind);
        });

        decisions.Count(d => d == SyncClaimDecisionKind.NeedsLookup).Should().Be(1);
        decisions.Count(d => d == SyncClaimDecisionKind.InFlight).Should().Be(7);
        var claim = (await h.ClaimsAsync(map.Id)).Single();
        claim.Status.Should().Be(ClaimStatus.InFlight);
        claim.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_sends_of_one_new_record_make_one_map_and_one_QuickBooks_record()
    {
        await using var h = NewHarness();
        await h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);

        var results = new System.Collections.Concurrent.ConcurrentBag<GatewayResult>();
        await StartTogether(6, async i =>
        {
            var p = TestPayloads.Customer();
            p.Notes = $"send {i}";
            results.Add(await h.Send(p));
        });

        results.Should().OnlyContain(r => r.Outcome == GatewayOutcome.Accepted);
        await using (var db = h.SuperDb())
            (await db.EntityMaps.CountAsync()).Should().Be(1, "the unique key holds and the loser retries onto the winner's map");

        await h.DrainAsync();
        h.Provider.Of(SyncKind.Customer).Should().ContainSingle();
    }

    [Fact]
    public async Task A_newer_payload_stored_while_a_create_is_on_the_wire_is_kept_and_sent_next()
    {
        await using var h = NewHarness();
        await h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await h.Send(TestPayloads.Customer());

        var newer = TestPayloads.Customer();
        newer.Phone = "042-NEW";
        h.Provider.DuringCreate = async _ => (await h.Send(newer)).State.Should().Be(SyncState.Pending);

        await h.RunOutboxAsync();

        var map = await h.MapAsync(SyncKind.Customer, "C-1");
        map.RemoteId.Should().NotBeNull("the create's result survived the RowVersion conflict");
        map.State.Should().Be(SyncState.Pending, "the newer payload has not been sent yet");
        map.LastPushedFingerprint.Should().NotBe(map.PayloadFingerprint);
        map.PayloadJson.Should().Contain("042-NEW", "the gateway's payload was not overwritten");
        (await h.EntriesAsync(SyncKind.Customer, "C-1")).Should().Contain(e => e.Status == OutboxStatus.Queued, "a follow-up entry waits");

        h.Clock.Advance(TimeSpan.FromSeconds(2));
        await h.DrainAsync();

        map = await h.MapAsync(SyncKind.Customer, "C-1");
        map.State.Should().Be(SyncState.Synced);
        map.LastPushedFingerprint.Should().Be(map.PayloadFingerprint);
        h.Provider.Calls.Select(c => c.Operation).Should().Equal("Create", "Update");
        h.Provider.Of(SyncKind.Customer).Should().ContainSingle();
    }

    [Fact]
    public async Task The_whole_pipeline_translates_and_runs_on_SQL_Server()
    {
        await using var h = NewHarness();
        await h.ConnectAsync();

        // D-2 (UPPER() comparisons), dependencies (OPENJSON / LIKE), pulls, release, push.
        h.Source.Has(TestPayloads.Customer(id: "C-1", name: "Acme Traders", code: "C001"));
        h.Source.Has(TestPayloads.Item());
        await h.Send(TestPayloads.Customer(id: "C-2", name: "ACME TRADERS", code: "C002"));
        await h.Send(TestPayloads.Invoice(customer: "C-1"));
        await h.Send(TestPayloads.Invoice(id: "SI-2", doc: "INV-0002", customer: "C-2"));

        h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Transient, ApplyAnyway: true));   // the first create times out
        await h.DrainAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.DrainAsync();

        (await h.MapAsync(SyncKind.SalesInvoice, "SI-1")).State.Should().Be(SyncState.Synced);
        (await h.MapAsync(SyncKind.SalesInvoice, "SI-2")).State.Should().Be(SyncState.Synced);

        // C-2 went first (its create timed out, then was found by its reserved name); C-1 then yielded
        // the plain name. Two records, two QuickBooks customers — never one shared.
        h.Provider.Of(SyncKind.Customer).Select(c => c.Name).Should().BeEquivalentTo("ACME TRADERS", "Acme Traders (C001)");
        var c1 = await h.MapAsync(SyncKind.Customer, "C-1");
        var c2 = await h.MapAsync(SyncKind.Customer, "C-2");
        c1.RemoteId.Should().NotBe(c2.RemoteId);
        c2.RemoteName.Should().Be("ACME TRADERS");
        c1.RemoteName.Should().Be("Acme Traders (C001)");
        h.Provider.Of(SyncKind.Item).Should().ContainSingle();
        h.Provider.Of(SyncKind.SalesInvoice).Should().HaveCount(2);

        // The dashboard's queries.
        var summary = await h.Scoped(sp => sp.GetRequiredService<IQuickBooksSyncAdminService>().GetSummaryAsync());
        summary.Kinds.Single(k => k.Kind == "SalesInvoice").CountsByState["Synced"].Should().Be(2);
        var items = await h.Scoped(sp => sp.GetRequiredService<IQuickBooksSyncAdminService>().GetItemsAsync(new SyncItemQuery { Search = "INV-0002" }));
        items.Data.Should().ContainSingle();
        var log = await h.Scoped(sp => sp.GetRequiredService<IQuickBooksSyncAdminService>().GetLogAsync(items.Data[0].Id));
        log.Should().NotBeEmpty();

        // Void, resolve, status.
        (await h.Gateway(g => g.VoidSalesInvoiceAsync("SI-2"))).State.Should().Be(SyncState.Pending);
        await h.DrainAsync();
        (await h.MapAsync(SyncKind.SalesInvoice, "SI-2")).State.Should().Be(SyncState.Voided);
        var statuses = await h.Scoped(sp => sp.GetRequiredService<IQuickBooksGateway>().GetStatusAsync(SyncKind.SalesInvoice, ["SI-1", "SI-2"]));
        statuses.Should().HaveCount(2);
    }

    [Fact]
    public async Task Matching_sweep_retention_and_reconciliation_run_on_SQL_Server()
    {
        await using var h = NewHarness();
        var connection = await h.ConnectAsync();
        h.Provider.Seed(SyncKind.Vendor, "Karachi Supplies Pvt Ltd", accountNumber: "S001");
        await h.Send(TestPayloads.Vendor());

        var scan = await h.Scoped(sp => sp.GetRequiredService<IQuickBooksMatchingService>().ScanAsync(SyncKind.Vendor));
        scan.Exact.Should().Be(1);
        var candidate = (await h.Scoped(sp => sp.GetRequiredService<IQuickBooksMatchingService>().ListAsync(SyncKind.Vendor, "Pending"))).Single();
        await h.Scoped(sp => sp.GetRequiredService<IQuickBooksMatchingService>().ConfirmAsync(SyncKind.Vendor,
            new ConfirmMatchesRequest { Decisions = [new MatchDecisionItem { CandidateId = candidate.Id, Decision = "Link" }] }, 1));
        (await h.MapAsync(SyncKind.Vendor, "V-1")).LinkOrigin.Should().Be(LinkOrigin.Adopted);

        // Set-based sweep (ExecuteUpdate) and retention (ExecuteDelete).
        var map = await h.MapAsync(SyncKind.Vendor, "V-1");
        await using (var db = h.SuperDb())
        {
            db.CommandClaims.Add(new SyncCommandClaim { OrganizationId = h.OrgId, EntityMapId = map.Id, CommandKey = "stale", Status = ClaimStatus.InFlight, LeaseExpiresAt = h.Now.AddMinutes(-10) });
            db.SyncLog.Add(new SyncLogEntry { OrganizationId = h.OrgId, ConnectionId = connection.Id, Operation = "Create", Outcome = "Succeeded", CreatedAt = h.Now.AddDays(-200) });
            await db.SaveChangesAsync();
        }
        (await h.RunSweepAsync()).expired.Should().Be(1);
        (await h.AsJob(sp => sp.GetRequiredService<SyncLogRetentionJob>().PurgeAsync())).Should().Be(1);

        await h.RunReconciliationAsync();
        await using var check = h.SuperDb();
        (await check.Settings.SingleAsync()).LastReconciledAt.Should().Be(h.Now);
        (await check.CommandClaims.SingleAsync(c => c.CommandKey == "stale")).Status.Should().Be(ClaimStatus.Unknown);
    }
}
