using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Posting;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;
using Xunit;
using static SMS.Modules.Accounting.Tests.PostingHarness;

namespace SMS.Modules.Accounting.Tests;

/// <summary>A44 CORE — gateway routing, validation, licence, idempotency/update, isolation (T-01..T-08, BR-01..BR-05).</summary>
public sealed class PostingGatewayTests
{
    private static string Key(Guid id) => $"finance:SalesInvoice:{id}";

    [Fact]
    public async Task T01_posts_to_every_active_target_and_records_the_log()
    {
        using var h = new PostingHarness();
        await h.SetupGlAsync();
        var id = Guid.NewGuid();

        var result = await h.Gateway.PostAsync(Invoice(id));

        result.Status.Should().Be(PostingStatus.Success);
        result.TargetResults.Select(r => (r.TargetCode, r.Status)).Should().BeEquivalentTo(new[]
            { (PostingTargetCodes.InternalGl, TargetPostingStatus.Posted), (PostingTargetCodes.QuickBooks, TargetPostingStatus.Queued) });
        var posting = await h.PostingAsync(Key(id));
        posting.Kind.Should().Be(PostingKinds.Post);
        posting.TotalAmount.Should().Be(118m);
        posting.Targets.Should().OnlyContain(t => t.Status == PostingTargetStatuses.Posted || t.Status == PostingTargetStatuses.Queued);
        h.External.Posts.Should().ContainSingle();
    }

    [Fact]
    public async Task T02_BR01_unbalanced_transaction_is_ValidationFailed_and_not_recorded()
    {
        using var h = new PostingHarness();
        await h.SetupGlAsync();
        var id = Guid.NewGuid();
        var tx = Invoice(id);
        var unbalanced = new FinancialTransaction
        {
            DocumentType = tx.DocumentType, SourceDocumentId = id, SourceModule = tx.SourceModule, TransactionDate = tx.TransactionDate,
            CurrencyCode = "PKR", Lines = [L(1, DefaultKeys.Receivable, 118m, 0), L(2, DefaultKeys.Revenue, 0, 117.99m)],
        };

        var result = await h.Gateway.PostAsync(unbalanced);

        result.Status.Should().Be(PostingStatus.ValidationFailed);
        result.Message.Should().Contain("do not balance");
        (await h.Db.FinancialPostings.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        h.External.Posts.Should().BeEmpty();
    }

    [Fact]
    public async Task BR01_a_difference_within_0_001_is_accepted()
    {
        using var h = new PostingHarness(withExternal: true);
        var tx = new FinancialTransaction
        {
            DocumentType = FinDocType.SalesInvoice, SourceDocumentId = Guid.NewGuid(), SourceModule = "finance", TransactionDate = new DateTime(2026, 10, 1),
            CurrencyCode = "PKR", Lines = [L(1, DefaultKeys.Receivable, 10.0005m, 0), L(2, DefaultKeys.Revenue, 0, 10m)],
        };
        (await h.Gateway.PostAsync(tx)).Status.Should().Be(PostingStatus.Success);
    }

    [Fact]
    public async Task BR02_every_line_problem_is_reported_at_once()
    {
        using var h = new PostingHarness();
        var tx = new FinancialTransaction
        {
            DocumentType = FinDocType.SalesInvoice, SourceDocumentId = Guid.NewGuid(), SourceModule = "finance", TransactionDate = new DateTime(2026, 10, 1),
            CurrencyCode = "PKR",
            Lines =
            [
                L(1, DefaultKeys.Receivable, -5m, 0),
                L(2, DefaultKeys.Revenue, 5m, 5m),
                new FinancialTransactionLine { LineNumber = 3, Debit = 1m },
                L(4, "DEFAULT_NOPE", 0, 1m),
            ],
        };

        var result = await h.Gateway.PostAsync(tx);

        result.Status.Should().Be(PostingStatus.ValidationFailed);
        result.Message.Should().Contain("Line 1: amounts cannot be negative")
            .And.Contain("Line 2: a line is either a debit or a credit")
            .And.Contain("Line 3: an account role or account code is required")
            .And.Contain("Line 4: unknown account role DEFAULT_NOPE");
    }

    [Fact]
    public async Task T03_same_key_same_payload_is_DuplicateSkipped_for_the_GL()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));

        var again = await h.Gateway.PostAsync(Invoice(id));

        again.Status.Should().Be(PostingStatus.DuplicateSkipped);
        again.TargetResults.Should().ContainSingle(r => r.TargetCode == PostingTargetCodes.InternalGl && r.Status == TargetPostingStatus.Posted);
        (await h.JournalsAsync()).Should().ContainSingle();
        (await h.BalanceAsync("1113")).Should().Be(118m);
    }

    [Fact]
    public async Task REV3_same_payload_is_sent_to_external_targets_again_but_not_to_the_GL()
    {
        using var h = new PostingHarness();
        await h.SetupGlAsync();
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));

        var again = await h.Gateway.PostAsync(Invoice(id));

        again.Status.Should().Be(PostingStatus.Success);
        h.External.Posts.Should().HaveCount(2);
        (await h.JournalsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task T04_update_on_changed_fingerprint_reverses_and_reposts()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id, net: 100m, tax: 18m));

        var update = await h.Gateway.PostAsync(Invoice(id, net: 200m, tax: 36m));

        update.Status.Should().Be(PostingStatus.Success);
        var journals = await h.JournalsAsync();
        journals.Should().HaveCount(3);
        journals[1].ReversesEntryId.Should().Be(journals[0].Id);
        journals[1].Lines.Single(l => l.LineNumber == 1).CreditAmount.Should().Be(118m);
        journals[2].TotalDebit.Should().Be(236m);
        (await h.BalanceAsync("1113")).Should().Be(236m);
        (await h.BalanceAsync("4111")).Should().Be(200m);
        (await h.BalanceAsync("2112")).Should().Be(36m);
        var posting = await h.PostingAsync(Key(id));
        posting.Version.Should().Be(2);
        posting.Targets.Single().InternalId.Should().Be(journals[2].EntryNumber);
    }

    [Fact]
    public async Task T05_no_active_target_is_NoTargetConfigured()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync(enable: false);

        var result = await h.Gateway.PostAsync(Invoice(Guid.NewGuid()));

        result.Status.Should().Be(PostingStatus.NoTargetConfigured);
        (await h.Db.FinancialPostings.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task T06_D10_ModuleNotLicensed_only_when_neither_GL_nor_Integration_is_on()
    {
        var gate = new FakeGate();
        using var h = new PostingHarness(gate: gate);
        (await h.Gateway.PostAsync(Invoice(Guid.NewGuid()))).Status.Should().Be(PostingStatus.ModuleNotLicensed);
        h.External.Posts.Should().BeEmpty();

        gate.On.Add((h.OrgA, ModuleCodes.Integration));
        (await h.Gateway.PostAsync(Invoice(Guid.NewGuid()))).Status.Should().Be(PostingStatus.Success);
    }

    [Fact]
    public async Task D10_GL_target_inactive_while_feature_off_or_setup_incomplete()
    {
        var gate = new FakeGate();
        using var h = new PostingHarness(withExternal: false, gate: gate);
        gate.On.Add((h.OrgA, ModuleCodes.Integration));
        await h.SetupGlAsync();
        var gl = h.Get<IEnumerable<IFinancialPostingTarget>>().Single(t => t.TargetCode == PostingTargetCodes.InternalGl);
        (await gl.IsActiveAsync()).Should().BeFalse("FEATURE_GENERAL_LEDGER is off");

        gate.On.Add((h.OrgA, AccountingCodes.FeatureGeneralLedger));
        (await gl.IsActiveAsync()).Should().BeTrue();

        var status = await h.Db.CoaSetupStatuses.IgnoreQueryFilters().SingleAsync(s => s.OrganizationId == h.OrgA);
        status.Status = SetupStatuses.BalancesEntered;
        await h.Db.SaveChangesAsync();
        (await gl.IsActiveAsync()).Should().BeFalse("the setup is not Complete");
    }

    [Fact]
    public async Task T07_BR04_one_target_failing_never_stops_another()
    {
        using var h = new PostingHarness();
        await h.SetupGlAsync();
        h.External.Throw = new InvalidOperationException("Intuit is down");
        var id = Guid.NewGuid();

        var result = await h.Gateway.PostAsync(Invoice(id));

        result.Status.Should().Be(PostingStatus.PartialFailure);
        result.Message.Should().Contain("Intuit is down");
        result.TargetResults.Single(r => r.TargetCode == PostingTargetCodes.InternalGl).Status.Should().Be(TargetPostingStatus.Posted);
        var row = (await h.PostingAsync(Key(id))).Targets.Single(t => t.TargetCode == PostingTargetCodes.QuickBooks);
        row.Status.Should().Be(PostingTargetStatuses.Failed);
        row.Attempts.Should().Be(1);
        row.NextAttemptAt.Should().Be(h.Clock.Now.UtcDateTime.AddSeconds(30));
        (await h.JournalsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task T08_BR03_the_gateway_never_throws()
    {
        using var h = new PostingHarness();
        h.External.ThrowOnActive = true;

        (await h.Gateway.PostAsync(null!)).Status.Should().Be(PostingStatus.ValidationFailed);
        (await h.Gateway.VoidAsync(null!)).Status.Should().Be(PostingStatus.ValidationFailed);
        (await h.Gateway.PostAsync(Invoice(Guid.NewGuid()))).Status.Should().Be(PostingStatus.NoTargetConfigured,
            "a target that cannot say whether it is active counts as inactive");
    }

    [Fact]
    public async Task T08_IssueCredit_refuses_a_non_credit_type()
    {
        using var h = new PostingHarness();
        (await h.Gateway.IssueCreditAsync(Invoice(Guid.NewGuid()))).Status.Should().Be(PostingStatus.ValidationFailed);
    }

    [Fact]
    public async Task T21_the_source_commit_is_independent_of_the_GL_failure()
    {
        // D-9 — the caller already committed; a GL refusal (unmapped role) is a Failed row to retry, never an exception.
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var id = Guid.NewGuid();
        var tx = Invoice(id);
        var withPayable = new FinancialTransaction
        {
            DocumentType = tx.DocumentType, SourceDocumentId = id, SourceModule = tx.SourceModule, TransactionDate = tx.TransactionDate,
            CurrencyCode = "PKR", Lines = [L(1, DefaultKeys.Receivable, 10m, 0), L(2, DefaultKeys.DiscountReceived, 0, 10m)],
        };

        var result = await h.Gateway.PostAsync(withPayable);

        result.Status.Should().Be(PostingStatus.PartialFailure);
        var row = (await h.PostingAsync(Key(id))).Targets.Single();
        row.Status.Should().Be(PostingTargetStatuses.Failed);
        row.LastError.Should().StartWith("UNMAPPED_ROLE: DEFAULT_DISCOUNT_RECEIVED");
        (await h.JournalsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task T22_GL_journal_and_posting_row_are_written_together()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));
        h.NewScope();

        var journal = (await h.JournalsAsync()).Single();
        var row = (await h.PostingAsync(Key(id))).Targets.Single();
        row.Status.Should().Be(PostingTargetStatuses.Posted);
        row.InternalId.Should().Be(journal.EntryNumber);
        row.PostedFingerprint.Should().NotBeNullOrEmpty();
        journal.IdempotencyKey.Should().Be(Key(id));
    }

    [Fact]
    public async Task T23_the_posting_row_exists_before_any_target_is_called()
    {
        using var h = new PostingHarness();
        var id = Guid.NewGuid();
        h.External.OnPost = _ =>
        {
            h.Db.FinancialPostings.IgnoreQueryFilters().AsNoTracking().Any(p => p.IdempotencyKey == Key(id)).Should().BeTrue();
            return new TargetResult { TargetCode = PostingTargetCodes.QuickBooks, Status = TargetPostingStatus.Queued };
        };
        (await h.Gateway.PostAsync(Invoice(id))).Status.Should().Be(PostingStatus.Success);
    }

    [Fact]
    public async Task Isolation_per_organization()
    {
        using var h = new PostingHarness();
        await h.SetupGlAsync(h.OrgA);
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));

        h.Tenant.Current = h.OrgB; // org B: same document uuid, no GL
        var b = await h.Gateway.PostAsync(Invoice(id));

        b.Status.Should().Be(PostingStatus.Success);
        b.TargetResults.Should().ContainSingle(r => r.TargetCode == PostingTargetCodes.QuickBooks);
        (await h.PostingAsync(Key(id), h.OrgB)).Targets.Should().ContainSingle();
        (await h.JournalsAsync(h.OrgB)).Should().BeEmpty();
        (await h.Log.ListAsync(h.Admin(h.OrgB), new PostingLogQuery())).TotalRecords.Should().Be(1);
        var aUuid = (await h.PostingAsync(Key(id), h.OrgA)).Uuid;
        await FluentActions.Invoking(() => h.Log.GetAsync(h.Admin(h.OrgB), aUuid)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task The_document_is_stored_and_rebuilt_for_a_retry()
    {
        using var h = new PostingHarness();
        var id = Guid.NewGuid();
        var doc = new SalesInvoicePayload { ExternalId = id.ToString(), DocNumber = "SI-2026-00001", CustomerExternalId = "c-1", CurrencyCode = "PKR",
            ExpectedTotal = 118m };
        h.External.Throw = new InvalidOperationException("down");
        await h.Gateway.PostAsync(Invoice(id, document: doc));
        var posting = await h.PostingAsync(Key(id));
        posting.DocumentPayloadType.Should().Be(nameof(SalesInvoicePayload));

        h.External.Throw = null;
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.NewScope();
        (await h.Get<PostingRetryJob>().RunAsync()).Should().Be(1);

        var rebuilt = h.External.Posts.Last().Metadata!.Document.Should().BeOfType<SalesInvoicePayload>().Subject;
        rebuilt.DocNumber.Should().Be("SI-2026-00001");
        rebuilt.ExpectedTotal.Should().Be(118m);
        (await h.PostingAsync(Key(id))).Targets.Single().Status.Should().Be(PostingTargetStatuses.Queued);
    }

    [Fact]
    public void The_real_gateway_replaces_a_Null_registered_before_it()
    {
        using var h = new PostingHarness();
        h.Gateway.Should().BeOfType<FinancialPostingGateway>();
        h.Get<IEntitySyncGateway>().Should().BeOfType<EntitySyncGateway>();
        h.Get<IExternalAccountMapReader>().Should().BeOfType<ExternalAccountMapReader>();
    }

    [Theory]
    [InlineData("SalesInvoice", "SALES_INVOICE")]
    [InlineData("POSSale", "POS_SALE")]
    [InlineData("BillPayment", "BILL_PAYMENT")]
    [InlineData("ProductionCompletion", "PRODUCTION_COMPLETION")]
    public void Journal_entry_type_is_upper_snake(string type, string expected) => JournalEntryTypes.For(type).Should().Be(expected);
}
