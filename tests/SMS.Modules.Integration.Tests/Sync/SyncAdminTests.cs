using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Exceptions;
using SMS.Shared.Integration.QuickBooks;
using Script = SMS.Modules.Integration.Tests.Fakes.ScriptedAccountingProvider.Script;

namespace SMS.Modules.Integration.Tests.Sync;

/// <summary>The sync dashboard service behind SyncController: summary, items, log, retry, resolve, push, backfill, status.</summary>
public class SyncAdminTests : IAsyncLifetime
{
    private readonly SyncHarness _h = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<T> Admin<T>(Func<IQuickBooksSyncAdminService, Task<T>> call) =>
        _h.Scoped(sp => call(sp.GetRequiredService<IQuickBooksSyncAdminService>()));

    [Fact]
    public async Task Summary_without_a_connection_is_empty_but_well_formed()
    {
        var summary = await Admin(s => s.GetSummaryAsync());

        summary.ConnectionStatus.Should().Be("NotConnected");
        summary.Kinds.Select(k => k.Kind).Should().Equal("Customer", "Vendor", "Item", "SalesInvoice", "Bill");
        summary.Kinds.Should().OnlyContain(k => k.Total == 0 && k.CountsByState.Count == Enum.GetValues<SyncState>().Length);
    }

    [Fact]
    public async Task Summary_counts_records_by_kind_and_state_and_the_queue()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await _h.Send(TestPayloads.Customer());
        await _h.Send(TestPayloads.Customer(id: "C-2", name: "Bad: Name"));
        await _h.Send(TestPayloads.Item());
        await _h.RunOutboxAsync();
        await _h.Send(TestPayloads.Vendor());

        var summary = await Admin(s => s.GetSummaryAsync());

        summary.ConnectionStatus.Should().Be("Live");
        summary.Mode.Should().Be("Live");
        summary.QueueDepth.Should().Be(1, "only the vendor is still queued");
        summary.LastRunAt.Should().Be(_h.Now);
        var customers = summary.Kinds.Single(k => k.Kind == "Customer");
        customers.Total.Should().Be(2);
        customers.CountsByState["Synced"].Should().Be(1);
        customers.CountsByState["Blocked"].Should().Be(1);
        summary.Kinds.Single(k => k.Kind == "Vendor").CountsByState["Pending"].Should().Be(1);
    }

    [Fact]
    public async Task Items_filter_search_page_and_carry_attempts_and_deep_links()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        for (var i = 1; i <= 5; i++) await _h.Send(TestPayloads.Customer(id: $"C-{i}", name: $"Customer {i}"));
        await _h.Send(TestPayloads.Item());
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Transient));
        await _h.RunOutboxAsync();          // C-1 transient, the rest synced

        var page = await Admin(s => s.GetItemsAsync(new SyncItemQuery { Kind = "customer", Page = 1, PageSize = 2 }));
        page.TotalRecords.Should().Be(5);
        page.TotalPages.Should().Be(3);
        page.Data.Should().HaveCount(2);

        var synced = await Admin(s => s.GetItemsAsync(new SyncItemQuery { State = "Synced", PageSize = 50 }));
        synced.Data.Should().HaveCount(5);
        synced.Data.Should().OnlyContain(i => i.DeepLink != null && i.DeepLink.Contains(i.RemoteId!));

        var search = await Admin(s => s.GetItemsAsync(new SyncItemQuery { Search = "Customer 1" }));
        var retrying = search.Data.Single();
        retrying.ExternalId.Should().Be("C-1");
        retrying.State.Should().Be("Pending");
        retrying.AttemptCount.Should().Be(1);
        retrying.LastErrorCode.Should().Be("TRANSIENT");

        (await Admin(s => s.GetItemsAsync(new SyncItemQuery { Search = "C-3" }))).Data.Should().ContainSingle();

        var act = () => Admin(s => s.GetItemsAsync(new SyncItemQuery { State = "Bogus" }));
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task The_log_is_newest_first_and_an_unknown_item_is_404()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Transient));
        await _h.Send(TestPayloads.Customer());
        await _h.RunOutboxAsync();
        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        await _h.RunOutboxAsync();
        var map = await _h.MapAsync(SyncKind.Customer, "C-1");

        var log = await Admin(s => s.GetLogAsync(map.Uuid));

        log.Select(l => l.Operation).Should().Equal("Create", "Find", "Create");
        log[0].Outcome.Should().Be("Succeeded");
        log[^1].Outcome.Should().Be("Transient");

        var act = () => Admin(s => s.GetLogAsync(Guid.NewGuid()));
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Retry_forgets_the_refusal_and_sends_it_now()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Refused, Message: "Account is inactive"));
        await _h.Send(TestPayloads.Customer());
        await _h.DrainAsync();
        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.State.Should().Be(SyncState.Failed);

        // The accountant fixed the cause in QuickBooks; the same data should now go.
        var item = await Admin(s => s.RetryAsync(map.Uuid));

        item.State.Should().Be("Synced");
        item.RemoteId.Should().NotBeNull();
        _h.Provider.CallsOf("Create").Should().Be(2);
    }

    [Fact]
    public async Task Retry_needs_a_payload_and_a_usable_connection()
    {
        var connection = await _h.ConnectAsync();
        await _h.Send(TestPayloads.Invoice());                    // creates payload-less placeholders
        var placeholder = await _h.MapAsync(SyncKind.Customer, "C-1");

        var noPayload = () => Admin(s => s.RetryAsync(placeholder.Uuid));
        await noPayload.Should().ThrowAsync<BadRequestException>().WithMessage("*Push now*");

        await using (var db = _h.SuperDb())
        {
            (await db.Connections.SingleAsync()).Status = ConnectionStatus.Revoked;
            await db.SaveChangesAsync();
        }
        var invoice = await _h.MapAsync(SyncKind.SalesInvoice, "SI-1");
        var revoked = () => Admin(s => s.RetryAsync(invoice.Uuid));
        await revoked.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Resolve_by_linking_adopts_the_record_without_overwriting_it_and_releases_waiting_documents()
    {
        await _h.ConnectAsync();
        _h.Provider.Seed(SyncKind.Item, "Widget");
        await _h.Send(TestPayloads.Customer());
        await _h.Send(TestPayloads.Invoice());                    // waits on C-1 (queued) and I-1 (placeholder)
        _h.Provider.Enqueue("Create", Enumerable.Repeat(new Script(ProviderOutcomeKind.Transient), 8).ToArray());
        _h.Jobs.MaxAttempts = 1;
        await _h.RunOutboxAsync();                                 // C-1: gives up → NeedsResolution
        var customer = await _h.MapAsync(SyncKind.Customer, "C-1");
        customer.State.Should().Be(SyncState.NeedsResolution);

        var accountants = _h.Provider.Seed(SyncKind.Customer, "Acme Traders Ltd");
        var item = await Admin(s => s.ResolveAsync(customer.Uuid, new ResolveSyncItemRequest { Action = "LinkRemote", RemoteId = accountants.Id }, userId: 42));

        item.State.Should().Be("Synced");
        item.RemoteId.Should().Be(accountants.Id);
        var linked = await _h.MapAsync(SyncKind.Customer, "C-1");
        linked.LinkOrigin.Should().Be(LinkOrigin.Adopted);
        linked.LinkedByUserId.Should().Be(42);
        linked.LastPushedFingerprint.Should().Be(linked.PayloadFingerprint);

        // The invoice still waits on the item; link that too and it is released.
        await using (var db = _h.SuperDb())
        {
            var i = await db.EntityMaps.SingleAsync(m => m.Kind == SyncKind.Item);
            i.PayloadJson = "{}";
            await db.SaveChangesAsync();
        }
        var itemMap = await _h.MapAsync(SyncKind.Item, "I-1");
        await Admin(s => s.ResolveAsync(itemMap.Uuid, new ResolveSyncItemRequest { Action = "linkremote", RemoteId = _h.Provider.Of(SyncKind.Item).Single().Id }, 42));

        (await _h.OpenEntryAsync(SyncKind.SalesInvoice, "SI-1")).Status.Should().Be(OutboxStatus.Queued);
    }

    [Fact]
    public async Task Resolve_refuses_a_remote_already_linked_elsewhere_and_unknown_actions()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await _h.Send(TestPayloads.Customer());
        await _h.Send(TestPayloads.Customer(id: "C-2", name: "Second"));
        await _h.DrainAsync();
        var first = await _h.MapAsync(SyncKind.Customer, "C-1");
        var second = await _h.MapAsync(SyncKind.Customer, "C-2");

        var taken = () => Admin(s => s.ResolveAsync(second.Uuid, new ResolveSyncItemRequest { Action = "LinkRemote", RemoteId = first.RemoteId }, 1));
        await taken.Should().ThrowAsync<BadRequestException>().WithMessage("*already linked*");

        var bogus = () => Admin(s => s.ResolveAsync(second.Uuid, new ResolveSyncItemRequest { Action = "Delete" }, 1));
        await bogus.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Mark_resolved_and_requeue()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Refused));
        await _h.Send(TestPayloads.Customer());
        await _h.DrainAsync();
        var map = await _h.MapAsync(SyncKind.Customer, "C-1");

        var requeued = await Admin(s => s.ResolveAsync(map.Uuid, new ResolveSyncItemRequest { Action = "Requeue" }, 1));
        requeued.State.Should().Be("Pending");
        (await _h.OpenEntryAsync(SyncKind.Customer, "C-1")).Status.Should().Be(OutboxStatus.Queued);
        (await _h.ClaimsAsync(map.Id)).Should().BeEmpty("the refusal is forgotten so the requeue really sends");

        var resolved = await Admin(s => s.ResolveAsync(map.Uuid, new ResolveSyncItemRequest { Action = "MarkResolved" }, 1));
        resolved.State.Should().Be("NotSynced");
        resolved.LastError.Should().BeNull();
        (await _h.EntriesAsync(SyncKind.Customer, "C-1")).Should().OnlyContain(e => e.Status == OutboxStatus.Done || e.Status == OutboxStatus.Failed);
    }

    [Fact]
    public async Task Push_now_marks_records_wanted_queues_stored_payloads_and_asks_the_source()
    {
        await _h.ConnectAsync();                                   // only when referenced
        await _h.Send(TestPayloads.Customer());                    // stored, NotSynced
        _h.Source.Has(TestPayloads.Customer(id: "C-2", name: "From SCM"));

        var result = await Admin(s => s.PushAsync(new ManualPushRequest { Kind = "Customer", ExternalIds = ["C-1", "C-2", "C-2", " "] }));

        result.Requested.Should().Be(2);
        _h.Source.PushCalls.Should().ContainSingle(c => c.Kind == SyncKind.Customer && c.Ids.SequenceEqual(new[] { "C-1", "C-2" }));
        (await _h.MapAsync(SyncKind.Customer, "C-1")).State.Should().Be(SyncState.Pending);
        var fromSource = await _h.MapAsync(SyncKind.Customer, "C-2");
        fromSource.RequestedAt.Should().Be(_h.Now);
        fromSource.State.Should().Be(SyncState.Pending, "requested, so in scope when the source sent it");

        var bad = () => Admin(s => s.PushAsync(new ManualPushRequest { Kind = "Invoice", ExternalIds = ["x"] }));
        await bad.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Backfill_asks_the_source_for_everything_of_the_kind()
    {
        await _h.ConnectAsync();
        _h.Source.Has(TestPayloads.Item());
        _h.Source.Has(TestPayloads.Item(id: "I-2", name: "Gadget", sku: "G-1"));

        var result = await Admin(s => s.BackfillAsync(SyncKind.Item));

        result.Kind.Should().Be("Item");
        result.Sent.Should().Be(2);
        _h.Source.PushAllCalls.Should().ContainSingle(c => c.Kind == SyncKind.Item && c.Since == null);
    }

    [Fact]
    public async Task Status_lookup_goes_through_the_gateway()
    {
        await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await _h.Send(TestPayloads.Customer());

        var result = await Admin(s => s.LookupStatusAsync(new StatusLookupRequest { Kind = "customer", ExternalIds = ["C-1", "C-9"] }));

        result.Items.Should().ContainSingle(i => i.ExternalId == "C-1" && i.State == SyncState.Pending);
    }

    [Theory]
    [InlineData("customers", SyncKind.Customer)]
    [InlineData("Customer", SyncKind.Customer)]
    [InlineData("SALESINVOICE", SyncKind.SalesInvoice)]
    [InlineData("sales-invoices", SyncKind.SalesInvoice)]
    [InlineData("bills", SyncKind.Bill)]
    public void Kind_names_parse_in_route_and_enum_forms(string text, SyncKind expected)
    {
        SyncKindNames.TryParse(text, out var kind).Should().BeTrue();
        kind.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("invoices")]
    public void Unknown_kind_names_do_not_parse(string text) => SyncKindNames.TryParse(text, out _).Should().BeFalse();
}
