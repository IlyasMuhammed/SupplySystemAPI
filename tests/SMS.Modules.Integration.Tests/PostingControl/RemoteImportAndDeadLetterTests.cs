using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Matching;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Exceptions;
using SMS.Shared.Integration.QuickBooks;
using Script = SMS.Modules.Integration.Tests.Fakes.ScriptedAccountingProvider.Script;

namespace SMS.Modules.Integration.Tests.PostingControl;

/// <summary>
/// A44 D-17 import from QuickBooks / link / unlink (T-40..T-44) and D-11 dead-letter bulk retry and dismiss.
/// </summary>
public class RemoteImportAndDeadLetterTests : IAsyncLifetime
{
    private readonly SyncHarness _h = PostingControlKit.Harness();

    public async Task InitializeAsync() => await _h.ConnectAsync(s => s.PartnerScope = PartnerScope.AllActive);
    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<T> Remote<T>(Func<IQuickBooksRemoteEntityService, Task<T>> call) =>
        _h.Scoped(sp => call(sp.GetRequiredService<IQuickBooksRemoteEntityService>()));

    private Task<T> Admin<T>(Func<IQuickBooksSyncAdminService, Task<T>> call) =>
        _h.Scoped(sp => call(sp.GetRequiredService<IQuickBooksSyncAdminService>()));

    [Fact]
    public async Task T40_import_reads_every_record_creates_nothing_and_a_second_import_updates_and_removes()
    {
        var acme  = _h.Provider.Seed(SyncKind.Customer, "Acme Traders", email: "ap@acme.example");
        var gone  = _h.Provider.Seed(SyncKind.Customer, "Gone Ltd");
        _h.Provider.Seed(SyncKind.Vendor, "Not a customer");

        var first = await Remote(r => r.ImportAsync(SyncKind.Customer));
        first.Should().Match<RemoteImportResultModel>(x => x.Read == 2 && x.Added == 2 && x.Unlinked == 2);

        _h.Provider.Company.Remove(gone);
        acme.Name = "Acme Traders (Pvt)";
        var second = await Remote(r => r.ImportAsync(SyncKind.Customer));

        second.Should().Match<RemoteImportResultModel>(x => x.Read == 1 && x.Updated == 1 && x.Removed == 1);
        var list = await Remote(r => r.ListAsync(SyncKind.Customer, new RemoteEntityQuery()));
        list.Data.Should().ContainSingle().Which.Name.Should().Be("Acme Traders (Pvt)");
        _h.Provider.WriteCalls.Should().Be(0, "importing never writes to QuickBooks");
        (await _h.FindMapAsync(SyncKind.Customer, acme.Id)).Should().BeNull("and never creates an SCM record");

        var invoices = async () => await Remote(r => r.ImportAsync(SyncKind.SalesInvoice));
        await invoices.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task T41_link_adopts_the_imported_record_and_the_list_filters_on_it()
    {
        var acme = _h.Provider.Seed(SyncKind.Customer, "Acme Trading Company");
        _h.Provider.Seed(SyncKind.Customer, "Other");
        await _h.Send(TestPayloads.Customer());
        await Remote(r => r.ImportAsync(SyncKind.Customer));

        var linked = await Remote(r => r.LinkAsync(SyncKind.Customer, acme.Id, new LinkRemoteEntityRequest { ExternalId = "C-1" }, 7));

        linked.Should().Match<RemoteEntityModel>(m => m.Linked && m.LinkedExternalId == "C-1");
        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        (map.RemoteId, map.LinkOrigin, map.State).Should().Be((acme.Id, LinkOrigin.Adopted, SyncState.Synced));
        map.LastPushedFingerprint.Should().Be(map.PayloadFingerprint, "the accountant's record is not overwritten");
        (await _h.EntriesAsync(SyncKind.Customer, "C-1")).Should().OnlyContain(e => e.Status == OutboxStatus.Done);

        (await Remote(r => r.ListAsync(SyncKind.Customer, new RemoteEntityQuery { Linked = false }))).Data
            .Should().ContainSingle(e => e.Name == "Other");
        (await Remote(r => r.ListAsync(SyncKind.Customer, new RemoteEntityQuery { Linked = true }))).Data
            .Should().ContainSingle(e => e.RemoteId == acme.Id);

        await _h.DrainAsync();
        _h.Provider.CallsOf("Create").Should().Be(0);
    }

    [Fact]
    public async Task T42_link_is_refused_for_a_record_already_backing_another_and_for_an_unknown_one()
    {
        var acme = _h.Provider.Seed(SyncKind.Customer, "Acme");
        await _h.Send(TestPayloads.Customer());
        await _h.Send(TestPayloads.Customer(id: "C-2", name: "Beta"));
        await Remote(r => r.ImportAsync(SyncKind.Customer));
        await Remote(r => r.LinkAsync(SyncKind.Customer, acme.Id, new LinkRemoteEntityRequest { ExternalId = "C-1" }, 7));

        var taken = async () => await Remote(r => r.LinkAsync(SyncKind.Customer, acme.Id, new LinkRemoteEntityRequest { ExternalId = "C-2" }, 7));
        (await taken.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("already linked");

        var unknownRemote = async () => await Remote(r => r.LinkAsync(SyncKind.Customer, "999", new LinkRemoteEntityRequest { ExternalId = "C-2" }, 7));
        await unknownRemote.Should().ThrowAsync<NotFoundException>();

        _h.Source.Records.Clear();
        var unknownScm = async () => await Remote(r => r.LinkAsync(SyncKind.Customer, acme.Id, new LinkRemoteEntityRequest { ExternalId = "C-404" }, 7));
        await unknownScm.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task T43_unlink_is_refused_while_an_open_synced_document_uses_the_record()
    {
        _h.Source.Has(TestPayloads.Customer());
        _h.Source.Has(TestPayloads.Item());
        await _h.Send(TestPayloads.Customer());
        await _h.Send(TestPayloads.Item());
        await _h.Send(TestPayloads.Invoice());
        await _h.DrainAsync();
        (await _h.MapAsync(SyncKind.SalesInvoice, "SI-1")).State.Should().Be(SyncState.Synced);

        var customer = await _h.MapAsync(SyncKind.Customer, "C-1");
        var act = async () => await Admin(a => a.UnlinkAsync(customer.Uuid, 7));

        var conflict = (await act.Should().ThrowAsync<IntegrationConflictException>()).Which;
        conflict.Detail.Should().BeOfType<UnlinkRefusedModel>().Which.OpenDocuments.Should().Be(1);
        (await _h.MapAsync(SyncKind.Customer, "C-1")).RemoteId.Should().NotBeNull();

        var invoice = await _h.MapAsync(SyncKind.SalesInvoice, "SI-1");
        var document = async () => await Admin(a => a.UnlinkAsync(invoice.Uuid, 7));
        await document.Should().ThrowAsync<BadRequestException>("documents are voided, not unlinked");
    }

    [Fact]
    public async Task T44_unlink_forgets_the_link_and_its_ledger_and_keeps_the_QuickBooks_record()
    {
        await _h.Send(TestPayloads.Customer(id: "C-2", name: "Beta"));
        await _h.DrainAsync();
        var before = await _h.MapAsync(SyncKind.Customer, "C-2");
        before.RemoteId.Should().NotBeNull();

        var item = await Admin(a => a.UnlinkAsync(before.Uuid, 7));

        item.State.Should().Be("NotSynced");
        var after = await _h.MapAsync(SyncKind.Customer, "C-2");
        (after.RemoteId, after.LinkOrigin, after.LastPushedFingerprint).Should().Be((null, null, null));
        (await _h.ClaimsAsync(after.Id)).Should().BeEmpty();
        _h.Provider.Of(SyncKind.Customer).Should().ContainSingle(r => r.Id == before.RemoteId, "the record stays in QuickBooks");
        await using var db = _h.SuperDb();
        (await db.SettingsAudit.AsNoTracking().SingleAsync(a => a.Area == AuditAreas.Link)).BeforeJson.Should().Contain(before.RemoteId!);
    }

    [Fact]
    public async Task Bulk_retry_requeues_the_dead_letter_and_says_why_others_were_left()
    {
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Refused, Code: "6000", Message: "Refused"),
                                      new Script(ProviderOutcomeKind.Refused, Code: "6000", Message: "Refused"));
        await _h.Send(TestPayloads.Customer());
        await _h.Send(TestPayloads.Vendor());
        await _h.DrainAsync();
        (await _h.MapAsync(SyncKind.Customer, "C-1")).State.Should().Be(SyncState.Failed);
        (await _h.MapAsync(SyncKind.Vendor, "V-1")).State.Should().Be(SyncState.Failed);

        // One of them skipped in matching: a retry never overrides that.
        await using (var db = _h.SuperDb())
        {
            var vendor = await db.EntityMaps.SingleAsync(m => m.Kind == SyncKind.Vendor);
            db.MatchCandidates.Add(new MatchCandidate { OrganizationId = _h.OrgId, ConnectionId = vendor.ConnectionId, EntityMapId = vendor.Id, Kind = SyncKind.Vendor, Decision = MatchDecision.Skip });
            await db.SaveChangesAsync();
        }

        var deadLetter = await Admin(a => a.GetItemsAsync(new SyncItemQuery { DeadLetter = true }));
        deadLetter.TotalRecords.Should().Be(2);

        var result = await Admin(a => a.RetryFailedAsync(new RetryFailedRequest(), 7));

        result.Should().Match<RetryFailedResult>(r => r.Found == 2 && r.Requeued == 1);
        result.Skipped.Should().ContainSingle().Which.Reason.Should().Contain("skipped in matching");
        (await _h.MapAsync(SyncKind.Customer, "C-1")).State.Should().Be(SyncState.Pending);

        (await Admin(a => a.RetryFailedAsync(new RetryFailedRequest { Kind = "items" }, 7))).Found.Should().Be(0);
    }

    [Fact]
    public async Task Dismissing_a_dead_letter_record_stops_it_and_only_dead_letter_records_can_be()
    {
        _h.Provider.Enqueue("Create", new Script(ProviderOutcomeKind.Refused, Code: "6000", Message: "Refused"));
        await _h.Send(TestPayloads.Customer());
        await _h.DrainAsync();
        var map = await _h.MapAsync(SyncKind.Customer, "C-1");

        var item = await Admin(a => a.SkipAsync(map.Uuid, 7));

        (item.State, item.LastErrorCode).Should().Be(("NotSynced", SyncAdminService.DismissedCode));
        item.LastError.Should().Contain("Refused");
        (await Admin(a => a.GetItemsAsync(new SyncItemQuery { DeadLetter = true }))).TotalRecords.Should().Be(0);

        var again = async () => await Admin(a => a.SkipAsync(map.Uuid, 7));
        await again.Should().ThrowAsync<BadRequestException>();
    }
}
