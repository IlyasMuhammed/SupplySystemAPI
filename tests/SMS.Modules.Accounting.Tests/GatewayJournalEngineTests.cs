using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Posting;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Finance.Posting;
using Xunit;
using static SMS.Modules.Accounting.Tests.PostingHarness;

namespace SMS.Modules.Accounting.Tests;

/// <summary>
/// A47 GW (C11, tasks 402–414) — the A44 internal GL target writing through the journal engine: the D-12 resolver for all 20
/// FinDocTypes (CASH vs BANK), per-type numbers, the document partner on AR/AP lines (D-13), update / void as engine reversals
/// that mark the original Reversed and auto-reconcile (D-4, BR-RV-008), drafts, and the engine refusals mapped to the A44 codes (task 411).
/// </summary>
public sealed class GatewayJournalEngineTests
{
    private static readonly Guid Customer = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static FinancialTransaction Doc(FinDocType type, Guid? id = null, params (string Role, decimal Dr, decimal Cr)[] lines) => new()
    {
        DocumentType = type, SourceDocumentId = id ?? Guid.NewGuid(), SourceModule = PostingSourceModules.Finance, SourceDocumentNumber = $"{type}-1",
        TransactionDate = new DateTime(2026, 10, 15), CurrencyCode = "PKR",
        Lines = lines.Select((l, i) => L(i + 1, l.Role, l.Dr, l.Cr)).ToList(),
    };

    private static async Task<JournalEntry> JournalOfAsync(PostingHarness h, Guid source) =>
        (await h.JournalsAsync()).Where(j => j.SourceDocumentUuid == source).OrderBy(j => j.Id).Last();

    private static async Task<string> TypeCodeAsync(PostingHarness h, JournalEntry j) =>
        (await h.Db.JournalTypes.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == j.JournalTypeId)).Code;

    // ── D-12 resolver ──────────────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<FinDocType, string> AllDocTypes()
    {
        var data = new TheoryData<FinDocType, string>();
        foreach (var t in Enum.GetValues<FinDocType>())
            data.Add(t, t switch
            {
                FinDocType.SalesInvoice or FinDocType.CreditNote or FinDocType.SalesReturn or FinDocType.POSSale or FinDocType.POSReturn
                    or FinDocType.SalesReceipt or FinDocType.RefundReceipt => SystemJournalTypes.Sale,
                FinDocType.PurchaseBill or FinDocType.VendorCredit or FinDocType.PurchaseReturn or FinDocType.Expense => SystemJournalTypes.Purchase,
                FinDocType.Payment or FinDocType.BillPayment or FinDocType.DepositSlip or FinDocType.BankFee => SystemJournalTypes.Bank,
                FinDocType.OpeningBalance => SystemJournalTypes.Opening,
                _ => SystemJournalTypes.Misc,
            });
        return data;
    }

    [Fact]
    public void Resolver_table_covers_all_20_FinDocTypes()
    {
        Enum.GetValues<FinDocType>().Should().HaveCount(20);
        AllDocTypes().Should().HaveCount(20);
    }

    [Theory]
    [MemberData(nameof(AllDocTypes))]
    public async Task Every_FinDocType_gets_its_D12_journal_type_and_per_type_number(FinDocType type, string expected)
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        // Bank line (no DEFAULT_CASH): money documents go to BANK.
        var tx = Doc(type, null, (DefaultKeys.Bank, 25m, 0m), (DefaultKeys.Revenue, 0m, 25m));

        (await h.Gateway.PostAsync(tx)).Status.Should().Be(PostingStatus.Success);

        var j = await JournalOfAsync(h, tx.SourceDocumentId);
        (await TypeCodeAsync(h, j)).Should().Be(expected);
        j.EntryType.Should().Be(JournalEntryTypes.For(type.ToString()));
        var prefix = SystemJournalTypes.Definitions.Single(d => d.Code == expected).SequencePrefix;
        j.EntryNumber.Should().Be($"{prefix}/2026/0001", "C11 — the engine's per-type sequence, not JE-YYYY-NNNNN");
        j.Status.Should().Be(JournalStatuses.Posted);
        j.IsOpeningEntry.Should().Be(type == FinDocType.OpeningBalance);
        j.FiscalPeriodId.Should().NotBeNull();
        (await h.PostingAsync(tx.IdempotencyKey)).Targets.Single().InternalId.Should().Be(j.EntryNumber);
    }

    [Theory]
    [InlineData(FinDocType.Payment)]
    [InlineData(FinDocType.BillPayment)]
    [InlineData(FinDocType.DepositSlip)]
    [InlineData(FinDocType.BankFee)]
    public async Task Money_documents_on_DEFAULT_CASH_go_to_CASH_else_BANK(FinDocType type)
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var cash = Doc(type, null, (DefaultKeys.Cash, 10m, 0m), (DefaultKeys.Receivable, 0m, 10m));
        var bank = Doc(type, null, (DefaultKeys.Bank, 10m, 0m), (DefaultKeys.Receivable, 0m, 10m));
        await h.Gateway.PostAsync(cash);
        await h.Gateway.PostAsync(bank);

        var cj = await JournalOfAsync(h, cash.SourceDocumentId);
        var bj = await JournalOfAsync(h, bank.SourceDocumentId);
        ((await TypeCodeAsync(h, cj)), cj.EntryNumber).Should().Be((SystemJournalTypes.Cash, "CASH/2026/0001"));
        ((await TypeCodeAsync(h, bj)), bj.EntryNumber).Should().Be((SystemJournalTypes.Bank, "BANK/2026/0001"));
    }

    [Fact]
    public async Task Explicit_cash_code_counts_as_DEFAULT_CASH_and_a_deactivated_type_falls_back_to_MISC()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var explicitCash = new FinancialTransaction
        {
            DocumentType = FinDocType.Payment, SourceDocumentId = Guid.NewGuid(), SourceModule = "finance", SourceDocumentNumber = "R-1",
            TransactionDate = new DateTime(2026, 10, 15), CurrencyCode = "PKR",
            Lines = [new FinancialTransactionLine { LineNumber = 1, AccountCode = "1111", Debit = 5m }, L(2, DefaultKeys.Receivable, 0, 5m)],
        };
        await h.Gateway.PostAsync(explicitCash);
        (await TypeCodeAsync(h, await JournalOfAsync(h, explicitCash.SourceDocumentId))).Should().Be(SystemJournalTypes.Cash);

        var sale = await h.Db.JournalTypes.IgnoreQueryFilters().SingleAsync(t => t.OrganizationId == h.OrgA && t.Code == SystemJournalTypes.Sale);
        sale.IsActive = false;
        await h.Db.SaveChangesAsync();
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));
        var j = await JournalOfAsync(h, id);
        ((await TypeCodeAsync(h, j)), j.EntryNumber).Should().Be((SystemJournalTypes.Misc, "JV/2026/0001"), "D-12: never refused for its type");
    }

    // ── numbers, partner, currency ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gateway_journals_number_per_type_and_carry_the_partner_on_the_receivable_line()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(a, number: "SI-A"));
        await h.Gateway.PostAsync(Invoice(b, number: "SI-B"));

        var ja = await JournalOfAsync(h, a);
        var jb = await JournalOfAsync(h, b);
        (ja.EntryNumber, jb.EntryNumber).Should().Be(("SALE/2026/0001", "SALE/2026/0002"));
        var ar = ja.Lines.Single(l => l.DebitAmount == 118m);
        (ar.PartnerUuid, ar.PartnerType, ar.PartnerName).Should().Be((Customer, JournalPartnerTypes.Customer, "Acme"));
        ja.Lines.Where(l => l != ar).Should().OnlyContain(l => l.PartnerUuid == null, "D-13 — revenue/tax lines have no partner");
        ja.Lines.Should().OnlyContain(l => l.AmountResidual == l.DebitAmount - l.CreditAmount && !l.Reconciled);
        (ja.LineCount, ja.DocumentDate, ja.CurrencyCode, ja.ExchangeRate).Should().Be((3, new DateOnly(2026, 10, 15), "PKR", (decimal?)null));
    }

    [Fact]
    public async Task A_line_partner_wins_and_a_vendor_bill_marks_the_payable_line()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var vendor = Guid.NewGuid();
        var lineParty = Guid.NewGuid();
        var bill = new FinancialTransaction
        {
            DocumentType = FinDocType.PurchaseBill, SourceDocumentId = Guid.NewGuid(), SourceModule = "suppliers", SourceDocumentNumber = "BILL-9",
            TransactionDate = new DateTime(2026, 10, 10), CurrencyCode = "PKR", PartnerId = vendor, PartnerType = PartnerType.Vendor, PartnerName = "Vend",
            Lines = [new FinancialTransactionLine { LineNumber = 1, AccountRole = DefaultKeys.Cogs, Debit = 70m, PartnerId = lineParty, CostCenter = "CC1", TaxCode = "GST" },
                     L(2, DefaultKeys.Payable, 0, 70m)],
        };
        await h.Gateway.PostAsync(bill);
        var j = await JournalOfAsync(h, bill.SourceDocumentId);
        j.EntryNumber.Should().Be("PURCH/2026/0001");
        var cogs = j.Lines.Single(l => l.LineNumber == 1);
        (cogs.PartnerUuid, cogs.PartnerType, cogs.CostCenter, cogs.TaxCode).Should().Be((lineParty, JournalPartnerTypes.Vendor, "CC1", "GST"));
        j.Lines.Single(l => l.LineNumber == 2).PartnerUuid.Should().Be(vendor);
    }

    [Fact]
    public async Task Foreign_document_keeps_its_currency_and_rate_on_the_header_and_lines_base_amounts_unchanged()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var tx = new FinancialTransaction
        {
            DocumentType = FinDocType.SalesInvoice, SourceDocumentId = Guid.NewGuid(), SourceModule = "finance", SourceDocumentNumber = "SI-USD",
            TransactionDate = new DateTime(2026, 10, 15), CurrencyCode = "USD", ExchangeRate = 1.5m,
            Lines = [L(1, DefaultKeys.Receivable, 10m, 0), L(2, DefaultKeys.Revenue, 0, 3.33m), L(3, DefaultKeys.Revenue, 0, 3.33m), L(4, DefaultKeys.TaxPayable, 0, 3.34m)],
        };
        await h.Gateway.PostAsync(tx);
        var j = await JournalOfAsync(h, tx.SourceDocumentId);
        (j.CurrencyCode, j.ExchangeRate, j.TotalDebit, j.TotalCredit).Should().Be(("USD", 1.5m, 15.01m, 15.01m));
        var ar = j.Lines.Single(l => l.LineNumber == 1);
        (ar.DebitAmount, ar.AmountCurrency, ar.CurrencyCode, ar.ExchangeRate).Should().Be((15m, 10m, "USD", 1.5m));
        var rounding = j.Lines.Single(l => l.Memo == "Rounding difference");
        (rounding.DebitAmount, rounding.CurrencyCode, rounding.ExchangeRate).Should().Be((0.01m, "PKR", (decimal?)null));
    }

    // ── update / void through the engine (D-4, BR-RV-008) ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_reverses_through_the_engine_marks_the_original_Reversed_and_auto_reconciles_per_account()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));
        (await h.Gateway.PostAsync(Invoice(id, net: 200m, tax: 36m))).Status.Should().Be(PostingStatus.Success);

        var journals = await h.JournalsAsync();
        journals.Should().HaveCount(3);
        var (original, reversal, current) = (journals[0], journals[1], journals[2]);
        (original.EntryNumber, reversal.EntryNumber, current.EntryNumber).Should().Be(("SALE/2026/0001", "SALE/2026/0002", "SALE/2026/0003"));
        original.Status.Should().Be(JournalStatuses.Reversed);
        original.ReversedByEntryId.Should().Be(reversal.Id);
        original.ReversalReason.Should().Be("document changed");
        (reversal.ReversesEntryId, reversal.Status, reversal.IdempotencyKey).Should().Be((original.Id, JournalStatuses.Posted, Invoice(id).IdempotencyKey));
        reversal.Description.Should().Be("Reversal of SALE/2026/0001: document changed");
        reversal.ReversalReason.Should().Be("document changed");
        reversal.Lines.Single(l => l.CreditAmount == 118m).PartnerUuid.Should().Be(Customer, "the mirror copies the partner");
        current.Status.Should().Be(JournalStatuses.Posted);

        var groups = await h.Db.ReconcileGroups.IgnoreQueryFilters().AsNoTracking().OrderBy(g => g.AccountId).ToListAsync();
        groups.Should().HaveCount(3, "one Full group per account of the original (AR, revenue, tax)");
        groups.Should().OnlyContain(g => g.ReconcileType == ReconcileTypes.Full && g.GroupNumber.StartsWith("REC/"));
        (original.Lines.Concat(reversal.Lines)).Should().OnlyContain(l => l.Reconciled && l.AmountResidual == 0 && l.ClearingEntryId == reversal.Id
                                                                          && l.ClearingDate == reversal.EntryDate && l.ReconcileGroupId != null);
        current.Lines.Should().OnlyContain(l => !l.Reconciled && l.ReconcileGroupId == null, "the new version stays open");
        (await h.BalanceAsync("1113")).Should().Be(236m);
        (await h.PostingAsync(Invoice(id).IdempotencyKey)).Targets.Single().InternalId.Should().Be("SALE/2026/0003");
    }

    [Fact]
    public async Task Void_reverses_under_the_void_key_marks_Reversed_reconciles_and_is_idempotent()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));

        var v = await h.Gateway.VoidAsync(VoidOf(id));
        v.TargetResults.Single().InternalId.Should().Be("SALE/2026/0002");
        var journals = await h.JournalsAsync();
        journals.Should().HaveCount(2);
        (journals[0].Status, journals[0].ReversedByEntryId).Should().Be((JournalStatuses.Reversed, journals[1].Id));
        (journals[1].IdempotencyKey, journals[1].EntryDate, journals[1].ReversalReason).Should().Be((Invoice(id).IdempotencyKey + ":void", new DateOnly(2026, 10, 16), "cancelled"));
        journals.SelectMany(j => j.Lines).Should().OnlyContain(l => l.Reconciled);
        (await h.Db.ReconcileGroups.IgnoreQueryFilters().CountAsync()).Should().Be(3);

        (await h.Gateway.VoidAsync(VoidOf(id))).TargetResults.Single().InternalId.Should().Be("SALE/2026/0002");
        (await h.JournalsAsync()).Should().HaveCount(2);
        (await h.Db.ReconcileGroups.IgnoreQueryFilters().CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task An_account_line_reconciled_by_hand_is_left_alone_by_the_auto_reconcile()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));
        // Someone matched the AR line partly (a partial payment group): it keeps its group, its mirror stays open.
        var ar = await h.Db.JournalEntryLines.IgnoreQueryFilters().SingleAsync(l => l.DebitAmount == 118m);
        var group = new ReconcileGroup { OrganizationId = h.OrgA, GroupNumber = "REC/2026/0900", ReconcileType = ReconcileTypes.Partial, AccountId = ar.AccountId, Amount = 18m, ReconciledAt = DateTime.UtcNow };
        h.Db.ReconcileGroups.Add(group);
        ar.ReconcileGroup = group;
        ar.AmountResidual = 100m;
        await h.Db.SaveChangesAsync();
        h.NewScope();

        await h.Gateway.VoidAsync(VoidOf(id));

        var journals = await h.JournalsAsync();
        journals[0].Status.Should().Be(JournalStatuses.Reversed);
        var mirrorAr = journals[1].Lines.Single(l => l.AccountId == ar.AccountId);
        (mirrorAr.Reconciled, mirrorAr.ReconcileGroupId, mirrorAr.AmountResidual).Should().Be((false, (int?)null, -118m));
        journals[0].Lines.Single(l => l.AccountId == ar.AccountId).ReconcileGroupId.Should().Be(group.Id);
        (await h.Db.ReconcileGroups.IgnoreQueryFilters().CountAsync()).Should().Be(3, "the partial one + revenue + tax");
    }

    [Fact]
    public async Task A_gateway_journal_reversed_by_hand_is_not_reversed_again_by_the_document_void()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));
        var original = await JournalOfAsync(h, id);
        await h.Get<IJournalReversalService>().ReverseAsync(AccountingCaller.System(h.OrgA), original.Uuid, new ReverseJournalEntryRequest { Reason = "by hand" });
        h.NewScope();

        var v = await h.Gateway.VoidAsync(VoidOf(id));

        v.TargetResults.Single().Error.Should().StartWith(InternalGlPostingTarget.NotPosted);
        (await h.JournalsAsync()).Should().HaveCount(2, "the manual reversal already cancelled it");
        (await h.BalanceAsync("1113")).Should().Be(0m);
    }

    [Fact]
    public async Task Drafts_when_AutoPostToGL_is_off_are_engine_drafts_replaced_on_update_and_deleted_on_void()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync(autoPost: false);
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));
        var draft = (await h.JournalsAsync()).Single();
        (draft.Status, draft.EntryNumber, draft.PostedAt).Should().Be((JournalStatuses.Draft, "SALE/2026/0001", (DateTime?)null));

        await h.Gateway.PostAsync(Invoice(id, net: 50m, tax: 9m));
        var replaced = (await h.JournalsAsync()).Single();
        (replaced.Status, replaced.TotalDebit, replaced.EntryNumber).Should().Be((JournalStatuses.Draft, 59m, "SALE/2026/0002"));
        (await h.Db.ReconcileGroups.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await h.BalanceAsync("1113")).Should().Be(0m);
    }

    // ── task 411 — engine refusals ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_engine_refusal_is_Failed_with_the_A44_code_writes_nothing_and_never_blocks_the_external_target()
    {
        using var h = new PostingHarness();
        await h.SetupGlAsync();
        var id = Guid.NewGuid();
        // "US" passes the A44 checks (a rate is given) but the engine refuses the currency code (JE-012).
        var tx = new FinancialTransaction
        {
            DocumentType = FinDocType.SalesInvoice, SourceDocumentId = id, SourceModule = "finance", SourceDocumentNumber = "SI-X",
            TransactionDate = new DateTime(2026, 10, 15), CurrencyCode = "US", ExchangeRate = 2m,
            Lines = [L(1, DefaultKeys.Receivable, 10m, 0), L(2, DefaultKeys.Revenue, 0, 10m)],
        };

        var result = await h.Gateway.PostAsync(tx);

        result.Status.Should().Be(PostingStatus.PartialFailure);
        var gl = result.TargetResults.Single(r => r.TargetCode == PostingTargetCodes.InternalGl);
        gl.Status.Should().Be(TargetPostingStatus.Failed);
        gl.Error.Should().Be("EXCHANGE_RATE_MISSING: JE-012 Currency must be a 3-letter currency code");
        result.TargetResults.Single(r => r.TargetCode == PostingTargetCodes.QuickBooks).Status.Should().Be(TargetPostingStatus.Queued);
        (await h.JournalsAsync()).Should().BeEmpty();
        (await h.Db.JournalEntrySequences.IgnoreQueryFilters().AnyAsync(s => s.CurrentSequence > 0)).Should().BeFalse("a refused post consumes no number");
        var row = (await h.PostingAsync(tx.IdempotencyKey)).Targets.Single(t => t.TargetCode == PostingTargetCodes.InternalGl);
        (row.Status, row.NextAttemptAt is not null).Should().Be((PostingTargetStatuses.Failed, true), "retried like any failure");
    }

    [Theory]
    [InlineData(JournalErrorCodes.NoPeriod, JournalMessages.NoPeriod, "PERIOD_CLOSED: 2026-10-15 is not in an open fiscal period. (JE-005 No fiscal period covers this date)")]
    [InlineData(JournalErrorCodes.PeriodLocked, JournalMessages.PeriodLocked, "PERIOD_CLOSED: 2026-10-15 is not in an open fiscal period. (JE-007 Period is locked)")]
    [InlineData(JournalErrorCodes.AdjustmentOnly, JournalMessages.AdjustmentOnly, "PERIOD_CLOSED: 2026-10-15 is not in an open fiscal period. (JE-008 Period accepts only adjustment entries)")]
    [InlineData(JournalErrorCodes.PeriodClosed, JournalMessages.YearClosed, "PERIOD_CLOSED: 2026-10-15 is not in an open fiscal period. (JE-006 Fiscal year is closed)")]
    [InlineData(JournalErrorCodes.AccountInactive, JournalMessages.AccountInactive, "ACCOUNT_NOT_FOUND: JE-010 Account is inactive")]
    [InlineData(JournalErrorCodes.AccountNotPostable, JournalMessages.AccountNotPostable, "ACCOUNT_NOT_FOUND: JE-009 Account is not postable")]
    [InlineData(JournalErrorCodes.NotBalanced, JournalMessages.NotBalanced, "JOURNAL_INVALID: JE-001 Entry is not balanced")]
    [InlineData(JournalErrorCodes.JournalTypeInvalid, JournalMessages.JournalTypeInactive, "JOURNAL_INVALID: JE-013 Journal type is inactive")]
    public void Engine_codes_map_to_the_A44_target_codes(string code, string message, string expected)
    {
        InternalGlPostingTarget.RefusalText(new JournalValidationException(code, message), new DateOnly(2026, 10, 15)).Should().Be(expected);
    }

    [Fact]
    public void A_line_error_keeps_its_line_number()
    {
        var e = new JournalValidationException([new JournalError(JournalErrorCodes.AccountInactive, JournalMessages.AccountInactive, 2),
                                                new JournalError(JournalErrorCodes.NoAmount, JournalMessages.NoAmount, 3)]);
        InternalGlPostingTarget.RefusalText(e, new DateOnly(2026, 1, 1))
            .Should().Be("ACCOUNT_NOT_FOUND: JE-010 Line 2: Account is inactive; Line 3: Line must have a debit or a credit amount");
    }

    [Fact]
    public async Task A_closed_period_is_still_PERIOD_CLOSED_before_the_engine_and_consumes_no_number()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        await h.SetPeriodStatusAsync(10, PeriodStatuses.AdjustmentOnly);
        var id = Guid.NewGuid();
        (await h.Gateway.PostAsync(Invoice(id))).TargetResults.Single().Error.Should().StartWith("PERIOD_CLOSED: 2026-10-15");
        await h.SetPeriodStatusAsync(10, PeriodStatuses.Open);
        h.NewScope();
        await h.Get<FinancialPostingGateway>().RetryAsync((await h.PostingAsync(Invoice(id).IdempotencyKey)).Id, PostingTargetCodes.InternalGl, true);
        (await JournalOfAsync(h, id)).EntryNumber.Should().Be("SALE/2026/0001");
    }
}
