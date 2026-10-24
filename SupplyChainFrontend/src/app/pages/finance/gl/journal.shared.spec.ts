import { HttpErrorResponse } from '@angular/common/http';
import {
  EntryDraft, LineDraft, actionBlockedByDrafts, baseTotals, csvCell, balancingAmount, entryProblem, entrySourceRoute, entryTotals, finDocTypeOf, journalErrorCode,
  journalErrors, journalStatusTone, lineProblem, numberPreview, parens, partnerTypeFor, periodCloseActions, reconcileProblem, selectionTotals,
  sourceLabel, tbTotals, trialBalanceCsv, typeProblem, yearEndBlockers
} from './journal.shared';
import { TrialBalanceReport } from '../../../services/gl-journal.service';

const line = (over: Partial<LineDraft> = {}): LineDraft => ({
  key: 1, accountUuid: 'a', description: '', debit: null, credit: null, partnerUuid: null, partnerName: null, partnerType: null, ...over
});
const draft = (lines: LineDraft[], over: Partial<EntryDraft> = {}): EntryDraft => ({
  journalTypeUuid: 't', entryDate: new Date(2026, 9, 20), documentDate: null, reference: '', description: '', currencyCode: null,
  exchangeRate: null, lines, ...over
});

describe('journal.shared (A47 rules shown in the UI)', () => {
  it('status tones: Draft blue, Posted green, Reversed red (task 254)', () => {
    expect(journalStatusTone('Draft')).toBe('in');
    expect(journalStatusTone('Posted')).toBe('ok');
    expect(journalStatusTone('Reversed')).toBe('er');
    expect(journalStatusTone('x')).toBe('');
  });

  it('negative amounts in parentheses (task 355)', () => {
    expect(parens(-24000)).toBe('(24,000.00)');
    expect(parens(5000)).toBe('5,000.00');
    expect(parens(0)).toBe('0.00');
    expect(parens(null)).toBe('0.00');
  });

  it('totals and balance (tasks 258–259, BR-JE-001)', () => {
    const t = entryTotals([line({ debit: 100.005 }), line({ credit: 100 })]);
    expect(t.debit).toBe(100.01);
    expect(t.balanced).toBeTrue();
    expect(entryTotals([line({ debit: 100 }), line({ credit: 90 })]).difference).toBe(10);
    expect(entryTotals([line(), line()]).balanced).toBeFalse();
    expect(balancingAmount([line({ debit: 100 }), line({ credit: 30 }), line()], 2)).toEqual({ debit: null, credit: 70 });
    expect(balancingAmount([line({ credit: 50 }), line()], 1)).toEqual({ debit: 50, credit: null });
  });

  it('line rules: account, both sides, negative, none, partner (BR-JE-003 / BR-JE-011)', () => {
    expect(lineProblem(line({ accountUuid: null, debit: 1 }), false)).toBe('Choose the account.');
    expect(lineProblem(line({ debit: 1, credit: 1 }), false)).toContain('not both');
    expect(lineProblem(line({ debit: -1 }), false)).toContain('negative');
    expect(lineProblem(line(), false)).toContain('debit or a credit');
    expect(lineProblem(line({ debit: 1 }), true)).toContain('partner');
    expect(lineProblem(line({ debit: 1, partnerUuid: 'p' }), true)).toBeNull();
  });

  it('entry rules: type, date, rate for a foreign currency, two lines, balanced', () => {
    const ok = [line({ debit: 10 }), line({ credit: 10 })];
    expect(entryProblem(draft(ok), false, 'PKR')).toBeNull();
    expect(entryProblem(draft(ok, { journalTypeUuid: null }), false, 'PKR')).toBe('Choose the journal type.');
    expect(entryProblem(draft(ok, { entryDate: null }), false, 'PKR')).toBe('Choose the entry date.');
    expect(entryProblem(draft(ok, { currencyCode: 'USD' }), false, 'PKR')).toContain('exchange rate');
    expect(entryProblem(draft(ok, { currencyCode: 'USD', exchangeRate: 280 }), false, 'PKR')).toBeNull();
    expect(entryProblem(draft([line({ debit: 10 }), { ...line(), accountUuid: null }]), false, 'PKR')).toBe('An entry needs at least two lines.');
    expect(entryProblem(draft([line({ debit: 10 }), line({ credit: 9 })]), false, 'PKR')).toBe('Debits and credits differ by 1.00.');
    expect(entryProblem(draft([line({ debit: 10 }), line({ credit: 10, accountUuid: null })]), false, 'PKR')).toBe('Line 2: Choose the account.');
  });

  it('partner type by journal kind (D-13)', () => {
    expect(partnerTypeFor('Sale')).toBe('Customer');
    expect(partnerTypeFor('Purchase')).toBe('Vendor');
    expect(partnerTypeFor('Bank')).toBeNull();
  });

  it('journal type rules (§1): code / prefix format, unique code, one Opening type', () => {
    const d = { code: 'PROJ', name: 'Project', type: 'Miscellaneous', sequencePrefix: 'PROJ', defaultDebitAccountUuid: null, defaultCreditAccountUuid: null,
      requiresPartner: false, autoPost: false };
    const others = [{ uuid: 'o', code: 'OPEN', type: 'Opening' }];
    expect(typeProblem(d, others, null)).toBeNull();
    expect(typeProblem({ ...d, code: 'pr-1' }, others, null)).toContain('upper-case');
    expect(typeProblem({ ...d, code: 'ABCDEFGHIJK' }, others, null)).toContain('10');
    expect(typeProblem({ ...d, code: 'OPEN' }, others, null)).toBe('Journal type OPEN already exists.');
    expect(typeProblem({ ...d, code: 'OPEN' }, others, 'o')).toBeNull();
    expect(typeProblem({ ...d, type: 'Opening' }, others, null)).toBe('There can be only one opening journal type.');
    expect(typeProblem({ ...d, sequencePrefix: '' }, others, null)).toBe('Enter the number prefix.');
    expect(numberPreview('jv', 2026)).toBe('JV/2026/0001');
  });

  it('source document soft link (task 263): EntryType upper snake back to the A44 page', () => {
    expect(finDocTypeOf('SALES_INVOICE')).toBe('SalesInvoice');
    expect(finDocTypeOf('POS_SALE')).toBe('POSSale');
    expect(finDocTypeOf('BILL_PAYMENT')).toBe('BillPayment');
    expect(finDocTypeOf('MANUAL_JOURNAL')).toBe('ManualJournal');
    expect(finDocTypeOf('YEAR_END_CLOSE')).toBeNull();
    expect(entrySourceRoute({ entryType: 'SALES_INVOICE', sourceDocumentUuid: 'i1' })).toEqual(['/portal/pages/finance/sales-invoices', 'i1']);
    expect(entrySourceRoute({ entryType: 'MANUAL_JOURNAL', sourceDocumentUuid: null })).toBeNull();
    expect(sourceLabel({ entryType: 'MANUAL_JOURNAL', isManual: true })).toBe('Manual entry');
    expect(sourceLabel({ entryType: 'PURCHASE_BILL' })).toBe('Bill');
    expect(sourceLabel({ entryType: 'YEAR_END_CLOSE' })).toBe('Year end close');
  });

  it('reconciliation selection totals and refusals (task 318, BR-RC-003, RC-004)', () => {
    const t = selectionTotals([{ amountResidual: 1000 }, { amountResidual: -600 }]);
    expect(t).toEqual({ count: 2, debit: 1000, credit: 600, residual: 400, full: false });
    expect(selectionTotals([{ amountResidual: 500 }, { amountResidual: -500 }]).full).toBeTrue();
    expect(reconcileProblem([{ amountResidual: 5 }])).toContain('two');
    expect(reconcileProblem([{ amountResidual: 5 }, { amountResidual: 6 }])).toContain('debit and credit');
    expect(reconcileProblem([{ amountResidual: 5, reconcileGroupUuid: 'g' }, { amountResidual: -5 }])).toContain('unreconcile');
    expect(reconcileProblem([{ amountResidual: 5 }, { amountResidual: -5 }])).toBeNull();
  });

  it('trial balance totals: the server totals win; balanced flag; CSV', () => {
    const r: TrialBalanceReport = {
      fromDate: '2026-10-01', toDate: '2026-10-31', totalDebit: 150, totalCredit: 150, isBalanced: true,
      lines: [
        { account: { uuid: 'a', accountCode: '1111', accountName: 'Cash, petty' }, rootType: 'Asset', openingDebit: 50, openingCredit: 0,
          periodDebit: 100, periodCredit: 0, closingDebit: 150, closingCredit: 0, balance: 150 },
        { account: { uuid: 'b', accountCode: '4101', accountName: 'Sales' }, rootType: 'Revenue', openingDebit: 0, openingCredit: 50,
          periodDebit: 0, periodCredit: 100, closingDebit: 0, closingCredit: 150, balance: -150 }
      ]
    };
    const t = tbTotals(r);
    expect([t.debit, t.credit, t.balance, t.balanced]).toEqual([100, 100, 0, true]);
    expect(tbTotals({ ...r, isBalanced: false }).balanced).toBeFalse();
    expect(tbTotals({ ...r, isBalanced: null, totalCredit: 140 }).balanced).toBeFalse();
    const csv = trialBalanceCsv(r).split('\r\n');
    expect(csv[0]).toContain('Account code');
    expect(csv[1]).toBe('1111,"Cash, petty",Asset,50,0,100,0,150,0');
    expect(csv[3]).toContain('Total');
  });

  it('REV-18: saved-entry base totals are balanced only when the 2-dp totals are equal', () => {
    expect(baseTotals([{ debit: 100.004, credit: 0 }, { debit: 0, credit: 100 }]).balanced).toBeTrue();
    expect(baseTotals([{ debit: 100.01, credit: 0 }, { debit: 0, credit: 100 }]).balanced).toBeFalse();
    expect(baseTotals([{ debit: 0.1, credit: 0 }, { debit: 0.2, credit: 0 }, { debit: 0, credit: 0.3 }])).toEqual({ debit: 0.3, credit: 0.3, balanced: true });
  });

  it('REV-15: CSV text cells that look like formulas are prefixed with \'; numbers are not', () => {
    expect(csvCell('=HYPERLINK("x")')).toBe(`"'=HYPERLINK(""x"")"`);
    expect(csvCell('+1')).toBe("'+1");
    expect(csvCell('-cash')).toBe("'-cash");
    expect(csvCell('@SUM')).toBe("'@SUM");
    expect(csvCell('\tx')).toBe("'\tx");
    expect(csvCell(-24000)).toBe('-24000');
    expect(csvCell('Sales')).toBe('Sales');
  });

  it('period actions (D-11), server allowedActions win; drafts block a lock (PC-001)', () => {
    expect(periodCloseActions('Open')).toEqual(['Lock', 'AdjustmentOnly']);
    expect(periodCloseActions('AdjustmentOnly')).toEqual(['Lock', 'Reopen']);
    expect(periodCloseActions('Locked')).toEqual(['Reopen', 'FinalClose']);
    expect(periodCloseActions('Closed')).toEqual([]);
    expect(periodCloseActions('Locked', ['Reopen'])).toEqual(['Reopen']);
    expect(actionBlockedByDrafts('Lock', 2)).toBeTrue();
    expect(actionBlockedByDrafts('Reopen', 2)).toBeFalse();
    expect(yearEndBlockers([
      { periodNumber: 1, periodName: 'Jan', status: 'Locked', draftEntryCount: 0 },
      { periodNumber: 2, periodName: 'Feb', status: 'Open', draftEntryCount: 3 },
      { periodNumber: 13, periodName: 'Adj', status: 'Open', draftEntryCount: 0 }
    ])).toEqual(['Lock periods first: Feb.', 'Post or delete the drafts in Feb.']);
  });

  it('server errors (contract §0): every error, line errors prefixed, the code', () => {
    const err = new HttpErrorResponse({ status: 400, error: { success: false, message: 'Line 2: Account is inactive', result: {
      errorCode: 'JE-010', errors: [{ errorCode: 'JE-010', message: 'Account is inactive', lineNumber: 2 }, { errorCode: 'JE-001', message: 'Entry is not balanced' }] } } });
    expect(journalErrors(err)).toEqual(['Line 2: Account is inactive', 'Entry is not balanced']);
    expect(journalErrorCode(err)).toBe('JE-010');
    expect(journalErrors(new HttpErrorResponse({ status: 500 }))).toEqual([]);
  });
});
