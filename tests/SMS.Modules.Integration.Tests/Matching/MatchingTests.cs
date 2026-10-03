using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Matching;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Exceptions;
using SMS.Shared.Integration.QuickBooks;
using Script = SMS.Modules.Integration.Tests.Fakes.ScriptedAccountingProvider.Script;

namespace SMS.Modules.Integration.Tests.Matching;

/// <summary>Matching held records against the accountant's existing QuickBooks records. Nothing is ever created here.</summary>
public class MatchingTests : IAsyncLifetime
{
    private readonly SyncHarness _h = new();

    public async Task InitializeAsync() => await _h.ConnectAsync();   // only when referenced: payloads are held, not sent
    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<T> Matching<T>(Func<IQuickBooksMatchingService, Task<T>> call) =>
        _h.Scoped(sp => call(sp.GetRequiredService<IQuickBooksMatchingService>()));

    private async Task<MatchCandidateModel> CandidateFor(SyncKind kind, string externalId) =>
        (await Matching(m => m.ListAsync(kind, null))).Single(c => c.ExternalId == externalId);

    [Fact]
    public async Task Exact_matches_by_name_tax_id_account_number_code_and_sku()
    {
        _h.Provider.Seed(SyncKind.Customer, "  acme   TRADERS ");                 // same name, spacing and case aside
        _h.Provider.Seed(SyncKind.Customer, "Totally Different", taxId: "TX-C-2");
        _h.Provider.Seed(SyncKind.Vendor, "Some Supplier", accountNumber: "S001");
        _h.Provider.Seed(SyncKind.Vendor, "Another Supplier", accountNumber: "S-002");
        _h.Provider.Seed(SyncKind.Item, "Old Widget Name", sku: "W-1");

        await _h.Send(TestPayloads.Customer(id: "C-1", name: "Acme Traders"));
        await _h.Send(TestPayloads.Customer(id: "C-2", name: "Beta Foods"));
        await _h.Send(TestPayloads.Vendor(id: "V-1", name: "Karachi Supplies", code: "X", accountNumber: "S001"));
        await _h.Send(TestPayloads.Vendor(id: "V-2", name: "Lahore Supplies", code: "S002", accountNumber: null));
        await _h.Send(TestPayloads.Item(id: "I-1", name: "Widget", sku: "w-1"));

        var customers = await Matching(m => m.ScanAsync(SyncKind.Customer));
        var vendors   = await Matching(m => m.ScanAsync(SyncKind.Vendor));
        var items     = await Matching(m => m.ScanAsync(SyncKind.Item));

        customers.Should().BeEquivalentTo(new MatchScanResultModel { Kind = "Customer", LocalCount = 2, RemoteCount = 2, Exact = 2 });
        vendors.Exact.Should().Be(2);
        items.Exact.Should().Be(1);

        (await CandidateFor(SyncKind.Customer, "C-1")).Should().Match<MatchCandidateModel>(c => c.Confidence == "Exact" && c.Reason == "Exact name" && c.Decision == "Pending");
        (await CandidateFor(SyncKind.Customer, "C-2")).Reason.Should().Be("Tax id");
        (await CandidateFor(SyncKind.Vendor, "V-1")).Reason.Should().Be("Account number");
        (await CandidateFor(SyncKind.Vendor, "V-2")).Reason.Should().Be("Code");
        (await CandidateFor(SyncKind.Item, "I-1")).Should().Match<MatchCandidateModel>(c => c.Reason == "SKU" && c.RemoteName == "Old Widget Name");

        _h.Provider.WriteCalls.Should().Be(0);
    }

    [Fact]
    public async Task Probable_matches_by_email_similar_name_and_containment_and_none_otherwise()
    {
        _h.Provider.Seed(SyncKind.Customer, "Gamma Industries", email: "AP@delta.example");
        _h.Provider.Seed(SyncKind.Customer, "Epsilon Trading Co.");
        _h.Provider.Seed(SyncKind.Customer, "Zeta Holdings International");

        await _h.Send(TestPayloads.Customer(id: "C-1", name: "Delta Corp", email: "ap@delta.example"));
        await _h.Send(TestPayloads.Customer(id: "C-2", name: "Epsilon Trading (Pvt) Ltd"));
        await _h.Send(TestPayloads.Customer(id: "C-3", name: "Zeta Holdings"));
        await _h.Send(TestPayloads.Customer(id: "C-4", name: "Omega", email: "o@x.example"));

        var result = await Matching(m => m.ScanAsync(SyncKind.Customer));

        result.Should().BeEquivalentTo(new MatchScanResultModel { Kind = "Customer", LocalCount = 4, RemoteCount = 3, Probable = 3, Unmatched = 1 });
        (await CandidateFor(SyncKind.Customer, "C-1")).Reason.Should().Be("Email");
        (await CandidateFor(SyncKind.Customer, "C-2")).Reason.Should().Be("Similar name");
        (await CandidateFor(SyncKind.Customer, "C-3")).Reason.Should().Be("Name contains");
        var none = await CandidateFor(SyncKind.Customer, "C-4");
        none.Confidence.Should().Be("None");
        none.RemoteId.Should().BeNull();
    }

    [Fact]
    public async Task A_remote_record_already_linked_to_another_record_is_not_offered_again()
    {
        var remote = _h.Provider.Seed(SyncKind.Customer, "Acme Traders");
        await using (var db = _h.DbAs(_h.OrgId))
        {
            var connection = await db.Connections.SingleAsync();
            db.EntityMaps.Add(new EntityMap { ConnectionId = connection.Id, Kind = SyncKind.Customer, ExternalId = "C-OLD", DisplayLabel = "Acme Traders", RemoteId = remote.Id, PayloadJson = "{}" });
            await db.SaveChangesAsync();
        }
        await _h.Send(TestPayloads.Customer(id: "C-NEW", name: "Acme Traders"));

        var result = await Matching(m => m.ScanAsync(SyncKind.Customer));

        result.LocalCount.Should().Be(1, "linked records are not scanned");
        result.Unmatched.Should().Be(1);
        (await CandidateFor(SyncKind.Customer, "C-NEW")).RemoteId.Should().BeNull();
    }

    [Fact]
    public async Task A_rescan_replaces_open_proposals_and_keeps_decisions()
    {
        _h.Provider.Seed(SyncKind.Customer, "Acme Traders");
        await _h.Send(TestPayloads.Customer(id: "C-1", name: "Acme Traders"));
        await _h.Send(TestPayloads.Customer(id: "C-2", name: "Beta Foods"));
        await Matching(m => m.ScanAsync(SyncKind.Customer));

        var skip = await CandidateFor(SyncKind.Customer, "C-2");
        await Matching(m => m.ConfirmAsync(SyncKind.Customer, new ConfirmMatchesRequest { Decisions = [new MatchDecisionItem { CandidateId = skip.Id, Decision = "Skip" }] }, 1));

        await Matching(m => m.ScanAsync(SyncKind.Customer));
        await Matching(m => m.ScanAsync(SyncKind.Customer));

        var all = await Matching(m => m.ListAsync(SyncKind.Customer, null));
        all.Should().HaveCount(2);
        all.Count(c => c.Decision == "Pending").Should().Be(1);
        all.Single(c => c.ExternalId == "C-2").Decision.Should().Be("Skip");
        (await Matching(m => m.ListAsync(SyncKind.Customer, "skip"))).Should().ContainSingle();
    }

    [Fact]
    public async Task Link_adopts_without_writing_and_the_next_identical_payload_is_a_no_op()
    {
        var remote = _h.Provider.Seed(SyncKind.Customer, "Acme Traders");
        await _h.Send(TestPayloads.Customer());
        await Matching(m => m.ScanAsync(SyncKind.Customer));
        var candidate = await CandidateFor(SyncKind.Customer, "C-1");

        var decided = await Matching(m => m.ConfirmAsync(SyncKind.Customer,
            new ConfirmMatchesRequest { Decisions = [new MatchDecisionItem { CandidateId = candidate.Id, Decision = "Link" }] }, userId: 9));

        decided.Single().Decision.Should().Be("Link");
        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.RemoteId.Should().Be(remote.Id);
        map.LinkOrigin.Should().Be(LinkOrigin.Adopted);
        map.RemoteName.Should().Be("Acme Traders");
        map.RemoteSyncToken.Should().BeNull("fetched before any later update");
        map.State.Should().Be(SyncState.Synced);
        map.LinkedByUserId.Should().Be(9);
        map.LastPushedFingerprint.Should().Be(map.PayloadFingerprint);

        (await _h.Send(TestPayloads.Customer())).State.Should().Be(SyncState.Synced);
        await _h.DrainAsync();
        _h.Provider.WriteCalls.Should().Be(0, "the accountant's record is left as it is");

        // A real change later is a sparse update of the adopted record (token fetched first).
        var changed = TestPayloads.Customer();
        changed.Phone = "042-000-111";
        await _h.Send(changed);
        await _h.DrainAsync();
        _h.Provider.Calls.Select(c => c.Operation).Should().Equal("List", "Get", "Update");
        _h.Provider.Of(SyncKind.Customer).Should().ContainSingle();
    }

    [Fact]
    public async Task Linking_a_remote_record_that_is_already_linked_is_refused()
    {
        var remote = _h.Provider.Seed(SyncKind.Customer, "Acme Traders");
        await _h.Send(TestPayloads.Customer(id: "C-1", name: "Acme Traders"));
        await _h.Send(TestPayloads.Customer(id: "C-2", name: "Acme Traders Ltd"));
        await Matching(m => m.ScanAsync(SyncKind.Customer));
        var first  = await CandidateFor(SyncKind.Customer, "C-1");
        var second = await CandidateFor(SyncKind.Customer, "C-2");

        // Both in one request…
        var both = () => Matching(m => m.ConfirmAsync(SyncKind.Customer, new ConfirmMatchesRequest
        {
            Decisions =
            [
                new MatchDecisionItem { CandidateId = first.Id, Decision = "Link" },
                new MatchDecisionItem { CandidateId = second.Id, Decision = "Link", RemoteId = remote.Id }
            ]
        }, 1));
        await both.Should().ThrowAsync<BadRequestException>().WithMessage("*already linked*");
        (await _h.MapAsync(SyncKind.Customer, "C-1")).RemoteId.Should().BeNull("nothing was saved");

        // …or one after the other.
        await Matching(m => m.ConfirmAsync(SyncKind.Customer, new ConfirmMatchesRequest { Decisions = [new MatchDecisionItem { CandidateId = first.Id, Decision = "Link" }] }, 1));
        var later = () => Matching(m => m.ConfirmAsync(SyncKind.Customer,
            new ConfirmMatchesRequest { Decisions = [new MatchDecisionItem { CandidateId = second.Id, Decision = "Link", RemoteId = remote.Id }] }, 1));
        await later.Should().ThrowAsync<BadRequestException>().WithMessage("*already linked*");
    }

    [Fact]
    public async Task Create_new_marks_the_record_wanted_and_queues_it()
    {
        await _h.Send(TestPayloads.Customer(name: "Brand New Customer"));
        await Matching(m => m.ScanAsync(SyncKind.Customer));
        var candidate = await CandidateFor(SyncKind.Customer, "C-1");

        await Matching(m => m.ConfirmAsync(SyncKind.Customer, new ConfirmMatchesRequest { Decisions = [new MatchDecisionItem { CandidateId = candidate.Id, Decision = "CreateNew" }] }, 1));

        var map = await _h.MapAsync(SyncKind.Customer, "C-1");
        map.RequestedAt.Should().Be(_h.Now);
        map.State.Should().Be(SyncState.Pending);
        _h.Provider.WriteCalls.Should().Be(0, "nothing is created during matching");

        await _h.DrainAsync();
        _h.Provider.Of(SyncKind.Customer).Should().ContainSingle(r => r.Name == "Brand New Customer");
    }

    [Fact]
    public async Task Skip_stops_the_record_and_the_gateway_then_treats_it_as_disabled()
    {
        await _h.UpdateSettingsAsync(s => s.PartnerScope = PartnerScope.AllActive);
        await _h.Send(TestPayloads.Customer());                          // queued
        await Matching(m => m.ScanAsync(SyncKind.Customer));
        var candidate = await CandidateFor(SyncKind.Customer, "C-1");

        await Matching(m => m.ConfirmAsync(SyncKind.Customer, new ConfirmMatchesRequest { Decisions = [new MatchDecisionItem { CandidateId = candidate.Id, Decision = "skip" }] }, 1));

        (await _h.MapAsync(SyncKind.Customer, "C-1")).State.Should().Be(SyncState.NotSynced);
        (await _h.EntriesAsync(SyncKind.Customer, "C-1")).Should().OnlyContain(e => e.Status == OutboxStatus.Done);
        (await _h.Send(TestPayloads.Customer(name: "Changed Name"))).Outcome.Should().Be(GatewayOutcome.Disabled);
        await _h.DrainAsync();
        _h.Provider.WriteCalls.Should().Be(0);
    }

    [Fact]
    public async Task Linking_a_customer_releases_invoices_that_waited_for_it()
    {
        var remote = _h.Provider.Seed(SyncKind.Customer, "Acme Traders");
        var item = _h.Provider.Seed(SyncKind.Item, "Widget");
        await _h.Send(TestPayloads.Item());
        await _h.Send(TestPayloads.Customer());
        await Matching(m => m.ScanAsync(SyncKind.Item));
        await Matching(m => m.ScanAsync(SyncKind.Customer));
        var itemCandidate = await CandidateFor(SyncKind.Item, "I-1");
        await Matching(m => m.ConfirmAsync(SyncKind.Item, new ConfirmMatchesRequest { Decisions = [new MatchDecisionItem { CandidateId = itemCandidate.Id, Decision = "Link" }] }, 1));

        // Waits on the customer — whose held payload is queued as a dependency.
        (await _h.Send(TestPayloads.Invoice())).Outcome.Should().Be(GatewayOutcome.WaitingOnDependency);
        (await _h.OpenEntryAsync(SyncKind.Customer, "C-1")).Status.Should().Be(OutboxStatus.Queued);

        // Linking closes that queued create (it would have been a duplicate) and releases the invoice.
        var customerCandidate = await CandidateFor(SyncKind.Customer, "C-1");
        await Matching(m => m.ConfirmAsync(SyncKind.Customer, new ConfirmMatchesRequest { Decisions = [new MatchDecisionItem { CandidateId = customerCandidate.Id, Decision = "Link" }] }, 1));

        (await _h.OpenEntryAsync(SyncKind.SalesInvoice, "SI-1")).Status.Should().Be(OutboxStatus.Queued);
        await _h.DrainAsync();
        var invoice = (RemoteInvoice)_h.Provider.Calls.Single(c => c.Operation == "Create").Entity!;
        invoice.CustomerId.Should().Be(remote.Id);
        invoice.Lines.Single().ItemId.Should().Be(item.Id);
    }

    [Fact]
    public async Task Scans_page_through_QuickBooks_a_thousand_at_a_time()
    {
        for (var i = 0; i < 1500; i++) _h.Provider.Seed(SyncKind.Item, $"Item {i:0000}", sku: $"SKU-{i}");
        await _h.Send(TestPayloads.Item(name: "Item 1499", sku: "nope"));

        var result = await Matching(m => m.ScanAsync(SyncKind.Item));

        result.RemoteCount.Should().Be(1500);
        result.Exact.Should().Be(1);
        _h.Provider.CallsOf("List").Should().Be(2);
        await using var db = _h.SuperDb();
        (await db.SyncLog.CountAsync(l => l.Operation == "Query")).Should().Be(2);
    }

    [Fact]
    public async Task Scan_needs_a_connection_and_a_matchable_kind_and_reports_a_failed_read()
    {
        var invoices = () => Matching(m => m.ScanAsync(SyncKind.SalesInvoice));
        await invoices.Should().ThrowAsync<BadRequestException>();

        _h.Provider.Enqueue("List", new Script(ProviderOutcomeKind.Transient, Message: "timeout"));
        var failed = () => Matching(m => m.ScanAsync(SyncKind.Customer));
        await failed.Should().ThrowAsync<ConflictException>().WithMessage("*could not be read*");

        _h.Provider.Enqueue("List", new Script(ProviderOutcomeKind.AuthRevoked));
        var revoked = () => Matching(m => m.ScanAsync(SyncKind.Customer));
        await revoked.Should().ThrowAsync<ConflictException>().WithMessage("*revoked*");
        _h.Health.Calls.Should().ContainSingle(c => c.Status == ConnectionStatus.Revoked);

        var notConnected = () => Matching(m => m.ScanAsync(SyncKind.Customer));
        await notConnected.Should().ThrowAsync<ConflictException>().WithMessage("*not connected*");
    }

    [Fact]
    public async Task Confirm_validates_decisions_and_candidates()
    {
        _h.Provider.Seed(SyncKind.Customer, "Acme Traders");
        await _h.Send(TestPayloads.Customer());
        await Matching(m => m.ScanAsync(SyncKind.Customer));
        var candidate = await CandidateFor(SyncKind.Customer, "C-1");

        var empty = () => Matching(m => m.ConfirmAsync(SyncKind.Customer, new ConfirmMatchesRequest(), 1));
        await empty.Should().ThrowAsync<BadRequestException>();

        var bogus = () => Matching(m => m.ConfirmAsync(SyncKind.Customer, new ConfirmMatchesRequest { Decisions = [new MatchDecisionItem { CandidateId = candidate.Id, Decision = "Maybe" }] }, 1));
        await bogus.Should().ThrowAsync<BadRequestException>();

        var wrongKind = () => Matching(m => m.ConfirmAsync(SyncKind.Vendor, new ConfirmMatchesRequest { Decisions = [new MatchDecisionItem { CandidateId = candidate.Id, Decision = "Link" }] }, 1));
        await wrongKind.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Candidates_of_another_organization_are_invisible()
    {
        _h.Provider.Seed(SyncKind.Customer, "Acme Traders");
        await _h.Send(TestPayloads.Customer());
        await Matching(m => m.ScanAsync(SyncKind.Customer));
        var candidate = await CandidateFor(SyncKind.Customer, "C-1");

        var orgB = Guid.NewGuid();
        await _h.ConnectAsync(organizationId: orgB, realmId: "REALM-B");
        _h.Tenant.RequestOrganizationId = orgB;

        (await Matching(m => m.ListAsync(SyncKind.Customer, null))).Should().BeEmpty();
        var steal = () => Matching(m => m.ConfirmAsync(SyncKind.Customer, new ConfirmMatchesRequest { Decisions = [new MatchDecisionItem { CandidateId = candidate.Id, Decision = "Link" }] }, 1));
        await steal.Should().ThrowAsync<NotFoundException>();
    }

    [Theory]
    [InlineData("Acme Traders (Pvt) Ltd.", "ACME TRADERS")]
    [InlineData("Acme Co", "ACME")]
    [InlineData("Co", "CO")]
    [InlineData("  Beta,  Foods   Limited ", "BETA FOODS")]
    public void Loose_names_drop_punctuation_and_trailing_company_forms(string input, string expected) =>
        MatchNormalizer.Loose(input).Should().Be(expected);

    [Fact]
    public void Containment_needs_a_meaningful_shorter_name()
    {
        MatchNormalizer.Contains("ZETA HOLDINGS", "ZETA HOLDINGS INTERNATIONAL").Should().BeTrue();
        MatchNormalizer.Contains("ABC", "ABC TRADING").Should().BeFalse("three letters match too much");
        MatchNormalizer.Exact("  a   b  ").Should().Be("A B");
        MatchNormalizer.Id("tx-12 34").Should().Be("TX1234");
    }
}
