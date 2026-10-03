using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Integration.QuickBooks;
using Script = SMS.Modules.Integration.Tests.Fakes.ScriptedAccountingProvider.Script;

namespace SMS.Modules.Integration.Tests.Sync;

/// <summary>
/// The executor and the outbox job against a scripted QuickBooks: outcomes, the ledger, retries,
/// and above all — never a second record for one caller record.
/// </summary>
public class ExecutorTests : IAsyncLifetime
{
    private readonly SyncHarness _h = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<IntegrationConnection> LiveAllActive(Action<IntegrationSettings>? more = null) =>
        _h.ConnectAsync(s => { s.PartnerScope = PartnerScope.AllActive; more?.Invoke(s); });

    /// <summary>A record already in QuickBooks (map with a RemoteId, and the record in the fake company).</summary>
    private async Task<string> SeedMappedAsync(SyncKind kind, string externalId, string name)
    {
        var record = _h.Provider.Seed(kind, name);
        await using var db = _h.DbAs(_h.OrgId);
        var connection = await db.Connections.SingleAsync();
        db.EntityMaps.Add(new EntityMap
        {
            ConnectionId = connection.Id, Kind = kind, ExternalId = externalId, DisplayLabel = name,
            RemoteId = record.Id, RemoteSyncToken = "0", RemoteName = name, State = SyncState.Synced, LinkOrigin = LinkOrigin.Created
        });
        await db.SaveChangesAsync();
        return record.Id;
    }

    private async Task<SyncState> StateOf(SyncKind kind, string id) => (await _h.MapAsync(kind, id)).State;

    // ── The happy path ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_create_records_the_remote_identity_the_claim_the_log_and_closes_the_entry()
    {
        await LiveAllActive();
        await _h.Send(TestPayloads.Customer());
        var entry = await _h.OpenEntryAsync(SyncKind.Customer, "C-1");

        var result = await _h.ExecuteAsync(entry.Id);

        result.Status.Should().Be(SyncExecutionStatus.Succeeded);
        var remote = _h.Provider.Of(SyncKind.Customer).Single();
        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.RemoteId.Should().Be(remote.Id);
        map.RemoteSyncToken.Should().Be("0");
        map.RemoteName.Should().Be("Acme Traders");
        map.LinkOrigin.Should().Be(LinkOrigin.Created);
        map.State.Should().Be(SyncState.Synced);
        map.LastPushedFingerprint.Should().Be(map.PayloadFingerprint);
        map.LastSyncedAt.Should().Be(_h.Now);
        map.LastError.Should().BeNull();

        (await _h.ClaimsAsync(map.Id)).Should().ContainSingle(c => c.Status == ClaimStatus.Succeeded && c.RemoteId == remote.Id);
        (await _h.EntriesAsync(SyncKind.Customer, "C-1")).Should().ContainSingle(e => e.Status == OutboxStatus.Done && e.CompletedAt == _h.Now);
        (await _h.LogAsync(map.Id)).Should().ContainSingle(l => l.Operation == "Create" && l.Outcome == "Succeeded");

        var sent = (RemoteCustomer)_h.Provider.Calls.Single().Entity!;
        sent.DisplayName.Should().Be("Acme Traders");
        sent.CurrencyCode.Should().Be("PKR");
        sent.TaxId.Should().Be("TX-C-1");
        sent.BillAddress!.City.Should().Be("Lahore");
    }

    [Fact]
    public async Task Invoice_waits_for_its_customer_and_items_which_are_pushed_then_it_is_sent_with_their_ids()
    {
        await _h.ConnectAsync();                         // only when referenced
        await _h.Send(TestPayloads.Customer());
        await _h.Send(TestPayloads.Item());
        await _h.Send(TestPayloads.Item(id: "I-2", name: "Gadget", sku: "G-1"));

        var waiting = await _h.Send(TestPayloads.Invoice(lines: [new TestPayloads.Line("I-1", 2, 100m), new TestPayloads.Line("I-2", 1, 40m, Tax: 0m)]));
        waiting.Outcome.Should().Be(GatewayOutcome.WaitingOnDependency);

        await _h.DrainAsync();

        (await StateOf(SyncKind.SalesInvoice, "SI-1")).Should().Be(SyncState.Synced);
        var customerId = (await _h.MapAsync(SyncKind.Customer, "C-1")).RemoteId;
        var item1 = (await _h.MapAsync(SyncKind.Item, "I-1")).RemoteId;
        var item2 = (await _h.MapAsync(SyncKind.Item, "I-2")).RemoteId;

        var invoice = (RemoteInvoice)_h.Provider.Calls.Single(c => c.Kind == SyncKind.SalesInvoice && c.Operation == "Create").Entity!;
        invoice.CustomerId.Should().Be(customerId);
        invoice.Lines.Select(l => l.ItemId).Should().Equal(item1, item2);
        invoice.Lines.Select(l => l.TaxCodeId).Should().Equal("TAX17", "TAX0");
        invoice.DocNumber.Should().Be("INV-0001");

        // Parties and items went first.
        var creates = _h.Provider.Calls.Where(c => c.Operation == "Create").Select(c => c.Kind).ToList();
        creates.IndexOf(SyncKind.SalesInvoice).Should().Be(creates.Count - 1);
        _h.Provider.Of(SyncKind.Customer).Should().HaveCount(1);
        _h.Provider.Of(SyncKind.Item).Should().HaveCount(2);
    }

    [Fact]
    public async Task Dependencies_never_sent_are_pulled_from_the_SCM_source_then_the_invoice_goes()
    {
        await _h.ConnectAsync();
        _h.Source.Has(TestPayloads.Customer());
        _h.Source.Has(TestPayloads.Item());

        (await _h.Send(TestPayloads.Invoice())).Outcome.Should().Be(GatewayOutcome.WaitingOnDependency);
        await _h.DrainAsync();

        _h.Source.PushCalls.Should().Contain(c => c.Kind == SyncKind.Customer && c.Ids.SequenceEqual(new[] { "C-1" }));
        _h.Source.PushCalls.Should().Contain(c => c.Kind == SyncKind.Item && c.Ids.SequenceEqual(new[] { "I-1" }));
        (await StateOf(SyncKind.SalesInvoice, "SI-1")).Should().Be(SyncState.Synced);
        _h.Provider.Of(SyncKind.SalesInvoice).Should().ContainSingle();
    }

    // ── Never twice ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_create_that_timed_out_but_succeeded_is_found_on_retry_and_never_created_twice()
    {
        await LiveAllActive();
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Transient, ApplyAnyway: true, Message: "The operation has timed out."));
        await _h.Send(TestPayloads.Customer());

        (await _h.RunOutboxAsync()).Should().Be(1);

        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.RemoteId.Should().BeNull("we do not know it succeeded");
        map.State.Should().Be(SyncState.Pending);
        map.LastErrorCode.Should().Be("TRANSIENT");
        (await _h.ClaimsAsync(map.Id)).Single().Status.Should().Be(ClaimStatus.Unknown);
        var entry = await _h.OpenEntryAsync(SyncKind.Customer, "C-1");
        entry.AttemptCount.Should().Be(1);
        entry.NextAttemptAt.Should().Be(_h.Now.AddMinutes(1));
        _h.Provider.Of(SyncKind.Customer).Should().HaveCount(1, "QuickBooks did create it");

        (await _h.RunOutboxAsync()).Should().Be(0, "not due yet");

        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        await _h.RunOutboxAsync();

        map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.State.Should().Be(SyncState.Synced);
        map.RemoteId.Should().Be(_h.Provider.Of(SyncKind.Customer).Single().Id);
        map.LinkOrigin.Should().Be(LinkOrigin.Created);
        _h.Provider.Of(SyncKind.Customer).Should().HaveCount(1, "exactly one remote record");
        _h.Provider.CallsOf("Create").Should().Be(1);
        _h.Provider.CallsOf("Find").Should().Be(1);
        (await _h.ClaimsAsync(map.Id)).Single().Status.Should().Be(ClaimStatus.Succeeded);
    }

    [Fact]
    public async Task An_invoice_create_that_timed_out_is_found_by_its_number_on_retry()
    {
        await _h.ConnectAsync();
        await SeedMappedAsync(SyncKind.Customer, "C-1", "Acme Traders");
        await SeedMappedAsync(SyncKind.Item, "I-1", "Widget");
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Transient, ApplyAnyway: true));

        await _h.Send(TestPayloads.Invoice());
        await _h.RunOutboxAsync();
        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        await _h.RunOutboxAsync();

        _h.Provider.Of(SyncKind.SalesInvoice).Should().ContainSingle();
        _h.Provider.Calls.Should().Contain(c => c.Operation == "Find" && c.Lookup!.DocNumber == "INV-0001");
        (await _h.MapAsync(SyncKind.SalesInvoice, "SI-1")).RemoteDocNumber.Should().Be("INV-0001");
    }

    [Fact]
    public async Task A_worker_that_died_mid_call_is_swept_and_the_next_attempt_looks_before_creating()
    {
        await LiveAllActive();
        await _h.Send(TestPayloads.Customer());
        var map = await _h.MapAsync(SyncKind.Customer, "C-1");

        // Simulate the dead worker: it claimed, QuickBooks created the record, and nothing was recorded.
        _h.Provider.Seed(SyncKind.Customer, "Acme Traders");
        await using (var db = _h.SuperDb())
        {
            db.CommandClaims.Add(new SyncCommandClaim
            {
                OrganizationId = _h.OrgId, EntityMapId = map.Id, Status = ClaimStatus.InFlight, AttemptCount = 1,
                CommandKey = SyncPayloads.CommandKey(map.ConnectionId, map.SourceSystem, map.Kind, map.ExternalId, OutboxOperation.Upsert, map.PayloadFingerprint!),
                LeaseExpiresAt = _h.Now.AddMinutes(5)
            });
            var entry = await db.Outbox.SingleAsync();
            entry.Status = OutboxStatus.Running;
            entry.NextAttemptAt = _h.Now;
            await db.SaveChangesAsync();
        }

        (await _h.RunOutboxAsync()).Should().Be(0, "a Running entry is not picked up");
        (await _h.RunSweepAsync()).Should().Be((0, 0), "the lease and grace have not run out");

        _h.Clock.Advance(TimeSpan.FromMinutes(8));
        (await _h.RunSweepAsync()).Should().Be((1, 1));
        (await _h.ClaimsAsync(map.Id)).Single().Status.Should().Be(ClaimStatus.Unknown);

        await _h.RunOutboxAsync();

        _h.Provider.CallsOf("Create").Should().Be(0);
        _h.Provider.Of(SyncKind.Customer).Should().HaveCount(1);
        (await StateOf(SyncKind.Customer, "C-1")).Should().Be(SyncState.Synced);
    }

    [Fact]
    public async Task A_changed_payload_while_an_earlier_create_is_unknown_looks_before_creating()
    {
        await LiveAllActive();
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Transient, ApplyAnyway: true));
        await _h.Send(TestPayloads.Customer());
        await _h.RunOutboxAsync();

        var changed = TestPayloads.Customer();
        changed.Phone = "042-000-000";
        await _h.Send(changed);          // merges: due now, fresh attempts, new fingerprint = new command
        await _h.RunOutboxAsync();

        _h.Provider.Of(SyncKind.Customer).Should().HaveCount(1);
        _h.Provider.CallsOf("Create").Should().Be(1);
        _h.Provider.CallsOf("Update").Should().Be(1, "the found record is brought up to the new payload");
        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.State.Should().Be(SyncState.Synced);
        map.LastPushedFingerprint.Should().Be(map.PayloadFingerprint);
    }

    [Fact]
    public async Task A_record_renamed_while_its_create_is_in_doubt_is_found_by_the_name_it_was_sent_under()
    {
        await LiveAllActive();
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Transient, ApplyAnyway: true));
        await _h.Send(TestPayloads.Customer(name: "Acme Traders"));
        await _h.RunOutboxAsync();                                     // "Acme Traders" exists in QuickBooks; we do not know it

        await _h.Send(TestPayloads.Customer(name: "Acme Trading Company"));
        await _h.RunOutboxAsync();

        _h.Provider.CallsOf("Create").Should().Be(1, "the in-doubt record was found under its old name, not created again");
        _h.Provider.Of(SyncKind.Customer).Should().ContainSingle().Which.Name.Should().Be("Acme Trading Company", "then renamed by a sparse update");
        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.State.Should().Be(SyncState.Synced);
        map.RemoteName.Should().Be("Acme Trading Company");
    }

    [Fact]
    public async Task A_map_that_lost_its_RemoteId_is_repaired_from_the_succeeded_claim_not_created_again()
    {
        await LiveAllActive();
        await _h.Send(TestPayloads.Customer());
        await _h.DrainAsync();
        var remoteId = (await _h.MapAsync(SyncKind.Customer, "C-1")).RemoteId;

        await using (var db = _h.SuperDb())
        {
            var m = await db.EntityMaps.SingleAsync();
            m.RemoteId = null;
            m.RemoteSyncToken = null;
            await db.SaveChangesAsync();
        }

        var changed = TestPayloads.Customer();
        changed.Notes = "changed";
        await _h.Send(changed);
        await _h.DrainAsync();

        _h.Provider.CallsOf("Create").Should().Be(1);
        _h.Provider.Of(SyncKind.Customer).Should().HaveCount(1);
        (await _h.MapAsync(SyncKind.Customer, "C-1")).RemoteId.Should().Be(remoteId);
    }

    [Fact]
    public async Task A_timed_out_create_reserves_its_name_so_a_same_named_record_is_never_linked_to_it()
    {
        await LiveAllActive();
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Transient, ApplyAnyway: true));
        await _h.Send(TestPayloads.Customer(id: "C-2", name: "ACME TRADERS", code: "C002"));
        await _h.RunOutboxAsync();                                     // created in QuickBooks, outcome unknown here
        (await _h.MapAsync(SyncKind.Customer, "C-2")).RemoteName.Should().Be("ACME TRADERS", "reserved before the call");

        await _h.Send(TestPayloads.Customer(id: "C-1", name: "Acme Traders", code: "C001"));
        await _h.RunOutboxAsync();
        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        await _h.RunOutboxAsync();

        var c1 = await _h.MapAsync(SyncKind.Customer, "C-1");
        var c2 = await _h.MapAsync(SyncKind.Customer, "C-2");
        c1.RemoteName.Should().Be("Acme Traders (C001)");
        c1.State.Should().Be(SyncState.Synced);
        c2.State.Should().Be(SyncState.Synced);
        c1.RemoteId.Should().NotBe(c2.RemoteId);
        _h.Provider.Of(SyncKind.Customer).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_found_record_that_already_backs_another_record_is_never_linked_twice()
    {
        var accountants = _h.Provider.Seed(SyncKind.Customer, "Acme Traders");
        var connection = await LiveAllActive();
        await using (var db = _h.DbAs(_h.OrgId))
        {
            // Linked by hand earlier to the same QuickBooks record, under another name.
            db.EntityMaps.Add(new EntityMap { ConnectionId = connection.Id, Kind = SyncKind.Customer, ExternalId = "C-OLD", DisplayLabel = "Acme (old)", RemoteId = accountants.Id, State = SyncState.Synced });
            await db.SaveChangesAsync();
        }

        await _h.Send(TestPayloads.Customer());
        await _h.DrainAsync();

        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.State.Should().Be(SyncState.NeedsResolution);
        map.LastErrorCode.Should().Be("REMOTE_ALREADY_LINKED");
        map.LastError.Should().Contain("Acme (old)");
        map.RemoteId.Should().BeNull();
        map.RemoteName.Should().BeNull("the reserved name is released when nothing was created");
    }

    // ── Duplicates ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Duplicate_name_with_an_exact_match_is_adopted_and_left_untouched()
    {
        var accountants = _h.Provider.Seed(SyncKind.Customer, "ACME TRADERS");
        await LiveAllActive();
        await _h.Send(TestPayloads.Customer());

        await _h.DrainAsync();

        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.RemoteId.Should().Be(accountants.Id);
        map.LinkOrigin.Should().Be(LinkOrigin.Adopted);
        map.LinkedAt.Should().Be(_h.Now);
        map.State.Should().Be(SyncState.Synced);
        map.LastPushedFingerprint.Should().Be(map.PayloadFingerprint, "adoption must not immediately overwrite the accountant's record");
        map.Warning.Should().Contain("not changed");
        _h.Provider.CallsOf("Update").Should().Be(0);
        accountants.Token.Should().Be(0);
        _h.Provider.Of(SyncKind.Customer).Should().HaveCount(1);
    }

    [Fact]
    public async Task Duplicate_name_that_cannot_be_found_as_this_kind_needs_resolution()
    {
        _h.Provider.Seed(SyncKind.Vendor, "Acme Traders");   // DisplayName is shared with vendors
        await LiveAllActive();
        await _h.Send(TestPayloads.Customer());

        await _h.DrainAsync();

        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.State.Should().Be(SyncState.NeedsResolution);
        map.RemoteId.Should().BeNull();
        map.LastError.Should().Contain("another").And.Contain("Rename");
        (await _h.EntriesAsync(SyncKind.Customer, "C-1")).Single().Status.Should().Be(OutboxStatus.Failed);
        (await _h.ClaimsAsync(map.Id)).Single().Status.Should().Be(ClaimStatus.Refused);
        _h.Provider.Of(SyncKind.Customer).Should().BeEmpty();
    }

    [Fact]
    public async Task Duplicate_document_number_is_adopted_and_brought_in_line_with_our_invoice()
    {
        await _h.ConnectAsync();
        await SeedMappedAsync(SyncKind.Customer, "C-1", "Acme Traders");
        await SeedMappedAsync(SyncKind.Item, "I-1", "Widget");
        var existing = _h.Provider.Seed(SyncKind.SalesInvoice, name: null!, docNumber: "INV-0001");

        await _h.Send(TestPayloads.Invoice());
        await _h.DrainAsync();

        var map = await _h.MapAsync(SyncKind.SalesInvoice, "SI-1");
        map.RemoteId.Should().Be(existing.Id);
        map.LinkOrigin.Should().Be(LinkOrigin.Adopted);
        map.State.Should().Be(SyncState.Synced);
        _h.Provider.CallsOf("Update").Should().Be(1);
        _h.Provider.Of(SyncKind.SalesInvoice).Should().ContainSingle();
    }

    // ── Stale objects ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_stale_SyncToken_is_refetched_and_the_update_retried_once()
    {
        await LiveAllActive();
        await _h.Send(TestPayloads.Customer());
        await _h.DrainAsync();
        var remoteId = (await _h.MapAsync(SyncKind.Customer, "C-1")).RemoteId!;
        _h.Provider.TouchInQuickBooks(remoteId);      // the accountant edited it: token 0 → 1
        _h.Provider.Calls.Clear();

        var changed = TestPayloads.Customer();
        changed.Phone = "042-123-456";
        await _h.Send(changed);
        await _h.DrainAsync();

        _h.Provider.Calls.Select(c => c.Operation).Should().Equal("Update", "Get", "Update");
        _h.Provider.Calls[0].SyncToken.Should().Be("0");
        _h.Provider.Calls[2].SyncToken.Should().Be("1");
        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.State.Should().Be(SyncState.Synced);
        map.RemoteSyncToken.Should().Be("2");
    }

    [Fact]
    public async Task Stale_again_after_the_retry_fails_the_record()
    {
        await LiveAllActive();
        await _h.Send(TestPayloads.Customer());
        await _h.DrainAsync();
        _h.Provider.Enqueue("Update", new Script(ProviderOutcomeKind.StaleObject), new Script(ProviderOutcomeKind.StaleObject));

        var changed = TestPayloads.Customer();
        changed.Phone = "042-123-456";
        await _h.Send(changed);
        await _h.DrainAsync();

        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.State.Should().Be(SyncState.Failed);
        map.LastErrorCode.Should().Be("STALE_OBJECT");
        _h.Provider.CallsOf("Update").Should().Be(2);
    }

    // ── Throttling, backoff, giving up ───────────────────────────────────────

    [Fact]
    public async Task Throttled_calls_back_off_on_schedule_and_need_resolution_after_max_attempts()
    {
        _h.Jobs.MaxAttempts = 4;
        await LiveAllActive();
        _h.Provider.Enqueue("Create", Enumerable.Repeat(new Script(ProviderOutcomeKind.Throttled, Code: "429"), 4).ToArray());
        await _h.Send(TestPayloads.Customer());

        var expected = new[] { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15) };
        foreach (var (delay, attempt) in expected.Select((d, i) => (d, i + 1)))
        {
            await _h.RunOutboxAsync();
            var entry = await _h.OpenEntryAsync(SyncKind.Customer, "C-1");
            entry.AttemptCount.Should().Be(attempt);
            entry.NextAttemptAt.Should().Be(_h.Now + delay, $"attempt {attempt} backs off {delay}");
            var map = await _h.MapAsync(SyncKind.Customer, "C-1");
            map.State.Should().Be(SyncState.Pending);
            map.LastErrorCode.Should().Be("429");
            map.LastError.Should().Contain("slow down").And.Contain($"attempt {attempt + 1} of 4");
            _h.Clock.Advance(delay);
        }

        await _h.RunOutboxAsync();

        var final = await _h.MapAsync(SyncKind.Customer, "C-1");
        final.State.Should().Be(SyncState.NeedsResolution);
        final.LastError.Should().Contain("Gave up after 4 attempts");
        (await _h.EntriesAsync(SyncKind.Customer, "C-1")).Single().Status.Should().Be(OutboxStatus.Failed);
        _h.Provider.Of(SyncKind.Customer).Should().BeEmpty();
    }

    [Fact]
    public void Backoff_schedule_is_1m_5m_15m_1h_3h_6h_12h_24h_then_stays_at_24h()
    {
        Enumerable.Range(1, 9).Select(SyncBackoff.After).Should().Equal(
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1),
            TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(24), TimeSpan.FromHours(24));
    }

    [Fact]
    public async Task Throttling_stops_that_connections_batch_for_this_run()
    {
        await LiveAllActive();
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Throttled));
        await _h.Send(TestPayloads.Customer());
        await _h.Send(TestPayloads.Customer(id: "C-2", name: "Second Customer"));

        (await _h.RunOutboxAsync()).Should().Be(1);

        (await _h.OpenEntryAsync(SyncKind.Customer, "C-2")).AttemptCount.Should().Be(0);
        _h.Provider.CallsOf("Create").Should().Be(1);
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_refusal_fails_the_record_with_the_reason_and_is_not_retried_until_the_payload_changes()
    {
        await LiveAllActive();
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Refused, Code: "6000", Message: "Invalid postal code", Field: "BillAddr.PostalCode"));
        await _h.Send(TestPayloads.Customer());
        await _h.DrainAsync();

        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.State.Should().Be(SyncState.Failed);
        map.LastErrorCode.Should().Be("6000");
        map.LastError.Should().Contain("Invalid postal code").And.Contain("BillAddr.PostalCode");
        (await _h.ClaimsAsync(map.Id)).Single().Status.Should().Be(ClaimStatus.Refused);

        // The same data again: the ledger answers, QuickBooks is not asked.
        await _h.Send(TestPayloads.Customer());
        await _h.DrainAsync();
        _h.Provider.CallsOf("Create").Should().Be(1);
        var again = await _h.MapAsync(SyncKind.Customer, "C-1");
        again.State.Should().Be(SyncState.Failed);
        again.LastError.Should().Contain("already refused").And.Contain("Invalid postal code");

        // Changed data: sent.
        var fixedPayload = TestPayloads.Customer();
        fixedPayload.BillingAddress!.PostalCode = "54000";
        await _h.Send(fixedPayload);
        await _h.DrainAsync();
        _h.Provider.CallsOf("Create").Should().Be(2);
        (await StateOf(SyncKind.Customer, "C-1")).Should().Be(SyncState.Synced);
    }

    [Fact]
    public async Task A_refused_update_of_a_document_needs_a_person_per_D9()
    {
        await _h.ConnectAsync();
        await SeedMappedAsync(SyncKind.Customer, "C-1", "Acme Traders");
        await SeedMappedAsync(SyncKind.Item, "I-1", "Widget");
        await _h.Send(TestPayloads.Invoice());
        await _h.DrainAsync();

        _h.Provider.Enqueue("Update", new Script(ProviderOutcomeKind.Refused, Message: "Transaction is paid"));
        await _h.Send(TestPayloads.Invoice(lines: [new TestPayloads.Line("I-1", 3, 100m)]));
        await _h.DrainAsync();

        var map = await _h.MapAsync(SyncKind.SalesInvoice, "SI-1");
        map.State.Should().Be(SyncState.NeedsResolution);
        map.LastError.Should().Contain("paid in QuickBooks");
    }

    [Fact]
    public async Task A_record_deleted_in_QuickBooks_needs_resolution_on_its_next_update()
    {
        await LiveAllActive();
        await _h.Send(TestPayloads.Customer());
        await _h.DrainAsync();
        _h.Provider.Company.Clear();

        var changed = TestPayloads.Customer();
        changed.Notes = "x";
        await _h.Send(changed);
        await _h.DrainAsync();

        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.State.Should().Be(SyncState.NeedsResolution);
        map.LastErrorCode.Should().Be("REMOTE_NOT_FOUND");
        _h.Provider.CallsOf("Create").Should().Be(1, "never delete-and-recreate");
    }

    [Fact]
    public async Task A_setting_removed_after_the_payload_was_accepted_blocks_it_at_execution_without_a_call()
    {
        await LiveAllActive();
        await _h.Send(TestPayloads.Item());
        await _h.UpdateSettingsAsync(s => s.DefaultIncomeAccountId = null);

        await _h.DrainAsync();

        var map = await _h.MapAsync(SyncKind.Item, "I-1");
        map.State.Should().Be(SyncState.Blocked);
        map.LastErrorCode.Should().Be("SETTINGS_ACCOUNT_MISSING");
        (await _h.EntriesAsync(SyncKind.Item, "I-1")).Single().Status.Should().Be(OutboxStatus.Blocked);
        _h.Provider.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task QuickBooks_tax_that_differs_from_the_invoice_is_flagged_as_a_warning()
    {
        await _h.ConnectAsync();
        await SeedMappedAsync(SyncKind.Customer, "C-1", "Acme Traders");
        await SeedMappedAsync(SyncKind.Item, "I-1", "Widget");
        _h.Provider.InvoiceTax = _ => 40m;               // SCM says 34.00

        await _h.Send(TestPayloads.Invoice());
        await _h.DrainAsync();

        var map = await _h.MapAsync(SyncKind.SalesInvoice, "SI-1");
        map.State.Should().Be(SyncState.Synced);
        map.Warning.Should().Contain("40.00").And.Contain("34.00");

        _h.Provider.InvoiceTax = _ => 34.01m;
        var again = TestPayloads.Invoice();
        again.CustomerMemo = "thanks";
        await _h.Send(again);
        await _h.DrainAsync();
        (await _h.MapAsync(SyncKind.SalesInvoice, "SI-1")).Warning.Should().BeNull("within 0.01");
    }

    // ── Connection lost ──────────────────────────────────────────────────────

    [Fact]
    public async Task Auth_revoked_marks_the_connection_suspends_its_outbox_and_resume_sends_everything_once()
    {
        var connection = await LiveAllActive();
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.AuthRevoked, Code: "3200", Message: "AuthenticationFailed"));
        await _h.Send(TestPayloads.Customer());
        await _h.Send(TestPayloads.Customer(id: "C-2", name: "Second"));
        await _h.Send(TestPayloads.Customer(id: "C-3", name: "Third"));

        (await _h.RunOutboxAsync()).Should().Be(1, "a revoked connection stops at once");

        _h.Health.Calls.Should().ContainSingle(c => c.ConnectionId == connection.Id && c.Status == ConnectionStatus.Revoked);
        await using (var db = _h.SuperDb())
        {
            (await db.Outbox.Select(e => e.Status).ToListAsync()).Should().AllBeEquivalentTo(OutboxStatus.Suspended);
            (await db.Connections.SingleAsync()).Status.Should().Be(ConnectionStatus.Revoked);
            (await db.CommandClaims.SingleAsync()).Status.Should().Be(ClaimStatus.Unknown);
        }
        (await _h.MapAsync(SyncKind.Customer, "C-1")).LastErrorCode.Should().Be("CONNECTION_REVOKED");

        (await _h.RunOutboxAsync()).Should().Be(0, "nothing runs on a revoked connection");

        // Reconnected (by the connection work package) and resumed.
        await using (var db = _h.SuperDb())
        {
            (await db.Connections.SingleAsync()).Status = ConnectionStatus.Live;
            await db.SaveChangesAsync();
        }
        var resumed = await _h.AsJob(sp => sp.GetRequiredService<IOutboxControl>().ResumeAllAsync(connection.Id));
        resumed.Should().Be(3);

        await _h.DrainAsync();
        _h.Provider.Of(SyncKind.Customer).Should().HaveCount(3);
        (await StateOf(SyncKind.Customer, "C-1")).Should().Be(SyncState.Synced);
    }

    [Fact]
    public async Task Suspend_and_resume_touch_only_that_connection()
    {
        var orgB = Guid.NewGuid();
        var a = await LiveAllActive();
        var b = await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive, organizationId: orgB, realmId: "REALM-B");
        await _h.Send(TestPayloads.Customer());
        _h.Tenant.RequestOrganizationId = orgB;
        await _h.Send(TestPayloads.Customer());
        _h.Tenant.RequestOrganizationId = _h.OrgId;

        // As from the anonymous OAuth callback: no tenant at all.
        var suspended = await _h.AsJob(sp => sp.GetRequiredService<IOutboxControl>().SuspendAllAsync(a.Id, "revoked"));

        suspended.Should().Be(1);
        await using var db = _h.SuperDb();
        (await db.Outbox.SingleAsync(e => e.ConnectionId == a.Id)).Status.Should().Be(OutboxStatus.Suspended);
        (await db.Outbox.SingleAsync(e => e.ConnectionId == b.Id)).Status.Should().Be(OutboxStatus.Queued);
    }

    [Fact]
    public async Task An_entry_whose_connection_is_no_longer_usable_is_suspended_not_sent()
    {
        await LiveAllActive();
        await _h.Send(TestPayloads.Customer());
        var entry = await _h.OpenEntryAsync(SyncKind.Customer, "C-1");
        await using (var db = _h.SuperDb())
        {
            (await db.Connections.SingleAsync()).Status = ConnectionStatus.Expired;
            await db.SaveChangesAsync();
        }

        var result = await _h.ExecuteAsync(entry.Id);

        result.Status.Should().Be(SyncExecutionStatus.Suspended);
        result.StopConnection.Should().BeTrue();
        (await _h.EntriesAsync(SyncKind.Customer, "C-1")).Single().Status.Should().Be(OutboxStatus.Suspended);
        _h.Provider.Calls.Should().BeEmpty();
    }

    // ── Dry run ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dry_run_builds_validates_and_logs_but_makes_no_provider_call_at_all()
    {
        await _h.ConnectAsync(s => { s.Mode = SyncMode.DryRun; s.PartnerScope = PartnerScope.AllActive; });
        await _h.Send(TestPayloads.Customer());
        await _h.Send(TestPayloads.Item());
        (await _h.Send(TestPayloads.Invoice())).Outcome.Should().Be(GatewayOutcome.WaitingOnDependency);

        await _h.DrainAsync();

        _h.Provider.Calls.Should().BeEmpty();
        (await StateOf(SyncKind.Customer, "C-1")).Should().Be(SyncState.DryRunOk);
        (await StateOf(SyncKind.Item, "I-1")).Should().Be(SyncState.DryRunOk);
        (await StateOf(SyncKind.SalesInvoice, "SI-1")).Should().Be(SyncState.DryRunOk, "dry-run dependencies count as present in dry-run mode");

        var customer = await _h.MapAsync(SyncKind.Customer, "C-1");
        customer.RemoteId.Should().BeNull();
        customer.LastPushedFingerprint.Should().BeNull("nothing was pushed");
        var log = (await _h.LogAsync(customer.Id)).Single();
        log.Outcome.Should().Be("DryRun");
        log.Operation.Should().Be("Create");
        log.RequestJson.Should().Contain("Acme Traders");

        var invoiceLog = (await _h.LogAsync((await _h.MapAsync(SyncKind.SalesInvoice, "SI-1")).Id)).Single();
        invoiceLog.RequestJson.Should().Contain(QboObjectBuilder.DryRunRefPrefix + "Customer:C-1");

        // Re-sending an unchanged payload in dry run is a no-op.
        (await _h.Send(TestPayloads.Customer())).State.Should().Be(SyncState.DryRunOk);
    }

    [Fact]
    public async Task Switching_dry_run_to_live_sends_what_the_dry_run_checked()
    {
        await _h.ConnectAsync(s => { s.Mode = SyncMode.DryRun; s.PartnerScope = PartnerScope.AllActive; });
        await _h.Send(TestPayloads.Customer());
        await _h.Send(TestPayloads.Item());
        await _h.Send(TestPayloads.Invoice());
        await _h.DrainAsync();
        _h.Provider.Calls.Should().BeEmpty();

        await _h.UpdateSettingsAsync(s => s.Mode = SyncMode.Live);
        await _h.DrainAsync();

        (await StateOf(SyncKind.SalesInvoice, "SI-1")).Should().Be(SyncState.Synced);
        var invoice = (RemoteInvoice)_h.Provider.Calls.Single(c => c.Kind == SyncKind.SalesInvoice).Entity!;
        invoice.CustomerId.Should().Be((await _h.MapAsync(SyncKind.Customer, "C-1")).RemoteId);
        _h.Provider.Company.Should().HaveCount(3);
    }

    // ── Void ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_after_push_voids_it_in_QuickBooks()
    {
        await _h.ConnectAsync();
        await SeedMappedAsync(SyncKind.Customer, "C-1", "Acme Traders");
        await SeedMappedAsync(SyncKind.Item, "I-1", "Widget");
        await _h.Send(TestPayloads.Invoice());
        await _h.DrainAsync();
        var remoteId = (await _h.MapAsync(SyncKind.SalesInvoice, "SI-1")).RemoteId!;

        (await _h.Gateway(g => g.VoidSalesInvoiceAsync("SI-1"))).State.Should().Be(SyncState.Pending);
        await _h.DrainAsync();

        _h.Provider.Calls.Should().Contain(c => c.Operation == "Void" && c.RemoteId == remoteId);
        _h.Provider.Company.Single(r => r.Id == remoteId).Voided.Should().BeTrue();
        (await StateOf(SyncKind.SalesInvoice, "SI-1")).Should().Be(SyncState.Voided);
    }

    [Fact]
    public async Task Void_of_an_invoice_whose_create_is_in_doubt_looks_it_up_and_voids_it()
    {
        await _h.ConnectAsync();
        await SeedMappedAsync(SyncKind.Customer, "C-1", "Acme Traders");
        await SeedMappedAsync(SyncKind.Item, "I-1", "Widget");
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Transient, ApplyAnyway: true));
        await _h.Send(TestPayloads.Invoice());
        await _h.RunOutboxAsync();

        (await _h.Gateway(g => g.VoidSalesInvoiceAsync("SI-1"))).State.Should().Be(SyncState.Pending, "it may exist in QuickBooks");
        await _h.DrainAsync();

        _h.Provider.Of(SyncKind.SalesInvoice).Single().Voided.Should().BeTrue();
        (await StateOf(SyncKind.SalesInvoice, "SI-1")).Should().Be(SyncState.Voided);
    }

    // ── Robustness ───────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unexpected_exception_is_retried_with_backoff_and_the_claim_left_unknown()
    {
        await LiveAllActive();
        _h.Provider.ThrowForRealms.Add(SyncHarness.Realm);
        await _h.Send(TestPayloads.Customer());

        await _h.RunOutboxAsync();

        var entry = await _h.OpenEntryAsync(SyncKind.Customer, "C-1");
        entry.Status.Should().Be(OutboxStatus.Queued);
        entry.AttemptCount.Should().Be(1);
        entry.NextAttemptAt.Should().Be(_h.Now.AddMinutes(1));
        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.LastErrorCode.Should().Be("UNEXPECTED_ERROR");
        (await _h.ClaimsAsync(map.Id)).Single().Status.Should().Be(ClaimStatus.Unknown);
        (await _h.LogAsync(map.Id)).Should().Contain(l => l.Outcome == "Exception");
    }

    [Fact]
    public async Task Only_queued_entries_run_and_a_missing_one_is_reported()
    {
        await LiveAllActive();
        await _h.Send(TestPayloads.Customer());
        var entry = await _h.OpenEntryAsync(SyncKind.Customer, "C-1");
        await _h.ExecuteAsync(entry.Id);

        (await _h.ExecuteAsync(entry.Id)).Status.Should().Be(SyncExecutionStatus.Skipped);
        (await _h.ExecuteAsync(987654)).Status.Should().Be(SyncExecutionStatus.NotFound);
        _h.Provider.CallsOf("Create").Should().Be(1);
    }

    [Fact]
    public async Task A_follow_up_entry_waits_while_another_push_of_the_same_record_is_running()
    {
        await LiveAllActive();
        await _h.Send(TestPayloads.Customer());
        await using (var db = _h.SuperDb())
        {
            (await db.Outbox.SingleAsync()).Status = OutboxStatus.Running;
            await db.SaveChangesAsync();
        }

        var changed = TestPayloads.Customer();
        changed.Notes = "while running";
        await _h.Send(changed);

        var entries = await _h.EntriesAsync(SyncKind.Customer, "C-1");
        entries.Should().HaveCount(2, "the running entry is never rewritten; a follow-up is queued");
        var followUp = entries.Single(e => e.Status == OutboxStatus.Queued);

        _h.Clock.Advance(TimeSpan.FromSeconds(5));
        (await _h.ExecuteAsync(followUp.Id)).Status.Should().Be(SyncExecutionStatus.InFlight);
        _h.Provider.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Secrets_in_provider_request_bodies_never_reach_the_sync_log()
    {
        await LiveAllActive();
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Refused, Message: "Bearer abc.def.ghi rejected"));
        await _h.Send(TestPayloads.Customer());
        await _h.DrainAsync();

        var log = (await _h.LogAsync((await _h.MapAsync(SyncKind.Customer, "C-1")).Id)).Single();
        log.RequestJson.Should().NotContain("SECRET-TOKEN");
        log.Message.Should().NotContain("abc.def.ghi");
        (await _h.MapAsync(SyncKind.Customer, "C-1")).LastError.Should().NotContain("abc.def.ghi");
    }
}
