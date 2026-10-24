using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;
using Xunit;
using static SMS.Integration.Tests.FinancialPosting.Fp;

namespace SMS.Integration.Tests.FinancialPosting;

/// <summary>
/// A44 (QA) — the posting gateway on the real host (LocalDB), called in-process as a caller would after its commit:
/// <list type="bullet">
/// <item>T-01/T-02, D-8 — <see cref="Without_a_target_nothing_posts_and_the_GL_posts_only_once_ready"/>.</item>
/// <item>T-07/T-08, D-13, BR-01 — <see cref="The_same_key_is_a_duplicate_a_changed_payload_an_update_and_an_unbalanced_one_is_refused"/>.</item>
/// <item>T-03..T-05 — <see cref="Both_targets_get_the_posting_and_one_failing_is_a_partial_failure"/>.</item>
/// <item>T-06, D-10 — <see cref="No_licensed_target_module_is_ModuleNotLicensed"/>.</item>
/// <item>D-12, D-31, per-org isolation — <see cref="Posting_api_permissions_and_organizations_are_kept_apart"/>.</item>
/// </list>
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~PostingGatewayE2ETests</c>.</para>
/// </summary>
public sealed class PostingGatewayE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public PostingGatewayE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A44");
    }

    [Fact]
    public async Task Without_a_target_nothing_posts_and_the_GL_posts_only_once_ready()
    {
        var o = await _root.NewOrgAsync("GW1");
        var k = o.K;

        // D-8 — lazy per-org config with the defaults
        var cfg = await k.Ok(k.GetConfig(), "read posting config");
        (cfg.B("internalAccountingEnabled"), cfg.B("autoPostToGL")).Should().Be((false, true), J.Short(cfg));

        // T-01 — nothing configured: NoTargetConfigured, no journal, no throw
        var tx = Synthetic(FinDocType.SalesInvoice, 1000m);
        var none = await _f.PostDirectAsync(o.OrgId, tx);
        none.Status.Should().Be(PostingStatus.NoTargetConfigured, none.Describe());
        (await _f.JournalsAsync(o.OrgId, tx.SourceDocumentId)).Should().BeEmpty();

        // enabled, but the A42 setup is not Complete: the GL target is not active (GL_NOT_READY), still no journal
        await k.Ok(k.PutConfig(true), "enable before the setup is complete");
        var notReady = await _f.PostDirectAsync(o.OrgId, Synthetic(FinDocType.SalesInvoice, 10m));
        (notReady.Status == PostingStatus.NoTargetConfigured
         || notReady.Target(PostingTargetCodes.InternalGl)?.Status == TargetPostingStatus.Skipped)
            .Should().BeTrue($"D-8: the GL posts only when enabled AND setup Complete — {notReady.Describe()}");
        if (notReady.Target(PostingTargetCodes.InternalGl) is { } skipped) skipped.Error.Should().Contain("GL_NOT_READY");
        (await _f.AllJournalsAsync(o.OrgId)).Should().BeEmpty();

        // T-02 — setup Complete: the GL target alone posts a balanced journal by role (D-5)
        await k.CompleteGlSetupAsync();
        var codes = await k.DefaultCodesAsync();
        var posted = await _f.PostDirectAsync(o.OrgId, tx);
        posted.Status.Should().Be(PostingStatus.Success, posted.Describe());
        posted.TargetResults.Should().ContainSingle("only the GL target is active").Which.Should()
            .Match<TargetResult>(t => t.TargetCode == PostingTargetCodes.InternalGl && t.Status == TargetPostingStatus.Posted, posted.Describe());
        posted.Target(PostingTargetCodes.InternalGl)!.InternalId.Should().NotBeNullOrWhiteSpace("the journal is named in the result");

        var journal = (await _f.JournalsAsync(o.OrgId, tx.SourceDocumentId)).Should().ContainSingle().Subject;
        journal.Balanced.Should().BeTrue(journal.ToString());
        (journal.Type, journal.Status, journal.Reference, journal.Key, journal.Debit, journal.Credit)
            .Should().Be(("SALES_INVOICE", "Posted", tx.SourceDocumentNumber, tx.IdempotencyKey, 1000m, 1000m), journal.ToString());
        journal.Net(codes["DEFAULT_RECEIVABLE"]).Should().Be(1000m);
        journal.Net(codes["DEFAULT_REVENUE"]).Should().Be(-1000m);
        journal.Date.Should().Be(tx.TransactionDate.Date);

        // the chart's balances moved (A42 CurrentBalance)
        var accounts = await k.AccountsAsync();
        accounts[codes["DEFAULT_RECEIVABLE"]].B("hasTransactions").Should().BeTrue();
    }

    [Fact]
    public async Task The_same_key_is_a_duplicate_a_changed_payload_an_update_and_an_unbalanced_one_is_refused()
    {
        var o = await _root.NewOrgAsync("GW2");
        var k = o.K;
        await k.EnableGlAsync();
        var codes = await k.DefaultCodesAsync();
        var receivable = codes["DEFAULT_RECEIVABLE"];

        var id = Guid.NewGuid();
        var first = Synthetic(FinDocType.SalesInvoice, 500m, id);
        (await _f.PostDirectAsync(o.OrgId, first)).Status.Should().Be(PostingStatus.Success);

        // T-07 — the same key and payload again: DuplicateSkipped, still one journal
        var identical = new FinancialTransaction
        {
            DocumentType = first.DocumentType, SourceDocumentId = id, SourceDocumentNumber = first.SourceDocumentNumber, SourceModule = first.SourceModule,
            TransactionDate = first.TransactionDate, CurrencyCode = first.CurrencyCode, Description = first.Description, Lines = first.Lines
        };
        var dup = await _f.PostDirectAsync(o.OrgId, identical);
        dup.Status.Should().Be(PostingStatus.DuplicateSkipped, dup.Describe());
        (await _f.JournalsAsync(o.OrgId, id)).Should().ContainSingle("a duplicate posts nothing");
        (await _f.NetAsync(o.OrgId, receivable)).Should().Be(500m);

        // D-13 — same key, changed payload (an edited document): the old journal is reversed and the new one posted
        var changed = Synthetic(FinDocType.SalesInvoice, 800m, id);
        var update = await _f.PostDirectAsync(o.OrgId, changed);
        update.Status.Should().Be(PostingStatus.Success, $"a changed payload is an update, not a duplicate — {update.Describe()}");
        var journals = await _f.JournalsAsync(o.OrgId, id);
        journals.Should().OnlyContain(j => j.Balanced, string.Join("\n", journals));
        journals.Where(j => j.Status is "Posted" or "Reversed").Sum(j => j.Net(receivable)) // A47 D-4 / D-CORE-4
            .Should().Be(800m, $"the reversal cancels the 500 and the new journal posts 800 — {string.Join("\n", journals)}");

        // BR-01 — unbalanced: ValidationFailed, nothing posted
        var unbalanced = Synthetic(FinDocType.SalesInvoice, 0m, lines: [("DEFAULT_RECEIVABLE", 100m, 0m), ("DEFAULT_REVENUE", 0m, 99.99m)]);
        var refused = await _f.PostDirectAsync(o.OrgId, unbalanced);
        refused.Status.Should().Be(PostingStatus.ValidationFailed, refused.Describe());
        (await _f.JournalsAsync(o.OrgId, unbalanced.SourceDocumentId)).Should().BeEmpty();

        // within 0.001 is balanced (rounding to DEFAULT_ROUNDING, D-5)
        var nearly = Synthetic(FinDocType.SalesInvoice, 0m, lines: [("DEFAULT_RECEIVABLE", 100.0005m, 0m), ("DEFAULT_REVENUE", 0m, 100m)]);
        (await _f.PostDirectAsync(o.OrgId, nearly)).Status.Should().NotBe(PostingStatus.ValidationFailed);

        // a role that is not an A42 default key is a caller error: ValidationFailed, nothing recorded
        var unknownRole = Synthetic(FinDocType.SalesInvoice, 0m, lines: [("DEFAULT_RECEIVABLE", 10m, 0m), ("NO_SUCH_ROLE", 0m, 10m)]);
        (await _f.PostDirectAsync(o.OrgId, unknownRole)).Status.Should().Be(PostingStatus.ValidationFailed);
        (await _f.JournalsAsync(o.OrgId, unknownRole.SourceDocumentId)).Should().BeEmpty();

        // a known role whose default is not assigned: never throws, never posts half a journal (D-9: recorded with the reason,
        // retried) — and posts once the default is assigned and the posting retried
        await k.Ok(k.SetDefault("DEFAULT_DISCOUNT_RECEIVED", null), "unassign an optional default");
        var unassigned = Synthetic(FinDocType.SalesInvoice, 0m, lines: [("DEFAULT_RECEIVABLE", 10m, 0m), ("DEFAULT_DISCOUNT_RECEIVED", 0m, 10m)]);
        var failed = await _f.PostDirectAsync(o.OrgId, unassigned);
        failed.Status.Should().NotBe(PostingStatus.Success, failed.Describe());
        (await _f.JournalsAsync(o.OrgId, unassigned.SourceDocumentId)).Should().BeEmpty("T-22: no journal without its posting");
        var row = (await k.PostingRowsAsync(unassigned.SourceDocumentId)).Should().ContainSingle("the failure is recorded for retry").Subject;
        row.TargetError("internal_gl").Should().Contain("DEFAULT_DISCOUNT_RECEIVED", $"the reason names the unassigned role — {J.Short(row)}");
        await k.Ok(k.SetDefault("DEFAULT_DISCOUNT_RECEIVED", (await k.AccountsAsync())[codes["DEFAULT_REVENUE"]].G("uuid")), "assign it");
        await k.Ok(k.RetryPosting(row, "internal_gl"), "retry");
        await _f.RunGlRetryJobAsync();
        (await _f.JournalsAsync(o.OrgId, unassigned.SourceDocumentId)).Should().ContainSingle("posted after the fix");

        // a void of a document never posted is a no-op that does not throw
        var voidNever = await _f.VoidDirectAsync(o.OrgId, new VoidRequest
        {
            SourceDocumentId = Guid.NewGuid(), SourceModule = PostingSourceModules.Finance, DocumentType = FinDocType.SalesInvoice,
            SourceDocumentNumber = "QA-NONE", Reason = "never posted"
        });
        voidNever.Status.Should().NotBe(PostingStatus.PartialFailure, voidNever.Describe());
    }

    [Fact]
    public async Task Both_targets_get_the_posting_and_one_failing_is_a_partial_failure()
    {
        var o = await (await _root.NewOrgAsync("GW3")).ConnectLiveAsync();
        var k = o.K;
        var pkr = o.O.Pkr;
        var (_, _, customer, item) = await k.StockedWorldAsync("GW3");

        // T-03 — QuickBooks only (GL off): an issued invoice reaches the gateway, the QuickBooks target queues it
        var inv1 = await k.IssuedInvoiceAsync(customer, item, 1m, pkr);
        var map1 = await _f.MapAsync(o.OrgId, inv1.G("uuid"), "SalesInvoice");
        map1.State.Should().NotBe("NotSynced", map1.ToString());
        (await _f.JournalsAsync(o.OrgId, inv1.G("uuid"))).Should().BeEmpty("the GL is off");
        var log1 = await k.PostingRowsAsync(inv1.G("uuid"));
        log1.Should().ContainSingle("D-12: the posting log shows the document with the QuickBooks target");
        log1[0].TargetStatus("quickbooks").Should().BeOneOf(new[] { "Queued", "Posted" }, J.Short(log1[0]));
        log1[0].TargetStatus("internal_gl").Should().Match(s => s == null || s == "Skipped");

        // T-04 — both: the GL posts and QuickBooks queues the same document
        await k.EnableGlAsync();
        var inv2 = await k.IssuedInvoiceAsync(customer, item, 2m, pkr);
        var j2 = (await _f.JournalsAsync(o.OrgId, inv2.G("uuid"))).Should().ContainSingle().Subject;
        j2.Balanced.Should().BeTrue(j2.ToString());
        (await _f.MapAsync(o.OrgId, inv2.G("uuid"), "SalesInvoice")).State.Should().NotBe("NotSynced");
        var log2 = (await k.PostingRowsAsync(inv2.G("uuid"))).Should().ContainSingle().Subject;
        (log2.TargetStatus("internal_gl"), log2.TargetStatus("quickbooks")).Should().Be(("Posted", "Queued"), J.Short(log2));

        await _f.RunJobsAsync(4);
        (await _f.MapAsync(o.OrgId, inv2.G("uuid"), "SalesInvoice")).State.Should().Be("Synced");

        // T-05 — the GL refuses (unknown leaf account) while QuickBooks takes it: PartialFailure, QuickBooks unaffected
        var customerUuid = customer.Uuid.ToString();
        var tx = new FinancialTransaction
        {
            DocumentType = FinDocType.SalesInvoice, SourceDocumentId = Guid.NewGuid(), SourceDocumentNumber = $"{k.Marker}-PF",
            SourceModule = PostingSourceModules.Finance, TransactionDate = Today, PartnerId = customer.Uuid, PartnerType = PartnerType.Customer,
            CurrencyCode = "PKR",
            Lines =
            [
                new FinancialTransactionLine { LineNumber = 1, AccountRole = "DEFAULT_RECEIVABLE", Debit = 100m },
                new FinancialTransactionLine { LineNumber = 2, AccountCode = "99999999", Credit = 100m }
            ],
            Metadata = new FinancialDocumentMetadata
            {
                Document = new SalesInvoicePayload
                {
                    ExternalId = Guid.NewGuid().ToString(), DocNumber = $"{k.Marker}-PF", CustomerExternalId = customerUuid, TxnDate = Today,
                    CurrencyCode = "PKR", ExpectedTotal = 100m,
                    Lines = [new SalesInvoiceLinePayload { LineNo = 1, ItemExternalId = item.VariantUuid.ToString(), Quantity = 1m, UnitPrice = 100m }]
                }
            }
        };
        var partial = await _f.PostDirectAsync(o.OrgId, tx);
        partial.Status.Should().Be(PostingStatus.PartialFailure, partial.Describe());
        partial.Target(PostingTargetCodes.InternalGl)!.Status.Should().BeOneOf(TargetPostingStatus.Failed, TargetPostingStatus.Queued);
        partial.Target(PostingTargetCodes.QuickBooks)!.Status.Should().BeOneOf(TargetPostingStatus.Queued, TargetPostingStatus.Posted);
    }

    [Fact]
    public async Task No_licensed_target_module_is_ModuleNotLicensed()
    {
        var o = await _root.NewOrgAsync("GW4");
        await o.K.EnableGlAsync();
        (await _f.PostDirectAsync(o.OrgId, Synthetic(FinDocType.Payment, 10m, lines: [("DEFAULT_CASH", 10m, 0m), ("DEFAULT_RECEIVABLE", 0m, 10m)])))
            .Status.Should().Be(PostingStatus.Success);

        await _root.LicenceAsync(o.OrgId, (Gl.Feature, false), ("MODULE_INTEGRATION", false));
        await _f.InvalidateAsync(o.OrgId);
        var tx = Synthetic(FinDocType.SalesInvoice, 10m);
        var r = await _f.PostDirectAsync(o.OrgId, tx);
        r.Status.Should().Be(PostingStatus.ModuleNotLicensed, r.Describe());
        (await _f.JournalsAsync(o.OrgId, tx.SourceDocumentId)).Should().BeEmpty();

        // GL licensed again, Integration still off → the GL target alone
        await _root.LicenceAsync(o.OrgId, (Gl.Feature, true));
        await _f.InvalidateAsync(o.OrgId);
        (await _f.PostDirectAsync(o.OrgId, tx)).Status.Should().Be(PostingStatus.Success);
    }

    [Fact]
    public async Task Posting_api_permissions_and_organizations_are_kept_apart()
    {
        var a = await _root.NewOrgAsync("GWA");
        var b = await _root.NewOrgAsync("GWB");
        await a.K.EnableGlAsync();
        await b.K.EnableGlAsync();

        var onlyA = Synthetic(FinDocType.SalesInvoice, 321m);
        (await _f.PostDirectAsync(a.OrgId, onlyA)).Status.Should().Be(PostingStatus.Success);
        (await _f.JournalsAsync(b.OrgId, onlyA.SourceDocumentId)).Should().BeEmpty("journals are per organization");
        (await b.K.PostingRowsAsync(onlyA.SourceDocumentId)).Should().BeEmpty("org B's posting log never shows org A's document");
        (await a.K.PostingRowsAsync(onlyA.SourceDocumentId)).Should().ContainSingle();

        // the same source uuid in another org is not a duplicate (keys are per organization)
        (await _f.PostDirectAsync(b.OrgId, onlyA)).Status.Should().Be(PostingStatus.Success);

        // summary counts by target × status (D-12)
        var summary = await a.K.Ok(a.K.Get($"{Posting}/summary"), "posting summary");
        summary.GetRawText().Should().ContainEquivalentOf("internal_gl");

        // D-31 — GL_VIEW reads, GL_SETUP_MANAGE writes, nobody else
        var k = a.K;
        var viewer = await k.LoginWithPermissionsAsync("fpview", Gl.View);
        var setup = await k.LoginWithPermissionsAsync("fpsetup", Gl.ManageSetup);
        var nobody = await k.LoginWithPermissionsAsync("fpnone", "INVOICE_VIEW");
        var row = (await k.PostingRowsAsync(onlyA.SourceDocumentId)).Single();
        var rowId = row.G("uuid");
        var detail = await k.PostingDetailAsync(row);
        detail.A("journals").Should().ContainSingle(J.Short(detail));
        detail.A("lines").Should().HaveCount(2);

        foreach (var url in new[] { $"{Posting}/config", $"{Posting}/log", $"{Posting}/summary", $"{Posting}/log/{rowId}" })
        {
            (await k.Get(url, viewer)).Status.Should().Be(HttpStatusCode.OK, $"GL_VIEW reads {url}");
            (await k.Get(url, nobody)).ShouldBeForbidden($"no GL permission: {url}");
        }
        (await k.PutConfig(true, true, viewer)).ShouldBeForbidden("GL_VIEW cannot change the config");
        (await k.RetryPosting(row, c: viewer)).ShouldBeForbidden("GL_VIEW cannot retry");
        (await k.SkipPosting(row, c: viewer)).ShouldBeForbidden("GL_VIEW cannot skip");
        (await k.PutConfig(true, true, nobody)).ShouldBeForbidden("no GL permission");
        await k.Ok(k.PutConfig(true, true, setup), "GL_SETUP_MANAGE changes the config");
        (await k.RetryPosting(row, c: setup)).ShouldNotBeForbidden("GL_SETUP_MANAGE may retry");

        // org B cannot read org A's row by id
        var cross = await b.K.Get($"{Posting}/log/{rowId}");
        cross.Status.Should().BeOneOf(new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden }, $"cross-org read — {cross}");
    }
}
