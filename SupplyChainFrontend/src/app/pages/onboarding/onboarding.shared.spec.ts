import {
  CATEGORY_INFO, SETUP_CATEGORIES, allCoa, categoryLabel, coaProblem, coaRoleKeys, coaToTree, errorResult, inviteProblem, payloadCount,
  resultTone, roleAvailable, setupStatusLabel, suggestChildCode, taxProblem, treeToCoa, warehouseProblem
} from './onboarding.shared';

import { sampleCoa, sampleLeaf as leaf } from './onboarding.testing';

describe('onboarding.shared', () => {
  it('every category has a label; view links point at the existing screens', () => {
    for (const c of SETUP_CATEGORIES) expect(CATEGORY_INFO[c]).toBeDefined();
    expect(CATEGORY_INFO['COA'].viewRoute).toBe('/portal/pages/finance/gl/accounts');
    expect(CATEGORY_INFO['PAYMENT_TERMS'].viewRoute).toBe('/portal/pages/finance/ap/settings');
    expect(categoryLabel('TAX')).toBe('Tax codes');
    expect(categoryLabel('SOMETHING_NEW')).toBe('Something new');
    expect(setupStatusLabel('NotStarted')).toBe('Not started');
    expect(resultTone('Failed')).toBe('er');
  });

  it('payloadCount counts per category (nested accounts, units, active approval levels)', () => {
    expect(payloadCount('COA', { accounts: sampleCoa() })).toBe(7);
    expect(payloadCount('TAX', { taxCodes: [{}, {}] })).toBe(2);
    expect(payloadCount('CURRENCY', { base: { code: 'PKR' }, additional: [{}, {}] })).toBe(3);
    expect(payloadCount('UOM', { groups: [{ units: [{}, {}] }, { units: [{}] }] })).toBe(3);
    expect(payloadCount('APPROVAL_RULES', { rules: [{ levels: [{}, {}] }, { afterA49: true, levels: [{}] }] })).toBe(3);
    expect(payloadCount('X', { a: [{}, { children: [{}] }] })).toBe(3);
    expect(payloadCount('TAX', null)).toBe(0);
  });

  it('coaToTree / treeToCoa round-trip and re-derive levels', () => {
    const tree = coaToTree(sampleCoa(), 1);
    expect(tree.length).toBe(2);
    expect(tree[0].expanded).toBeTrue();
    expect(tree[0].draggable).toBeFalse();
    expect(tree[0].children![0].draggable).toBeTrue();
    const back = treeToCoa(tree);
    expect(allCoa(back).map(a => a.code)).toEqual(['1', '11', '111', '1111', '1112', '112', '2']);
    expect(allCoa(back).find(a => a.code === '1111')!.level).toBe(4);
    expect(coaRoleKeys(tree[0])).toEqual(['DEFAULT_CASH']);
  });

  it('coaProblem enforces the A42 rules', () => {
    expect(coaProblem(sampleCoa())).toBeNull();
    const dup = sampleCoa(); dup[0].children[0].children[0].children.push(leaf('1112', 'Again', 'Asset'));
    expect(coaProblem(dup)).toContain('used twice');
    const digit = sampleCoa(); digit[0].children[0].children[0].children.push(leaf('2999', 'Wrong', 'Asset'));
    expect(coaProblem(digit)).toContain('start with 1');
    const shallowLeaf = sampleCoa(); shallowLeaf[0].children.push(leaf('12', 'Leaf at 2', 'Asset'));
    expect(coaProblem(shallowLeaf)).toContain('level 4');
    const twoRoles = sampleCoa(); twoRoles[0].children[0].children[0].children[1].default_account_key = 'DEFAULT_CASH';
    expect(coaProblem(twoRoles)).toContain('assigned twice');
    const noName = sampleCoa(); noName[0].children[0].name = ' ';
    expect(coaProblem(noName)).toContain('needs a name');
  });

  it('suggestChildCode picks the next free sibling code', () => {
    const parent = { code: '111' } as any;
    expect(suggestChildCode(parent, [{ code: '1111' }, { code: '1112' }] as any, ['1111', '1112'])).toBe('1113');
    expect(suggestChildCode({ code: '112' } as any, [], ['1121'])).toBe('1122');
  });

  it('taxProblem: code, rate range, one default per usage', () => {
    expect(taxProblem([{ code: 'GST18', name: 'GST', rate: 18, usage: 'SALES', isDefault: true }])).toBeNull();
    expect(taxProblem([{ code: '', name: 'x', rate: 1, usage: 'SALES' }])).toContain('needs a code');
    expect(taxProblem([{ code: 'A', name: 'x', rate: 101, usage: 'SALES' }])).toContain('between 0 and 100');
    expect(taxProblem([{ code: 'A', name: 'x', rate: 1, usage: 'SALES', isDefault: true }, { code: 'B', name: 'y', rate: 1, usage: 'SALES', isDefault: true }]))
      .toContain('Only one default');
    expect(taxProblem([{ code: 'A', name: 'x', rate: 1, usage: 'SALES' }, { code: 'a', name: 'y', rate: 1, usage: 'BOTH' }])).toContain('twice');
  });

  it('warehouse and invite rows', () => {
    expect(warehouseProblem([{ code: 'WH01', name: 'Main', zones: [] }])).toBeNull();
    expect(warehouseProblem([{ code: 'WH 1', name: 'Main', zones: [] }])).toContain('may contain');
    expect(warehouseProblem([{ code: 'A', name: 'x', zones: [] }, { code: 'a', name: 'y', zones: [] }])).toContain('twice');
    expect(inviteProblem([{ email: 'a@b.com', roleRef: 'SALES_REP' }])).toBeNull();
    expect(inviteProblem([{ email: 'nope', roleRef: 'SALES_REP' }])).toContain('not a valid');
    expect(inviteProblem([{ email: 'a@b.com', roleRef: '' }])).toContain('Choose a role');
  });

  it('roleAvailable needs every required module; errorResult reads the envelope result', () => {
    expect(roleAvailable({ requiredModules: [] }, () => false)).toBeTrue();
    expect(roleAvailable({ requiredModules: ['MODULE_DEMAND'] }, c => c === 'MODULE_DEMAND')).toBeTrue();
    expect(roleAvailable({ requiredModules: ['MODULE_DEMAND', 'MODULE_CRM'] }, c => c === 'MODULE_DEMAND')).toBeFalse();
    expect(errorResult<any>({ error: { result: { referencedCount: 2 } } })?.referencedCount).toBe(2);
    expect(errorResult({ error: 'x' })).toBeNull();
  });
});
