using FluentAssertions;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Posting;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;
using Xunit;
using static SMS.Modules.Accounting.Tests.PostingHarness;

namespace SMS.Modules.Accounting.Tests;

/// <summary>A44 CORE — QA-2 / D-49b: the posting log's quickbooks rows follow the QuickBooks outbox; D-36 journal lines in the detail.</summary>
public sealed class PostingStatusRefreshTests
{
    private static string Key(Guid id) => $"finance:SalesInvoice:{id}";

    private static FinancialTransaction Payment(Guid id) => new()
    {
        DocumentType = FinDocType.Payment, SourceDocumentId = id, SourceDocumentNumber = "RCPT-1", SourceModule = "finance",
        TransactionDate = new DateTime(2026, 10, 15), CurrencyCode = "PKR",
        Lines = [L(1, DefaultKeys.Cash, 50m, 0), L(2, DefaultKeys.Receivable, 0, 50m)],
        Metadata = new FinancialDocumentMetadata { Document = new PaymentPayload { ExternalId = id.ToString(), DocNumber = "RCPT-1", Amount = 50m } },
    };

    private static SalesInvoicePayload Doc(Guid id) => new() { ExternalId = id.ToString(), DocNumber = "SI-1", CurrencyCode = "PKR", ExpectedTotal = 118m };

    [Fact]
    public async Task QA2_list_detail_by_source_and_summary_bring_queued_rows_up_to_date_and_persist_it()
    {
        var qb = new FakeQuickBooksStatus();
        using var h = new PostingHarness(quickBooks: qb);
        var invoice = Guid.NewGuid();
        var payment = Guid.NewGuid();
        var pending = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(invoice, document: Doc(invoice)));
        await h.Gateway.PostAsync(Payment(payment));
        await h.Gateway.PostAsync(Invoice(pending, document: Doc(pending), number: "SI-2"));
        qb.Set(SyncKind.SalesInvoice, invoice.ToString(), SyncState.Synced, remoteId: "186", docNumber: "1045");
        qb.Set(SyncKind.Payment, payment.ToString(), SyncState.Failed, error: "Business validation error");
        qb.Set(SyncKind.SalesInvoice, pending.ToString(), SyncState.WaitingOnDependency);

        var list = await h.Log.ListAsync(h.Viewer(), new PostingLogQuery());

        qb.Calls.Select(c => c.Kind).Should().BeEquivalentTo([SyncKind.SalesInvoice, SyncKind.Payment], "one status call per kind");
        var synced = list.Data.Single(p => p.SourceDocumentUuid == invoice).Targets.Single();
        (synced.Status, synced.ExternalId, synced.ExternalDocNumber).Should().Be((PostingTargetStatuses.Posted, "186", "1045"));
        var failed = list.Data.Single(p => p.SourceDocumentUuid == payment).Targets.Single();
        failed.Status.Should().Be(PostingTargetStatuses.Failed);
        failed.LastError.Should().Contain("Business validation error");
        failed.NextAttemptAt.Should().BeNull("a mirrored QuickBooks failure is retried by Integration, not by the Accounting job");
        list.Data.Single(p => p.SourceDocumentUuid == pending).Targets.Single().Status.Should().Be(PostingTargetStatuses.Queued);

        // Persisted: the stored rows changed, and a second read asks only about the row still open.
        h.NewScope();
        (await h.PostingAsync(Key(invoice))).Targets.Single().Status.Should().Be(PostingTargetStatuses.Posted);
        qb.Calls.Clear();
        var summary = await h.Get<IPostingLogService>().SummaryAsync(h.Viewer());
        // The open invoice, and the mirrored payment failure (so a later Synced can recover it) — still one call per kind.
        qb.Calls.Select(x => (x.Kind, string.Join(",", x.Ids))).Should().BeEquivalentTo(
            [(SyncKind.SalesInvoice, pending.ToString()), (SyncKind.Payment, payment.ToString())]);
        var row = summary.Targets.Single();
        (row.Posted, row.Failed, row.Queued).Should().Be((1, 1, 1));

        qb.Set(SyncKind.SalesInvoice, pending.ToString(), SyncState.Synced, remoteId: "188");
        var bySource = await h.Get<IPostingLogService>().BySourceAsync(h.Viewer(), pending);
        bySource.Single().Targets.Single().ExternalId.Should().Be("188");
        h.NewScope();
        var uuid = (await h.PostingAsync(Key(pending))).Uuid;
        qb.Calls.Clear();
        (await h.Get<IPostingLogService>().GetAsync(h.Viewer(), uuid)).Targets.Single().Status.Should().Be(PostingTargetStatuses.Posted);
        qb.Calls.Should().BeEmpty("nothing is open any more");
    }

    [Fact]
    public async Task QA2_void_posting_follows_the_voided_record_and_errors_change_nothing()
    {
        var qb = new FakeQuickBooksStatus();
        using var h = new PostingHarness(quickBooks: qb);
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id, document: Doc(id)));
        await h.Gateway.VoidAsync(VoidOf(id));

        qb.Throw = true;
        (await h.Get<IPostingLogService>().BySourceAsync(h.Viewer(), id)).Should().OnlyContain(p => p.Targets.Single().Status == PostingTargetStatuses.Queued);

        qb.Throw = false;
        qb.Set(SyncKind.SalesInvoice, id.ToString(), SyncState.Synced, remoteId: "186");
        var items = await h.Get<IPostingLogService>().BySourceAsync(h.Viewer(), id);
        items[0].Targets.Single().Status.Should().Be(PostingTargetStatuses.Posted);
        items[1].Targets.Single().Status.Should().Be(PostingTargetStatuses.Queued, "the record is not voided in QuickBooks yet");

        qb.Set(SyncKind.SalesInvoice, id.ToString(), SyncState.Voided, remoteId: "186");
        items = await h.Get<IPostingLogService>().BySourceAsync(h.Viewer(), id);
        items[1].Targets.Single().Status.Should().Be(PostingTargetStatuses.Posted);
        items[1].Targets.Single().LastError.Should().BeNull();
    }

    [Fact]
    public async Task REV_optionB_mirrored_failure_is_left_by_the_job_retried_by_hand_dead_lettered_by_QBO_and_recovers()
    {
        var qb = new FakeQuickBooksStatus();
        using var h = new PostingHarness(quickBooks: qb);
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id, document: Doc(id)));
        h.External.Posts.Should().HaveCount(1);
        qb.Set(SyncKind.SalesInvoice, id.ToString(), SyncState.Failed, error: "Refused by Intuit");

        // The job's refresh mirrors the failure — and then the same run, and later runs, leave it alone.
        h.NewScope();
        await h.Get<PostingRetryJob>().RunAsync();
        h.Clock.Advance(TimeSpan.FromHours(5));
        h.NewScope();
        (await h.Get<PostingRetryJob>().RunAsync()).Should().Be(0);
        h.NewScope();
        var row = (await h.PostingAsync(Key(id))).Targets.Single();
        (row.Status, row.MirroredFromTarget, row.NextAttemptAt, row.Attempts).Should().Be((PostingTargetStatuses.Failed, true, (DateTime?)null, 0));
        row.LastError.Should().Be("QuickBooks Failed: 6000: Refused by Intuit");
        h.External.Posts.Should().HaveCount(1, "the Accounting job never re-sends a mirrored failure");

        // A manual retry re-dispatches it and clears the flag.
        var uuid = (await h.PostingAsync(Key(id))).Uuid;
        qb.Set(SyncKind.SalesInvoice, id.ToString(), SyncState.Pending);
        var retried = await h.Log.RetryAsync(h.IntegrationAdmin(), uuid, null);
        h.External.Posts.Should().HaveCount(2);
        retried.Posting.Targets.Single().Status.Should().Be(PostingTargetStatuses.Queued);
        h.NewScope();
        (await h.PostingAsync(Key(id))).Targets.Single().MirroredFromTarget.Should().BeFalse();

        // QBO NeedsResolution → DeadLetter (still mirrored, still not touched by the job).
        qb.Set(SyncKind.SalesInvoice, id.ToString(), SyncState.NeedsResolution, error: "Outcome unknown");
        await h.Get<IPostingLogService>().SummaryAsync(h.Viewer());
        h.NewScope();
        row = (await h.PostingAsync(Key(id))).Targets.Single();
        (row.Status, row.MirroredFromTarget).Should().Be((PostingTargetStatuses.DeadLetter, true));
        (await h.Get<PostingRetryJob>().RunAsync()).Should().Be(0);
        h.External.Posts.Should().HaveCount(2);

        // Recovery: QuickBooks synced it after all (resolved there) → Posted, flag cleared.
        qb.Set(SyncKind.SalesInvoice, id.ToString(), SyncState.Synced, remoteId: "186", docNumber: "1045");
        h.NewScope();
        await h.Get<PostingRetryJob>().RunAsync();
        h.NewScope();
        row = (await h.PostingAsync(Key(id))).Targets.Single();
        (row.Status, row.MirroredFromTarget, row.ExternalId).Should().Be((PostingTargetStatuses.Posted, false, "186"));
    }
    [Fact]
    public async Task QA2_the_retry_job_refreshes_every_run()
    {
        var qb = new FakeQuickBooksStatus();
        using var h = new PostingHarness(quickBooks: qb);
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id, document: Doc(id)));
        qb.Set(SyncKind.SalesInvoice, id.ToString(), SyncState.Synced, remoteId: "186", docNumber: "1045");

        h.NewScope();
        await h.Get<PostingRetryJob>().RunAsync();

        h.NewScope();
        var row = (await h.PostingAsync(Key(id))).Targets.Single();
        (row.Status, row.ExternalId, row.ExternalDocNumber).Should().Be((PostingTargetStatuses.Posted, "186", "1045"));
    }

    [Fact]
    public async Task D36_detail_journals_carry_their_lines_and_reversal_links()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));
        await h.Gateway.PostAsync(Invoice(id, net: 200m, tax: 36m));

        var detail = await h.Log.GetAsync(h.Viewer(), (await h.PostingAsync(Key(id))).Uuid);

        detail.Journals.Should().HaveCount(3);
        var first = detail.Journals[0];
        first.Lines.Select(l => (l.LineNumber, l.AccountCode, l.Debit, l.Credit)).Should().Equal((1, "1113", 118m, 0m), (2, "4111", 0m, 100m), (3, "2112", 0m, 18m));
        first.Lines[0].AccountName.Should().Be("Account 1113");
        first.Lines[0].Description.Should().Be("Invoice SI-2026-00001");
        first.ReversedByJournalNumber.Should().Be(detail.Journals[1].EntryNumber);
        detail.Journals[1].ReversesJournalNumber.Should().Be(first.EntryNumber);
        detail.Journals[1].Lines[0].Credit.Should().Be(118m);
        detail.Journals[2].ReversedByJournalNumber.Should().BeNull();
        detail.Journals[2].Lines.Sum(l => l.Debit).Should().Be(236m);
    }
}
