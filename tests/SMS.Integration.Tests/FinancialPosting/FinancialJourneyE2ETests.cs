using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using SMS.Modules.Integration.Core.Providers;
using SMS.Shared.Integration.QuickBooks;
using Xunit;
using static SMS.Integration.Tests.FinancialPosting.Fp;

namespace SMS.Integration.Tests.FinancialPosting;

/// <summary>
/// A44 (QA) — the regression journey (register 0578): sale order → delivery → invoice issue → customer payment → QuickBooks
/// (fake Intuit) Invoice + a Payment linked to it, with the internal GL on (balanced journals, AR nets to zero, the posting log
/// shows both targets) and off (QuickBooks unchanged, no journal) — and a supplier bill → payment → BillPayment beside it.
/// Also probes D-11's immediate kick (a sale reaching QuickBooks without the 1-minute job).
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~FinancialJourneyE2ETests</c>.</para>
/// </summary>
public sealed class FinancialJourneyE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public FinancialJourneyE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A44");
    }

    private async Task<(FpOrg O, Guid Invoice, Guid Payment, decimal Total, Guid Bill, Guid SupplierPayment)> JourneyAsync(string prefix, bool gl)
    {
        var o = await _root.NewOrgAsync(prefix);
        var k = o.K;
        if (gl) await k.EnableGlAsync();
        o = await o.ConnectLiveAsync();
        var (bank, _) = await k.BankAccountsAsync();
        await k.Ok(k.SetSyncControl("Payment", enabled: true), "payments on");
        await k.Ok(k.SetSyncControl("PurchaseBill", enabled: true), "bills on");
        await k.Ok(k.SetSyncControl("BillPayment", enabled: true), "bill payments on");
        await k.Ok(k.PutDefaults(("DepositToAccountRef.BANK_TRANSFER", bank), ("BankAccountRef.BANK_TRANSFER", bank)), "accounts");

        var gst = await k.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        var (_, vendor, customer, item) = await k.StockedWorldAsync(prefix, price: 250m, cost: 100m);
        var inv = await k.IssuedInvoiceAsync(customer, item, 4m, o.O.Pkr, gst);
        var total = inv.D("grandTotal");
        total.Should().Be(1170m);

        // D-11 probe — before any job runs, does the immediate kick deliver the invoice?
        var kicked = false;
        try
        {
            await SapKit.WaitForAsync(() => _f.MapOrNullAsync(o.OrgId, inv.G("uuid").ToString(), "SalesInvoice"), m => m?.State == "Synced",
                "immediate kick", seconds: 25); // a 2 s Hangfire Schedule is picked up by the delayed-job poller (≤ 15 s)
            kicked = true;
        }
        catch (TimeoutException) { }
        var kickType = typeof(SMS.Modules.Integration.Jobs.SyncOutboxJob).Assembly.GetType("SMS.Modules.Integration.Core.Sync.IOutboxKick");
        var kick = kickType is null ? null : _f.Services.GetService(kickType);
        var scheduled = kick?.GetType().GetProperty("Scheduled")?.GetValue(kick);
        Console.WriteLine($"PROBE D-11 immediate kick ({prefix}): invoice Synced without the outbox job = {kicked}; kick={kick?.GetType().Name} scheduled={scheduled}");

        var pay = await k.RecordPaymentAsync(customer, total, "BANK_TRANSFER", (inv.G("uuid"), total));
        var bill = await k.ApprovedBillAsync(vendor, 500m);
        var sp = await k.PostedSupplierPaymentAsync(vendor, bill, 500m, "BANK_TRANSFER");
        await _f.RunJobsAsync(6);
        return (o, inv.G("uuid"), pay.G("paymentUuid"), total, bill, sp);
    }

    private void ShouldBeInQuickBooks(Guid org, MapRow inv, MapRow pay, decimal total)
    {
        var qInv = _f.Remote(SyncKind.SalesInvoice, inv.RemoteId)!.Entity.Should().BeOfType<RemoteInvoice>().Subject;
        (qInv.Lines.Sum(l => l.Amount) - qInv.DiscountAmount).Should().Be(1000m, "4 × 250 net");
        var qPay = _f.Remote(SyncKind.Payment, pay.RemoteId)!.Entity.Should().BeOfType<RemotePayment>().Subject;
        qPay.TotalAmount.Should().Be(total);
        qPay.Lines.Should().ContainSingle().Which.Should().Match<RemotePaymentLine>(l => l.InvoiceId == inv.RemoteId && l.Amount == total,
            "the payment is linked to the invoice for its full total, so QuickBooks' invoice balance is 0");
        qPay.CustomerId.Should().Be(qInv.CustomerId);
    }

    [Fact]
    public async Task Sale_to_cash_reaches_QuickBooks_and_the_GL_balances()
    {
        var (o, invoice, payment, total, bill, sp) = await JourneyAsync("JGL", gl: true);
        var k = o.K;
        var c = await k.DefaultCodesAsync();

        var inv = await _f.MapAsync(o.OrgId, invoice, "SalesInvoice");
        var pay = await _f.MapAsync(o.OrgId, payment, "Payment");
        (inv.State, pay.State).Should().Be(("Synced", "Synced"), $"{inv} / {pay}");
        ShouldBeInQuickBooks(o.OrgId, inv, pay, total);
        var bp = await _f.MapAsync(o.OrgId, sp, "BillPayment");
        bp.State.Should().Be("Synced", bp.ToString());
        var billRemote = (await _f.MapAsync(o.OrgId, bill, "Bill")).RemoteId;
        _f.Remote(SyncKind.BillPayment, bp.RemoteId)!.Entity.Should().BeOfType<RemoteBillPayment>()
            .Which.Lines.Should().ContainSingle(l => l.BillId == billRemote);

        // GL: issue + payment + bill + bill payment, all balanced; AR and AP net to zero
        var all = await _f.AllJournalsAsync(o.OrgId);
        all.Select(j => j.Type).Should().BeEquivalentTo("SALES_INVOICE", "PAYMENT", "PURCHASE_BILL", "BILL_PAYMENT");
        all.Should().OnlyContain(j => j.Balanced && j.Status == "Posted", string.Join("\n", all));
        (await _f.NetAsync(o.OrgId, c["DEFAULT_RECEIVABLE"])).Should().Be(0m);
        (await _f.NetAsync(o.OrgId, c["DEFAULT_PAYABLE"])).Should().Be(0m);
        (await _f.NetAsync(o.OrgId, c["DEFAULT_REVENUE"])).Should().Be(-1000m);
        (await _f.NetAsync(o.OrgId, c["DEFAULT_TAX_PAYABLE"])).Should().Be(-170m);
        (await _f.NetAsync(o.OrgId, c["DEFAULT_BANK"])).Should().Be(total - 500m);

        // the cross-target posting log (D-12)
        foreach (var doc in new[] { invoice, payment, bill, sp })
        {
            var row = (await k.PostingRowsAsync(doc)).Should().ContainSingle($"posting row of {doc}").Subject;
            row.TargetStatus("internal_gl").Should().Be("Posted", J.Short(row));
            row.TargetStatus("quickbooks").Should().BeOneOf(new[] { "Queued", "Posted" }, J.Short(row));
        }

        // nothing is sent twice by another round of jobs or the hourly reconciliation (D-3)
        _f.Company.ResetCalls();
        await _f.RunReconciliationAsync();
        await _f.RunJobsAsync(3);
        _f.Company.Writes.Should().BeEmpty("unchanged documents are fingerprint no-ops");
        (await _f.AllJournalsAsync(o.OrgId)).Should().HaveCount(4, "the reconciliation re-push is a GL duplicate");
    }

    [Fact]
    public async Task With_the_GL_off_QuickBooks_gets_the_same_and_no_journal_is_written()
    {
        var (o, invoice, payment, total, _, sp) = await JourneyAsync("JQB", gl: false);
        var inv = await _f.MapAsync(o.OrgId, invoice, "SalesInvoice");
        var pay = await _f.MapAsync(o.OrgId, payment, "Payment");
        (inv.State, pay.State).Should().Be(("Synced", "Synced"), $"{inv} / {pay}");
        ShouldBeInQuickBooks(o.OrgId, inv, pay, total);
        (await _f.MapAsync(o.OrgId, sp, "BillPayment")).State.Should().Be("Synced");
        (await _f.AllJournalsAsync(o.OrgId)).Should().BeEmpty();
        var row = (await o.K.PostingRowsAsync(invoice)).Should().ContainSingle().Subject;
        row.TargetStatus("internal_gl").Should().Match(s => s == null || s == "Skipped");
    }
}
