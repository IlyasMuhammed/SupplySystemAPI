using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Exceptions;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Gateway;

/// <summary>
/// S-7: a supplier invoice reversed in SCM after its bill reached QuickBooks is not voided there automatically (out
/// of scope) — it is "flagged for the accountant". Found by the end-to-end tester: the flag was only a server log
/// line, so the bill showed as a normal Synced bill on the dashboard. <see cref="IQuickBooksGateway.FlagForAccountantAsync"/>
/// puts it where the accountant works: NeedsResolution with the reason, nothing more sent, until resolved.
/// </summary>
public sealed class FlagForAccountantTests : IAsyncLifetime
{
    private const string Reason = "Supplier invoice INV-2026-00017 was reversed in SCM on 01 Oct 2026 (duplicate). Void bill SUP-INV-1 in QuickBooks by hand, then mark this resolved.";

    private readonly SyncHarness _h = new();

    public async Task InitializeAsync()
    {
        await _h.ConnectAsync();
        _h.Source.Has(TestPayloads.Vendor());
        _h.Source.Has(TestPayloads.Item());
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<GatewayResult> Flag(string id = "B-1") => _h.Gateway(g => g.FlagForAccountantAsync(SyncKind.Bill, id, Reason));

    private Task<T> Admin<T>(Func<IQuickBooksSyncAdminService, Task<T>> call) =>
        _h.Scoped(sp => call(sp.GetRequiredService<IQuickBooksSyncAdminService>()));

    [Fact]
    public async Task A_bill_in_QuickBooks_is_flagged_on_the_dashboard_and_nothing_more_is_sent_until_resolved()
    {
        await _h.Send(TestPayloads.Bill());
        await _h.DrainAsync();
        (await _h.MapAsync(SyncKind.Bill, "B-1")).State.Should().Be(SyncState.Synced);
        var writes = _h.Provider.WriteCalls;

        (await Flag()).State.Should().Be(SyncState.NeedsResolution);

        var item = (await Admin(s => s.GetItemsAsync(new SyncItemQuery { Kind = "Bill" }))).Data.Single();
        item.State.Should().Be("NeedsResolution");
        item.LastErrorCode.Should().Be(QuickBooksGateway.AccountantActionCode);
        item.LastError.Should().Be(Reason);

        // A later payload (a reconciliation re-push, an edit) is not sent over the accountant's head…
        var changed = TestPayloads.Bill();
        changed.PrivateNote = "edited after reversal";
        (await _h.Send(changed)).State.Should().Be(SyncState.NeedsResolution);
        await _h.DrainAsync();
        _h.Provider.WriteCalls.Should().Be(writes, "nothing is written to QuickBooks");

        // …nor by Retry.
        await FluentActions.Awaiting(() => Admin(s => s.RetryAsync(item.Id))).Should().ThrowAsync<BadRequestException>()
            .WithMessage("*flagged for the accountant*Mark resolved*");

        // Done in QuickBooks: the accountant resolves it.
        var resolved = await Admin(s => s.ResolveAsync(item.Id, new ResolveSyncItemRequest { Action = "MarkResolved" }, 7));
        resolved.State.Should().Be("Synced");
        resolved.LastErrorCode.Should().BeNull();
        _h.Provider.WriteCalls.Should().Be(writes);
    }

    [Fact]
    public async Task A_bill_that_never_reached_QuickBooks_is_closed_and_its_queued_send_dropped()
    {
        await _h.Send(TestPayloads.Bill());      // queued, waiting for its vendor and item
        (await _h.MapAsync(SyncKind.Bill, "B-1")).State.Should().Be(SyncState.WaitingOnDependency);

        (await Flag()).State.Should().Be(SyncState.Voided);
        await _h.DrainAsync();

        (await _h.MapAsync(SyncKind.Bill, "B-1")).State.Should().Be(SyncState.Voided);
        _h.Provider.Calls.Should().NotContain(c => c.Kind == SyncKind.Bill);
        (await _h.EntriesAsync(SyncKind.Bill, "B-1")).Should().OnlyContain(e => e.Status == OutboxStatus.Done);
    }

    [Fact]
    public async Task A_record_the_gateway_never_saw_is_left_alone()
    {
        (await Flag("NEVER-SEEN")).State.Should().Be(SyncState.NotSynced);
        (await _h.FindMapAsync(SyncKind.Bill, "NEVER-SEEN")).Should().BeNull();
    }

    [Fact]
    public async Task Without_the_Integration_module_the_call_does_nothing()
    {
        IQuickBooksGateway none = new NullQuickBooksGateway();

        (await none.FlagForAccountantAsync(SyncKind.Bill, "B-1", Reason)).Outcome.Should().Be(GatewayOutcome.Disabled);
    }
}
