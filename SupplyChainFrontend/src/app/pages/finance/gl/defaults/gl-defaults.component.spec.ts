import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';

import { GlDefaultsComponent, requiredSummary } from './gl-defaults.component';
import { Account, AccountingDefaults, AccountingService } from '../../../../services/accounting.service';
import { AuthService } from '../../../service/auth.service';
import { fakeApi, fakeAuth, okRes } from '../gl.testing';

const account = (uuid: string, code: string, type: any): Account => ({
  uuid, accountCode: code, accountName: 'Acc ' + code, materializedPath: code, level: 4, rootType: 'Asset', isGroup: false, accountType: type,
  isSystemAccount: false, isActive: true, hasTransactions: false, openingBalanceDebit: 0, openingBalanceCredit: 0, currentBalance: 0, sortOrder: 0
});

function defaults(): AccountingDefaults {
  return {
    requiredCount: 3, assignedRequiredCount: 1,
    categories: [
      { category: 'Receivables', items: [
        { defaultKey: 'DEFAULT_AR', defaultLabel: 'Accounts receivable', category: 'Receivables', isRequired: true, expectedAccountType: 'Receivable', isLocked: false,
          account: { uuid: 'a-ar', accountCode: '1200', accountName: 'Trade debtors', accountType: 'Receivable' } },
        { defaultKey: 'DEFAULT_CUSTOMER_ADVANCE', defaultLabel: 'Customer advances', category: 'Receivables', isRequired: false, isLocked: false, account: null }
      ] },
      { category: 'Payables', items: [
        { defaultKey: 'DEFAULT_AP', defaultLabel: 'Accounts payable', category: 'Payables', isRequired: true, expectedAccountType: 'Payable', isLocked: false, account: null }
      ] },
      { category: 'Equity', items: [
        { defaultKey: 'DEFAULT_OPENING_BALANCE', defaultLabel: 'Opening balance equity', category: 'Equity', isRequired: true, isLocked: true,
          account: { uuid: 'a-obe', accountCode: '3900', accountName: 'Opening Balance Equity' } }
      ] }
    ]
  };
}

describe('GlDefaultsComponent (A42 defaults)', () => {
  let fixture: ComponentFixture<GlDefaultsComponent>;
  let c: GlDefaultsComponent;
  let api: jasmine.SpyObj<AccountingService>;

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(perms = ['GL_VIEW', 'GL_SETUP_MANAGE']) {
    api = fakeApi();
    api.getDefaults.and.returnValue(okRes(defaults()));
    api.getPostableAccounts.and.returnValue(of([account('a-ar', '1200', 'Receivable'), account('a-bank', '1112', 'Bank'), account('a-ap', '2100', 'Payable')]));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlDefaultsComponent],
      providers: [provideNoopAnimations(), MessageService, { provide: AccountingService, useValue: api }, { provide: AuthService, useValue: fakeAuth(perms) }]
    }).compileComponents();
    fixture = TestBed.createComponent(GlDefaultsComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('groups rows by category with Required / Optional badges', async () => {
    await setup();
    expect(q('cat-Receivables')).not.toBeNull();
    expect(q('def-DEFAULT_AR')!.querySelector('[data-testid="badge"]')!.textContent).toContain('Required');
    expect(q('def-DEFAULT_CUSTOMER_ADVANCE')!.querySelector('[data-testid="badge"]')!.textContent).toContain('Optional');
  });

  it('"X of Y required defaults assigned" follows the current selections', async () => {
    await setup();
    expect(q('required-summary')!.textContent).toContain('2 of 3 required defaults assigned'); // AR + the locked 3900
    c.selection['DEFAULT_AP'] = 'a-ap';
    fixture.detectChanges();
    expect(q('required-summary')!.textContent).toContain('3 of 3');
    expect(requiredSummary(c.items, {})).toEqual({ assigned: 0, required: 3 });
  });

  it('DEFAULT_OPENING_BALANCE is locked with a lock icon and is never sent', async () => {
    await setup();
    const row = q('def-DEFAULT_OPENING_BALANCE')!;
    expect(row.querySelector('[data-testid="locked-default"] .pi-lock')).not.toBeNull();
    expect(row.textContent).toContain('3900');
    expect(row.querySelector('[data-testid="pick-DEFAULT_OPENING_BALANCE"]')).toBeNull();
    c.selection['DEFAULT_AP'] = 'a-ap';
    api.saveDefaults.and.returnValue(okRes(defaults()));
    api.validateDefaults.and.returnValue(okRes({ isValid: true, missingRequired: [], problems: [], warnings: [] }));
    c.save();
    const sent = api.saveDefaults.calls.mostRecent().args[0];
    expect(sent.map(i => i.defaultKey)).toEqual(['DEFAULT_AR', 'DEFAULT_CUSTOMER_ADVANCE', 'DEFAULT_AP']);
    expect(sent.find(i => i.defaultKey === 'DEFAULT_AP')!.accountUuid).toBe('a-ap');
    expect(api.validateDefaults).toHaveBeenCalled();
  });

  it('Save is off until something changed', async () => {
    await setup();
    expect(c.dirty).toBeFalse();
    c.selection['DEFAULT_CUSTOMER_ADVANCE'] = 'a-bank';
    expect(c.dirty).toBeTrue();
  });

  it('a type mismatch is a warning, not a block', async () => {
    await setup();
    c.selection['DEFAULT_AR'] = 'a-bank';
    fixture.detectChanges();
    expect(c.mismatch(c.items[0])).toBe('Bank');
    expect(q('type-mismatch')!.textContent).toContain('expected Receivable');
  });

  it('Validate All shows missing, problems and warnings with labels', async () => {
    await setup();
    api.validateDefaults.and.returnValue(okRes({
      isValid: false, missingRequired: ['DEFAULT_AP'],
      problems: [{ defaultKey: 'DEFAULT_AR', message: 'The account must be active.' }],
      warnings: [{ defaultKey: 'DEFAULT_CUSTOMER_ADVANCE', message: 'Expected a Liability account.' }]
    }));
    c.validate();
    fixture.detectChanges();
    const v = q('validation')!.textContent!;
    expect(v).toContain('Accounts payable: required, not assigned.');
    expect(v).toContain('Accounts receivable: The account must be active.');
    expect(v).toContain('Customer advances: Expected a Liability account.');
  });

  it('the server per-row problem shows until the row is changed', async () => {
    const d = defaults();
    d.categories[0].items[0].problem = 'The account must be active.';
    api = fakeApi();
    await setup();
    c['apply'](d);
    fixture.detectChanges();
    expect(q('def-DEFAULT_AR')!.querySelector('[data-testid="row-problem"]')!.textContent).toContain('must be active');
    c.selection['DEFAULT_AR'] = 'a-bank';
    fixture.detectChanges();
    expect(q('def-DEFAULT_AR')!.querySelector('[data-testid="row-problem"]')).toBeNull();
  });

  it('a required row whose saved account has a problem counts as unassigned', async () => {
    await setup();
    const items = defaults().categories.flatMap(x => x.items);
    items[0].problem = 'The default account must be active.';
    expect(requiredSummary(items, { DEFAULT_AR: 'a-ar', DEFAULT_AP: 'a-ap', DEFAULT_OPENING_BALANCE: 'a-obe' })).toEqual({ assigned: 2, required: 3 });
    expect(requiredSummary(items, { DEFAULT_AR: 'a-bank', DEFAULT_AP: 'a-ap', DEFAULT_OPENING_BALANCE: 'a-obe' })).toEqual({ assigned: 3, required: 3 });
  });

  it('save-all 400 result.problems are shown on their rows', async () => {
    await setup();
    c.selection['DEFAULT_AP'] = 'a-bank';
    api.saveDefaults.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400, error: {
      success: false, message: 'Only Level 4 accounts can be assigned as defaults.',
      result: { problems: [{ defaultKey: 'DEFAULT_AP', message: 'Only Level 4 accounts can be assigned as defaults.' }] } } })));
    c.save();
    fixture.detectChanges();
    expect(q('def-DEFAULT_AP')!.querySelector('[data-testid="row-problem"]')!.textContent).toContain('Only Level 4 accounts');
  });

  it('read-only: pickers disabled and no Save', async () => {
    await setup(['GL_VIEW']);
    expect(q('save-defaults')).toBeNull();
  });
});
