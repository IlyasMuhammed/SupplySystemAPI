using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Exceptions;
using SMS.Shared.Integration.QuickBooks;
using Script = SMS.Modules.Integration.Tests.Fakes.ScriptedAccountingProvider.Script;

namespace SMS.Modules.Integration.Tests.PostingControl;

/// <summary>A44 D-34 QuickBooks logs page (every call across all records) and "Send to QuickBooks" for a record never seen.</summary>
public class SyncLogPageTests : IAsyncLifetime
{
    private readonly SyncHarness _h = PostingControlKit.Harness();

    public async Task InitializeAsync() => await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<T> Admin<T>(Func<IQuickBooksSyncAdminService, Task<T>> call) =>
        _h.Scoped(sp => call(sp.GetRequiredService<IQuickBooksSyncAdminService>()));

    private Task<SMS.Shared.Pagination.PaginatedResponse<SyncLogItemModel>> LogAsync(SyncLogQuery query) =>
        Admin(a => a.GetCallLogAsync(query));

    private async Task RefusedCustomerAndSyncedItemAsync()
    {
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Refused, Code: "6240", Message: "Duplicate Name Exists Error"));
        await _h.Send(TestPayloads.Customer());
        await _h.DrainAsync();
        await _h.Send(TestPayloads.Item());
        await _h.DrainAsync();
    }

    [Fact]
    public async Task By_default_only_failed_calls_are_listed_newest_first_with_their_record()
    {
        await RefusedCustomerAndSyncedItemAsync();

        var failed = await LogAsync(new SyncLogQuery());

        failed.Data.Should().NotBeEmpty().And.OnlyContain(i => i.Outcome != "Succeeded" && i.Outcome != "DryRun");
        var row = failed.Data.First(i => i.ErrorCode == "6240");
        row.Should().Match<SyncLogItemModel>(i => i.Kind == "Customer" && i.ExternalId == "C-1" && i.DisplayLabel == "Acme Traders"
                                               && i.State == "Failed" && i.ItemId != null && i.DeepLink == null);
        row.RequestJson.Should().BeNull("bodies only in the detail");

        var all = await LogAsync(new SyncLogQuery { FailedOnly = false, UnresolvedOnly = false });
        all.TotalRecords.Should().BeGreaterThan(failed.TotalRecords);
        all.Data.Should().BeInDescendingOrder(i => i.CreatedAt);
        var item = all.Data.First(i => i.Kind == "Item" && i.Outcome == "Succeeded");
        item.RemoteId.Should().NotBeNull();
        item.DeepLink.Should().NotBeNull("synced records open in QuickBooks");
    }

    [Fact]
    public async Task Filters_narrow_by_kind_operation_outcome_dates_and_search()
    {
        await RefusedCustomerAndSyncedItemAsync();
        var all = new SyncLogQuery { FailedOnly = false, UnresolvedOnly = false };

        (await LogAsync(new SyncLogQuery { FailedOnly = false, UnresolvedOnly = false, Kind = "items" })).Data.Should().OnlyContain(i => i.Kind == "Item");
        (await LogAsync(new SyncLogQuery { FailedOnly = false, UnresolvedOnly = false, Outcome = "Refused" })).Data.Should().OnlyContain(i => i.Outcome == "Refused").And.NotBeEmpty();
        (await LogAsync(new SyncLogQuery { FailedOnly = false, UnresolvedOnly = false, Operation = "Create" })).Data.Should().OnlyContain(i => i.Operation == "Create");
        (await LogAsync(new SyncLogQuery { Search = "Duplicate Name" })).Data.Should().ContainSingle(i => i.ErrorCode == "6240");
        (await LogAsync(new SyncLogQuery { FailedOnly = false, UnresolvedOnly = false, Search = "Widget" })).Data.Should().OnlyContain(i => i.Kind == "Item");
        (await LogAsync(new SyncLogQuery { FailedOnly = false, UnresolvedOnly = false, To = _h.Now.Date.AddDays(-1) })).TotalRecords.Should().Be(0);
        (await LogAsync(new SyncLogQuery { FailedOnly = false, UnresolvedOnly = false, From = _h.Now.Date, To = _h.Now.Date })).TotalRecords
            .Should().Be((await LogAsync(all)).TotalRecords, "a date-only To is the whole day");
        (await LogAsync(new SyncLogQuery { FailedOnly = false, UnresolvedOnly = false, PageSize = 1000 })).PageSize.Should().Be(200);

        var unknown = async () => await LogAsync(new SyncLogQuery { Kind = "Cheese" });
        await unknown.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task The_detail_has_the_stored_bodies_and_another_organizations_entry_is_not_found()
    {
        await RefusedCustomerAndSyncedItemAsync();
        var row = (await LogAsync(new SyncLogQuery())).Data.First(i => i.ErrorCode == "6240");

        var detail = await Admin(a => a.GetCallLogEntryAsync(row.Id));
        detail.RequestJson.Should().NotBeNullOrEmpty();
        detail.Kind.Should().Be("Customer");

        // Another organization with its own company and a failed call.
        var other = Guid.NewGuid();
        await _h.ConnectAsync(organizationId: other, realmId: "9130000000000002");
        await using (var db = _h.DbAs(other))
        {
            var connection = db.Connections.Single();
            db.SyncLog.Add(new SyncLogEntry { ConnectionId = connection.Id, Operation = "Create", Outcome = "Refused", ErrorCode = "OTHER" });
            await db.SaveChangesAsync();
        }

        (await LogAsync(new SyncLogQuery { Search = "OTHER", UnresolvedOnly = false })).TotalRecords.Should().Be(0);
        await using var all = _h.SuperDb();
        var foreign = all.SyncLog.Single(l => l.ErrorCode == "OTHER").Uuid;
        var act = async () => await Admin(a => a.GetCallLogEntryAsync(foreign));
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task By_default_a_failure_whose_record_has_since_synced_or_that_belongs_to_no_record_is_left_out()
    {
        await RefusedCustomerAndSyncedItemAsync();
        // A failed matching scan: a call with no record.
        await using (var db = _h.SuperDb())
        {
            db.SyncLog.Add(new SyncLogEntry { OrganizationId = _h.OrgId, ConnectionId = db.Connections.Single().Id, Operation = "Query", Outcome = "Transient", ErrorCode = "SCAN" });
            await db.SaveChangesAsync();
        }

        (await LogAsync(new SyncLogQuery())).Data.Should().Contain(i => i.ErrorCode == "6240")
            .And.NotContain(i => i.ErrorCode == "SCAN", "no record: left out by default");
        (await LogAsync(new SyncLogQuery { UnresolvedOnly = false })).Data.Should().Contain(i => i.ErrorCode == "SCAN");

        // The customer is fixed and sent: its old refusal is resolved.
        await _h.Scoped(async sp =>
        {
            var map = await _h.MapAsync(SyncKind.Customer, "C-1");
            await sp.GetRequiredService<IQuickBooksSyncAdminService>().RetryAsync(map.Uuid);
        });
        (await _h.MapAsync(SyncKind.Customer, "C-1")).State.Should().Be(SyncState.Synced);

        (await LogAsync(new SyncLogQuery())).Data.Should().NotContain(i => i.ErrorCode == "6240");
        var history = await LogAsync(new SyncLogQuery { UnresolvedOnly = false });
        history.Data.Should().ContainSingle(i => i.ErrorCode == "6240").Which.State.Should().Be("Synced", "the item shows the state now");
    }

    [Fact]
    public async Task The_log_query_and_its_filters_translate_on_SQL_Server()
    {
        await using var sql = PostingControlKit.Harness(sqlConnectionString: SyncHarness.LocalDb());
        await sql.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
        sql.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Refused, Code: "6240", Message: "Duplicate Name Exists Error"));
        await sql.Send(TestPayloads.Customer());
        await sql.DrainAsync();

        var page = await sql.Scoped(sp => sp.GetRequiredService<IQuickBooksSyncAdminService>().GetCallLogAsync(new SyncLogQuery
        {
            Kind = "Customer", Search = "Acme", From = sql.Now.AddDays(-1), To = sql.Now.Date, Outcome = "Refused"
        }));

        page.Data.Should().ContainSingle(i => i.ErrorCode == "6240" && i.ExternalId == "C-1");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Send_to_QuickBooks_works_for_a_record_the_gateway_has_never_seen(SyncKind kind)
    {
        if (!_h.Source.Kinds.Contains(kind)) _h.Source.Kinds.Add(kind);

        var result = await Admin(a => a.PushAsync(new ManualPushRequest { Kind = kind.ToString(), ExternalIds = ["NEW-1"] }));

        _h.Source.PushCalls.Should().ContainSingle(c => c.Kind == kind).Which.Ids.Should().Equal(["NEW-1"], "SCM is always asked");
        if (SyncPayloads.IsMasterData(kind))
        {
            // Master data keeps its placeholder: documents' dependencies rely on it.
            result.Requested.Should().Be(1);
            result.NotFound.Should().BeEmpty();
            (await _h.MapAsync(kind, "NEW-1")).RequestedAt.Should().NotBeNull("explicitly wanted, whatever the partner scope");
        }
        else
        {
            // REV-20: SCM sent nothing for it — no empty document map, and the screen is told.
            result.Requested.Should().Be(0);
            result.NotFound.Should().Equal("NEW-1");
            (await _h.FindMapAsync(kind, "NEW-1")).Should().BeNull();
        }
    }

    public static TheoryData<SyncKind> AllKinds()
    {
        var data = new TheoryData<SyncKind>();
        foreach (var kind in Enum.GetValues<SyncKind>()) data.Add(kind);
        return data;
    }

    [Fact]
    public async Task Send_to_QuickBooks_for_a_new_payment_stores_what_SCM_sends_back()
    {
        _h.Source.Has(TestPayloads.Customer());
        _h.Source.Has(SyncKind.Payment, new PaymentPayload
        {
            ExternalId = "PAY-9", DocNumber = "RCPT-9", CustomerExternalId = "C-1", TxnDate = TestPayloads.TxnDate,
            CurrencyCode = "PKR", Amount = 100m, Method = PaymentMethodCodes.Cash
        });

        var result = await Admin(a => a.PushAsync(new ManualPushRequest { Kind = "payments", ExternalIds = ["PAY-9"] }));

        (result.Requested, result.NotFound.Count).Should().Be((1, 0));
        var map = await _h.MapAsync(SyncKind.Payment, "PAY-9");
        map.PayloadJson.Should().Contain("RCPT-9");
        map.RequestedAt.Should().NotBeNull();
        map.State.Should().NotBe(SyncState.NotSynced);
    }
}
