using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.FinancialPosting;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Finance.Posting;
using Xunit;
using static SMS.Integration.Tests.JournalEngine.Je;

namespace SMS.Integration.Tests.JournalEngine;

/// <summary>
/// A47 (QA) — C11: the A44 posting gateway delegating to the journal engine, on the real host (LocalDB), Intuit faked.
/// Tasks 415–420 (TS-GW-001..006), D-12 resolver, D-4 / D-19 (A44 update/void mark the original Reversed).
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~JournalEngine.JeGatewayE2ETests</c>.</para>
/// </summary>
public sealed class JeGatewayE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public JeGatewayE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A47");
    }

    private async Task<JsonElement> JournalOfAsync(JeOrg o, Guid source) => await o.K.EntryAsync(await _f.JournalUuidAsync(o.OrgId, source));

    [Fact]
    public async Task TS_GW_001_sale_invoice_through_the_gateway_is_a_SALE_journal()
    {
        var o = await _root.NewJeOrgAsync("GW1");
        var k = o.K;
        var (_, _, customer, item) = await k.StockedWorldAsync("GW1", price: 100m);

        var inv = await k.IssuedInvoiceAsync(customer, item, 2m, o.Fp.O.Pkr);
        var j = await JournalOfAsync(o, inv.G("uuid"));
        (j.P("journalType").S("code"), j.S("status"), j.S("entryType")).Should().Be(("SALE", "Posted", "SALES_INVOICE"), J.Short(j));
        j.S("entryNumber").Should().Be($"SALE/{Y}/0001", "TS-GW-001: the engine's per-type sequence (C11)");
        j.P("fiscalPeriod").I("periodNumber").Should().Be(Today.Month, "the period holding the invoice date");
        j.P("fiscalYear").G("uuid").Should().Be((await k.YearAsync()).G("uuid"));
        j.D("totalDebit").Should().Be(j.D("totalCredit"));
        j.LinesOn(o.Acc("DEFAULT_RECEIVABLE")).Sum(l => l.D("debit")).Should().Be(inv.D("grandTotal"));
        j.S("sourceModule").Should().NotBeNullOrEmpty();
        j.NG("sourceDocumentUuid").Should().Be(inv.G("uuid"));
        j.B("isManual").Should().BeFalse();
        // the AR line carries the customer (D-13: where the source sends one)
        j.LinesOn(o.Acc("DEFAULT_RECEIVABLE")).Single().NG("partnerUuid").Should().Be(customer.Uuid);

        var row = (await k.PostingRowsAsync(inv.G("uuid"))).Should().ContainSingle().Subject;
        row.TargetStatus("internal_gl").Should().Be("Posted");

        var inv2 = await k.IssuedInvoiceAsync(customer, item, 1m, o.Fp.O.Pkr);
        (await JournalOfAsync(o, inv2.G("uuid"))).S("entryNumber").Should().Be($"SALE/{Y}/0002");

        // a source draft is not editable/deletable by hand (contract JE-014 SourceDraft) — but the posted one is reversible (D-14)
        j.B("canEdit").Should().BeFalse();
        j.B("canReverse").Should().BeTrue();
    }

    [Fact]
    public async Task TS_GW_002_purchase_bill_through_the_gateway_is_a_PURCH_journal()
    {
        var o = await _root.NewJeOrgAsync("GW2");
        var k = o.K;
        var vendor = await k.CreateVendorAsync("GW2 Vendor");

        var bill = await k.ApprovedBillAsync(vendor, 1000m, 170m);
        var j = await JournalOfAsync(o, bill);
        (j.P("journalType").S("code"), j.S("entryType"), j.S("status")).Should().Be(("PURCH", "PURCHASE_BILL", "Posted"), J.Short(j));
        j.S("entryNumber").Should().Be($"PURCH/{Y}/0001", "TS-GW-002");
        j.LinesOn(o.Acc("DEFAULT_PAYABLE")).Sum(l => l.D("credit")).Should().Be(1170m);

        // the bill payment goes to BANK (no DEFAULT_CASH line, D-12)
        var pay = await k.PostedSupplierPaymentAsync(vendor, bill, 1170m, "BANK_TRANSFER");
        var pj = await JournalOfAsync(o, pay);
        (pj.P("journalType").S("code"), pj.S("entryNumber")).Should().Be(("BANK", $"BANK/{Y}/0001"));
        // a cash one to CASH
        var bill2 = await k.ApprovedBillAsync(vendor, 50m);
        var cashPay = await k.PostedSupplierPaymentAsync(vendor, bill2, 50m, "CASH");
        (await JournalOfAsync(o, cashPay)).P("journalType").S("code").Should().Be("CASH");
        (await JournalOfAsync(o, bill2)).S("entryNumber").Should().Be($"PURCH/{Y}/0002");
    }

    [Fact]
    public async Task TS_GW_003_internal_GL_and_QuickBooks_outbox_both_get_it()
    {
        var o = await _root.NewJeOrgAsync("GW3", qbo: true);
        var k = o.K;
        var (_, _, customer, item) = await k.StockedWorldAsync("GW3");

        var inv = await k.IssuedInvoiceAsync(customer, item, 1m, o.Fp.O.Pkr);
        var j = await JournalOfAsync(o, inv.G("uuid"));
        (j.P("journalType").S("code"), j.S("status")).Should().Be(("SALE", "Posted"), "the GL posts synchronously");
        j.S("entryNumber").Should().MatchRegex(NumberFormat.ToString());
        var row = (await k.PostingRowsAsync(inv.G("uuid"))).Should().ContainSingle().Subject;
        (row.TargetStatus("internal_gl"), row.TargetStatus("quickbooks")).Should().Be(("Posted", "Queued"), J.Short(row));

        await _f.RunJobsAsync(4);
        (await _f.MapAsync(o.OrgId, inv.G("uuid"), "SalesInvoice")).State.Should().Be("Synced", "the outbox reached the fake company");
        (await _f.JournalUuidsAsync(o.OrgId, inv.G("uuid"))).Should().ContainSingle("the jobs never post the GL twice");
    }

    [Fact]
    public async Task TS_GW_004_GL_off_only_the_outbox()
    {
        var o = await _root.NewJeOrgAsync("GW4", gl: false, qbo: true);
        var k = o.K;
        (await k.Ok(k.GetConfig(), "config")).B("internalAccountingEnabled").Should().BeFalse();
        var (_, _, customer, item) = await k.StockedWorldAsync("GW4");

        var inv = await k.IssuedInvoiceAsync(customer, item, 1m, o.Fp.O.Pkr);
        (await _f.JournalUuidsAsync(o.OrgId, inv.G("uuid"))).Should().BeEmpty("TS-GW-004: no journal");
        var row = (await k.PostingRowsAsync(inv.G("uuid"))).Should().ContainSingle().Subject;
        row.TargetStatus("quickbooks").Should().BeOneOf(new[] { "Queued", "Posted" }, J.Short(row));
        row.TargetStatus("internal_gl").Should().Match(s => s == null || s == "Skipped");
        await _f.RunJobsAsync(4);
        (await _f.MapAsync(o.OrgId, inv.G("uuid"), "SalesInvoice")).State.Should().Be("Synced");
        (await k.ListEntriesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task TS_GW_005_closed_period_GL_fails_QuickBooks_row_still_written()
    {
        var o = await _root.NewJeOrgAsync("GW5", qbo: true);
        var k = o.K;
        var (_, _, customer, item) = await k.StockedWorldAsync("GW5");

        var period = (await k.PeriodAsync(Today)).G("uuid");
        await k.Ok(k.LockPeriod(period), "lock this month");
        await k.Ok(k.FinalClosePeriod(period), "close this month");

        var inv = await k.IssuedInvoiceAsync(customer, item, 1m, o.Fp.O.Pkr);
        (await k.GetInvoiceAsync(inv.G("uuid"))).S("status").Should().Be("ISSUED", "the source commit never depends on the GL");
        (await _f.JournalUuidsAsync(o.OrgId, inv.G("uuid"))).Should().BeEmpty("TS-GW-005: JE creation fails");
        var row = (await k.PostingRowsAsync(inv.G("uuid"))).Should().ContainSingle().Subject;
        row.TargetStatus("internal_gl").Should().BeOneOf(new[] { "Failed", "Pending", "DeadLetter" }, J.Short(row));
        (row.TargetError("internal_gl") ?? "").Should().Match(e => e.Contains("PERIOD_CLOSED") || e.Contains("Period is closed"), J.Short(row));
        row.TargetStatus("quickbooks").Should().BeOneOf(new[] { "Queued", "Posted" }, "the outbox row is still written");

        await _f.RunJobsAsync(4);
        (await _f.MapAsync(o.OrgId, inv.G("uuid"), "SalesInvoice")).State.Should().Be("Synced", "QuickBooks gets it regardless");
        (await _f.JournalUuidsAsync(o.OrgId, inv.G("uuid"))).Should().BeEmpty("a Closed period stays closed: the retry keeps failing");

        // a Locked period behaves the same, and reopening lets the retry post it with the next SALE number
        var o2 = await _root.NewJeOrgAsync("GW5B");
        var (_, _, c2, i2) = await o2.K.StockedWorldAsync("GW5B");
        var p2 = (await o2.K.PeriodAsync(Today)).G("uuid");
        await o2.K.Ok(o2.K.LockPeriod(p2), "lock");
        var inv2 = await o2.K.IssuedInvoiceAsync(c2, i2, 1m, o2.Fp.O.Pkr);
        (await _f.JournalUuidsAsync(o2.OrgId, inv2.G("uuid"))).Should().BeEmpty();
        await o2.K.Ok(o2.K.ReopenPeriod(p2), "reopen");
        var row2 = (await o2.K.PostingRowsAsync(inv2.G("uuid"))).Single();
        await o2.K.Ok(o2.K.RetryPosting(row2, "internal_gl"), "retry");
        await _f.RunGlRetryJobAsync();
        (await JournalOfAsync(o2, inv2.G("uuid"))).S("entryNumber").Should().Be($"SALE/{Y}/0001", "a refused post consumes no number");
    }

    [Fact]
    public async Task TS_GW_006_duplicate_idempotency_key_returns_the_existing_result()
    {
        var o = await _root.NewJeOrgAsync("GW6");
        var id = Guid.NewGuid();
        var first = Fp.Synthetic(FinDocType.SalesInvoice, 500m, id);
        (await _f.PostDirectAsync(o.OrgId, first)).Status.Should().Be(PostingStatus.Success);
        var j1 = await JournalOfAsync(o, id);
        j1.S("entryNumber").Should().Be($"SALE/{Y}/0001");

        var same = new FinancialTransaction
        {
            DocumentType = first.DocumentType, SourceDocumentId = id, SourceDocumentNumber = first.SourceDocumentNumber, SourceModule = first.SourceModule,
            TransactionDate = first.TransactionDate, CurrencyCode = first.CurrencyCode, Description = first.Description, Lines = first.Lines
        };
        var dup = await _f.PostDirectAsync(o.OrgId, same);
        dup.Status.Should().Be(PostingStatus.DuplicateSkipped, dup.Describe());
        (await _f.JournalUuidsAsync(o.OrgId, id)).Should().ContainSingle("TS-GW-006: no duplicate JE");

        // parallel duplicates
        var many = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _f.PostDirectAsync(o.OrgId, same)));
        many.Should().OnlyContain(r => r.Status == PostingStatus.DuplicateSkipped || r.Status == PostingStatus.Success, string.Join("; ", many.Select(m => m.Describe())));
        (await _f.JournalUuidsAsync(o.OrgId, id)).Should().ContainSingle();

        // the duplicate consumed no sequence number
        var next = Fp.Synthetic(FinDocType.SalesInvoice, 10m);
        await _f.PostDirectAsync(o.OrgId, next);
        (await JournalOfAsync(o, next.SourceDocumentId)).S("entryNumber").Should().Be($"SALE/{Y}/0002");
    }

    [Fact]
    public async Task Resolver_maps_every_document_kind_to_its_journal_type()
    {
        var o = await _root.NewJeOrgAsync("GWR");
        var k = o.K;

        async Task<string> TypeOf(FinDocType type, params (string Role, decimal Dr, decimal Cr)[] lines)
        {
            var tx = Fp.Synthetic(type, 0m, lines: lines);
            var r = await _f.PostDirectAsync(o.OrgId, tx);
            r.Status.Should().Be(PostingStatus.Success, $"{type}: {r.Describe()}");
            var j = await JournalOfAsync(o, tx.SourceDocumentId);
            j.S("entryNumber").Should().MatchRegex(NumberFormat.ToString(), $"{type}");
            return j.P("journalType").S("code")!;
        }

        (await TypeOf(FinDocType.Payment, ("DEFAULT_CASH", 10m, 0m), ("DEFAULT_RECEIVABLE", 0m, 10m))).Should().Be("CASH");
        (await TypeOf(FinDocType.Payment, ("DEFAULT_BANK", 10m, 0m), ("DEFAULT_RECEIVABLE", 0m, 10m))).Should().Be("BANK");
        (await TypeOf(FinDocType.BillPayment, ("DEFAULT_PAYABLE", 10m, 0m), ("DEFAULT_BANK", 0m, 10m))).Should().Be("BANK");
        (await TypeOf(FinDocType.CreditNote, ("DEFAULT_REVENUE", 10m, 0m), ("DEFAULT_RECEIVABLE", 0m, 10m))).Should().Be("SALE");
        (await TypeOf(FinDocType.VendorCredit, ("DEFAULT_PAYABLE", 10m, 0m), ("DEFAULT_PURCHASE", 0m, 10m))).Should().Be("PURCH");
        (await TypeOf(FinDocType.InventoryAdjustment, ("DEFAULT_INVENTORY", 10m, 0m), ("DEFAULT_COGS", 0m, 10m))).Should().Be("MISC");
        var misc = Fp.Synthetic(FinDocType.ManualJournal, 0m, lines: [("DEFAULT_CASH", 5m, 0m), ("DEFAULT_REVENUE", 0m, 5m)]);
        await _f.PostDirectAsync(o.OrgId, misc);
        (await JournalOfAsync(o, misc.SourceDocumentId)).S("entryNumber").Should().StartWith($"JV/{Y}/", "MISC prefix JV");

        // D-12 — a deactivated target type falls back to MISC; the system post is never refused
        var sale = await k.TypeAsync("SALE");
        await k.Ok(k.Patch($"{Types}/{sale}/deactivate", null), "deactivate SALE");
        (await TypeOf(FinDocType.SalesInvoice, ("DEFAULT_RECEIVABLE", 10m, 0m), ("DEFAULT_REVENUE", 0m, 10m))).Should().Be("MISC");
        await k.Ok(k.Patch($"{Types}/{sale}/activate", null), "activate SALE");
        (await TypeOf(FinDocType.SalesInvoice, ("DEFAULT_RECEIVABLE", 10m, 0m), ("DEFAULT_REVENUE", 0m, 10m))).Should().Be("SALE");
    }

    [Fact]
    public async Task A44_update_and_void_mark_the_original_Reversed()
    {
        var o = await _root.NewJeOrgAsync("GWU");
        var id = Guid.NewGuid();
        await _f.PostDirectAsync(o.OrgId, Fp.Synthetic(FinDocType.SalesInvoice, 500m, id));
        var update = await _f.PostDirectAsync(o.OrgId, Fp.Synthetic(FinDocType.SalesInvoice, 800m, id));
        update.Status.Should().Be(PostingStatus.Success, update.Describe());

        var uuids = await _f.JournalUuidsAsync(o.OrgId, id);
        uuids.Should().HaveCount(3, "original, its reversal, the new journal (A44 D-13)");
        var details = new List<JsonElement>();
        foreach (var u in uuids) details.Add(await o.K.EntryAsync(u));
        var original = details[0];
        original.S("status").Should().Be("Reversed", "D-4: an A44 update marks the original Reversed");
        original.P("reversedByEntry").G("uuid").Should().Be(details[1].G("uuid"));
        details[1].P("reversesEntry").G("uuid").Should().Be(original.G("uuid"));
        details[2].S("status").Should().Be("Posted");
        details.Should().OnlyContain(d => d.D("totalDebit") == d.D("totalCredit"));

        var v = await _f.VoidDirectAsync(o.OrgId, new VoidRequest
        {
            SourceDocumentId = id, SourceModule = PostingSourceModules.Finance, DocumentType = FinDocType.SalesInvoice,
            SourceDocumentNumber = "QA-VOID", Reason = "A47 QA void"
        });
        v.Status.Should().Be(PostingStatus.Success, v.Describe());
        var latest = await o.K.EntryAsync(uuids[2]);
        latest.S("status").Should().Be("Reversed");
        latest.IsNull("reversedByEntry").Should().BeFalse();

        // the trial balance nets the document to zero (Reversed lines still count)
        var tb = await o.K.TrialBalanceAsync(new DateTime(Y, 1, 1), new DateTime(Y, 12, 31), closing: true);
        var ar = tb.TbRow(o.Code("DEFAULT_RECEIVABLE"));
        (ar.ValueKind == JsonValueKind.Undefined ? 0m : ar.D("balance")).Should().Be(0m);
    }
}
