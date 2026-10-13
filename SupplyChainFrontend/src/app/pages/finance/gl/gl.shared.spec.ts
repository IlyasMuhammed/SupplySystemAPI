import { SetupStatus } from '../../../services/accounting.service';
import {
  accountProblem, accountTypeOptions, allNodes, differs, flattenTree, money, nodeBalance, openingRowProblem, openingTotals, round2,
  stepDone, syncStatusLabel, unmetList, uploadErrors
} from './gl.shared';
import { sampleTree } from './gl.testing';

describe('GL shared (A42)', () => {
  it('flattenTree: collapsed shows the roots; expanding opens one level; children sorted by code', () => {
    const roots = sampleTree();
    expect(flattenTree(roots, new Set()).map(r => r.node.accountCode)).toEqual(['1', '2']);
    const open = new Set(['u1', 'u11', 'u111']);
    const rows = flattenTree(roots, open);
    expect(rows.map(r => r.node.accountCode)).toEqual(['1', '11', '111', '1111', '1112', '2']);
    expect(rows.map(r => r.depth)).toEqual([0, 1, 2, 3, 3, 0]);
    expect(rows[0].hasChildren).toBeTrue();
    expect(rows[3].hasChildren).toBeFalse();
    expect(rows[5].expanded).toBeFalse();
  });

  it('flattenTree: a search shows matches with their ancestors, expanded; the level filter cuts deeper levels', () => {
    const roots = sampleTree();
    expect(flattenTree(roots, new Set(), { search: 'meezan' }).map(r => r.node.accountCode)).toEqual(['1', '11', '111', '1112']);
    expect(flattenTree(roots, new Set(), { search: '2111' }).map(r => r.node.accountCode)).toEqual(['2', '21', '211', '2111']);
    expect(flattenTree(roots, new Set(), { search: 'zzz' })).toEqual([]);
    const all = new Set(allNodes(roots).map(n => n.uuid));
    const two = flattenTree(roots, all, { maxLevel: 2 });
    expect(two.map(r => r.node.accountCode)).toEqual(['1', '11', '2', '21']);
    expect(two[1].hasChildren).toBeFalse();
  });

  it('nodeBalance sums leaves; allNodes walks depth-first', () => {
    const roots = sampleTree();
    expect(nodeBalance(roots[0])).toBeCloseTo(750.5, 2);
    expect(allNodes(roots).length).toBe(9);
  });

  it('accountProblem mirrors the server: parent group, code format, root digit, duplicates, type per root', () => {
    const parent = { uuid: 'p', accountCode: '111', level: 3, rootType: 'Asset' as const, isGroup: true };
    const base = { parent, accountCode: '1113', accountName: 'HBL', accountType: null, description: '', externalCode: '', sortOrder: null };
    expect(accountProblem(base, ['1111', '1112'])).toBeNull();
    expect(accountProblem({ ...base, parent: null }, [])).toBe('Choose the parent account.');
    expect(accountProblem({ ...base, parent: { ...parent, level: 4, isGroup: false } }, [])).toContain('group account');
    expect(accountProblem({ ...base, accountCode: '' }, [])).toBe('Enter the account code.');
    expect(accountProblem({ ...base, accountCode: '11-3' }, [])).toContain('only letters and digits');
    expect(accountProblem({ ...base, accountCode: '2113' }, [])).toBe('Asset account codes must start with 1.');
    expect(accountProblem(base, ['1113'])).toBe('Account code 1113 already exists.');
    expect(accountProblem({ ...base, accountName: ' ' }, [])).toBe('Enter the account name.');
    expect(accountProblem({ ...base, accountType: 'Payable' }, [])).toBe('Account type Payable is not valid for Asset accounts.');
    expect(accountProblem({ ...base, accountType: 'Tax' }, [])).toBeNull();
    expect(accountProblem({ ...base, sortOrder: -1 }, [])).toContain('Sort order');
    // Editing skips the code / parent checks (immutable).
    expect(accountProblem({ ...base, accountCode: '', parent: null }, ['1113'], true)).toBeNull();
  });

  it('accountTypeOptions follows Appendix A (Tax on Asset and Liability only)', () => {
    expect(accountTypeOptions('Liability').map(o => o.value)).toEqual(['Payable', 'OtherCurrentLiability', 'LongTermLiability', 'Tax']);
    expect(accountTypeOptions('Equity').map(o => o.value)).toEqual(['Equity']);
    expect(accountTypeOptions('Expense').some(o => o.value === 'Tax')).toBeFalse();
    expect(accountTypeOptions(null)).toEqual([]);
    expect(accountTypeOptions('Asset').find(o => o.value === 'OtherCurrentAsset')!.label).toBe('Other current asset');
  });

  it('openingTotals: plug excluded; credit plug when debits exceed credits, debit plug otherwise', () => {
    const rows = [
      { debitAmount: 250000, creditAmount: 0, isPlug: false },
      { debitAmount: 1200000, creditAmount: 0, isPlug: false },
      { debitAmount: 0, creditAmount: 800000, isPlug: false },
      { debitAmount: 0, creditAmount: 500000, isPlug: false },
      { debitAmount: 999, creditAmount: 999, isPlug: true }
    ];
    expect(openingTotals(rows)).toEqual({ totalDebits: 1450000, totalCredits: 1300000, difference: 150000, plugSide: 'Credit', plugAmount: 150000 });
    const t = openingTotals([{ debitAmount: 0.1, creditAmount: 0, isPlug: false }, { debitAmount: 0.2, creditAmount: 0, isPlug: false },
      { debitAmount: 0, creditAmount: 0.5, isPlug: false }]);
    expect(t.difference).toBe(-0.2);
    expect(t.plugSide).toBe('Debit');
    expect(openingTotals([{ debitAmount: 0.1, creditAmount: 0, isPlug: false }, { debitAmount: 0, creditAmount: 0.1, isPlug: false }]).plugSide).toBeNull();
  });

  it('openingRowProblem: never both sides, never negative, the plug is not checked', () => {
    expect(openingRowProblem({ debitAmount: 5, creditAmount: 0, isPlug: false })).toBeNull();
    expect(openingRowProblem({ debitAmount: 5, creditAmount: 1, isPlug: false })).toBe('Cannot have both debit and credit amounts.');
    expect(openingRowProblem({ debitAmount: -1, creditAmount: 0, isPlug: false })).toBe('Amounts cannot be negative.');
    expect(openingRowProblem({ debitAmount: 5, creditAmount: 5, isPlug: true })).toBeNull();
  });

  it('numbers: 2 dp with thousand separators; differs to the cent; round2 half away from zero', () => {
    expect(money(1234567.891)).toBe('1,234,567.89');
    expect(money(null)).toBe('0.00');
    expect(round2(1.005)).toBe(1.01);
    expect(round2(-1.005)).toBe(-1.01);
    expect(differs(100, 100.001)).toBeFalse();
    expect(differs(100, 100.01)).toBeTrue();
    expect(differs(100, null)).toBeFalse();
  });

  it('stepDone reads the server steps, else the flags', () => {
    const s = { steps: [{ step: 3, title: '', done: true }], accountsCreatedCount: 0, fiscalYearConfigured: false } as unknown as SetupStatus;
    expect(stepDone(s, 3)).toBeTrue();
    expect(stepDone(s, 1)).toBeFalse();
    const flags = { steps: [], accountsCreatedCount: 5, fiscalYearConfigured: true, defaultsConfigured: false, openingBalancesEntered: false, status: 'FiscalYearSet' } as unknown as SetupStatus;
    expect([1, 2, 3, 4, 5, 6].map(i => stepDone(flags, i))).toEqual([true, true, true, false, false, false]);
    expect(stepDone(null, 1)).toBeFalse();
  });

  it('error helpers: upload errors top-level or in result; unmet list; sync labels', () => {
    expect(uploadErrors({ error: { message: 'x', errors: [{ line: 2, rule: 'R1', message: 'm' }] } }).length).toBe(1);
    expect(uploadErrors({ error: { result: { errors: [{ line: 2, rule: 'R1', message: 'm' }] } } }).length).toBe(1);
    expect(uploadErrors({ error: null })).toEqual([]);
    expect(unmetList({ error: { message: 'no', errors: ['Fiscal year', { message: 'Defaults' }] } })).toEqual(['Fiscal year', 'Defaults']);
    expect(syncStatusLabel(null)).toBe('Never');
    expect(syncStatusLabel('SUCCESS')).toBe('Success');
    expect(syncStatusLabel('Failed')).toBe('Error');
    expect(syncStatusLabel('pending')).toBe('Pending');
  });
});
