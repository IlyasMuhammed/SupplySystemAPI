import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { ConfirmationService, MessageService } from 'primeng/api';
import { throwError } from 'rxjs';

import { GlOpeningBalancesComponent } from './gl-opening-balances.component';
import { AccountingService, OpeningBalances, OpeningRow } from '../../../../services/accounting.service';
import { ImportService } from '../../../../services/import.service';
import { AuthService } from '../../../service/auth.service';
import { fakeApi, fakeAuth, okRes } from '../gl.testing';

const row = (code: string, dr = 0, cr = 0, over: Partial<OpeningRow> = {}): OpeningRow => ({
  accountUuid: 'a' + code, accountCode: code, accountName: 'Acc ' + code, debitAmount: dr, creditAmount: cr, isPlug: false, ...over
});

function data(over: Partial<OpeningBalances> = {}): OpeningBalances {
  return {
    conversionDate: '2026-07-01', isPosted: false,
    groups: [
      { rootType: 'Asset', rows: [row('1111', 250000), row('1200', 1200000)] },
      { rootType: 'Liability', rows: [row('2100', 0, 800000)] },
      { rootType: 'Equity', rows: [row('3100', 0, 500000), row('3900', 0, 0, { isPlug: true, accountName: 'Opening Balance Equity' })] },
      { rootType: 'Revenue', rows: [row('4100', 0, 0, { warning: 'P&L account' })] }
    ],
    summary: { totalDebits: 1450000, totalCredits: 1300000, difference: 150000, isBalanced: false, plugSide: 'Credit' },
    subledger: { customerOpeningTotal: 1200000, supplierOpeningTotal: 750000, openingStockValue: 0, receivableAccountBalance: 1200000,
      payableAccountBalance: 800000, inventoryAccountBalance: null, currencyCode: 'PKR' },
    ...over
  };
}

describe('GlOpeningBalancesComponent (A42 opening balances)', () => {
  let fixture: ComponentFixture<GlOpeningBalancesComponent>;
  let c: GlOpeningBalancesComponent;
  let api: jasmine.SpyObj<AccountingService>;
  let messages: MessageService;

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const r = (code: string) => c.rows.find(x => x.accountCode === code)!;

  async function setup(d = data(), perms = ['GL_VIEW', 'GL_SETUP_MANAGE', 'FINANCE_SETUP_MANAGE']) {
    api = fakeApi();
    api.getOpeningBalances.and.returnValue(okRes(d));
    const importApi = jasmine.createSpyObj<ImportService>('ImportService', ['getOpeningBalanceDate', 'setOpeningBalanceDate']);
    importApi.getOpeningBalanceDate.and.returnValue(okRes({ date: d.conversionDate ?? null }));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlOpeningBalancesComponent],
      providers: [provideNoopAnimations(), MessageService, { provide: AccountingService, useValue: api }, { provide: ImportService, useValue: importApi },
        { provide: AuthService, useValue: fakeAuth(perms) }]
    }).compileComponents();
    messages = TestBed.inject(MessageService);
    fixture = TestBed.createComponent(GlOpeningBalancesComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('embeds the A40 opening balance date editor', async () => {
    await setup();
    expect(q('opening-date-card')).not.toBeNull();
  });

  it('running totals, difference and the line to Opening Balance Equity', async () => {
    await setup();
    expect(q('total-debits')!.textContent).toContain('1,450,000.00');
    expect(q('total-credits')!.textContent).toContain('1,300,000.00');
    expect(q('difference')!.textContent).toContain('150,000.00');
    expect(q('to-obe')!.textContent).toContain('150,000.00 Cr');
  });

  it('the 3900 row is read-only, labelled calculated, and shows the plug on its side', async () => {
    await setup();
    const plug = q('ob-3900')!;
    expect(plug.querySelector('[data-testid="calculated"]')!.textContent).toContain('calculated');
    expect(plug.querySelector('p-inputnumber')).toBeNull();
    expect(plug.querySelector('[data-testid="plug-credit"]')!.textContent).toContain('150,000.00');
    expect(plug.querySelector('[data-testid="plug-debit"]')!.textContent!.trim()).toBe('');
    // More credits than debits → the plug flips to a debit.
    r('2100').creditAmount = 1000000;
    fixture.detectChanges();
    expect(c.totals.plugSide).toBe('Debit');
    expect(q('ob-3900')!.querySelector('[data-testid="plug-debit"]')!.textContent).toContain('50,000.00');
  });

  it('typing one side clears the other (never both); a both-sided row blocks Save and Post', async () => {
    await setup();
    const cash = r('1111');
    cash.creditAmount = 10;
    c.onAmount(cash, 'credit');
    expect(cash.debitAmount).toBe(0);
    cash.debitAmount = 5;
    cash.creditAmount = 5; // e.g. a draft saved by an older client
    expect(c.rowProblem).toBe('Cannot have both debit and credit amounts.');
    expect(c.canPost).toBeFalse();
  });

  it('a difference needs "I confirm this difference is intentional" before Post', async () => {
    await setup();
    expect(q('difference-warning')!.textContent).toContain('differ by 150,000.00');
    expect(c.canPost).toBeFalse();
    c.acknowledge = true;
    expect(c.canPost).toBeTrue();
    // Changing an amount asks for the confirmation again.
    r('1111').debitAmount = 300000;
    c.onAmount(r('1111'), 'debit');
    expect(c.acknowledge).toBeFalse();
  });

  it('balanced: no warning, Post allowed without acknowledgement', async () => {
    await setup();
    r('1111').debitAmount = 100000;
    fixture.detectChanges();
    expect(c.totals.difference).toBe(0);
    expect(q('difference-warning')).toBeNull();
    expect(c.canPost).toBeTrue();
  });

  it('P&L warning on Revenue / Expense sections', async () => {
    await setup();
    expect(q('group-Revenue')!.textContent).toContain('Profit & loss balance');
    expect(q('group-Asset')!.textContent).not.toContain('Profit & loss balance');
  });

  it('Save draft sends changed rows (zeroed rows too, so the draft entry is deleted)', async () => {
    await setup();
    r('1200').debitAmount = 0;
    r('2100').creditAmount = 900000;
    api.saveOpeningBalances.and.returnValue(okRes(data()));
    c.saveDraft();
    const sent = api.saveOpeningBalances.calls.mostRecent().args[0];
    expect(sent.find(e => e.accountUuid === 'a1200')).toEqual(jasmine.objectContaining({ debitAmount: 0, creditAmount: 0 }));
    expect(sent.find(e => e.accountUuid === 'a2100')!.creditAmount).toBe(900000);
    expect(sent.some(e => e.accountUuid === 'a3900')).toBeFalse();
    expect(sent.some(e => e.accountUuid === 'a4100')).toBeFalse();
  });

  it('Post & Continue: confirmation, saves a dirty draft first, posts with the acknowledgement, emits posted', async () => {
    await setup();
    const confirm = fixture.debugElement.injector.get(ConfirmationService);
    const spy = spyOn(confirm, 'confirm').and.callFake((o: any) => { o.accept(); return confirm; });
    r('1111').debitAmount = 260000;
    c.acknowledge = true;
    api.saveOpeningBalances.and.returnValue(okRes(data({ groups: data().groups.map(g => ({ ...g, rows: g.rows.map(x => x.accountCode === '1111' ? { ...x, debitAmount: 260000 } : x) })) })));
    api.postOpeningBalances.and.returnValue(okRes({ journalEntryNumber: 'JE-2026-00001', summary: data().summary }));
    let posted = 0;
    c.posted.subscribe(() => posted++);
    c.confirmPost();
    expect(spy.calls.mostRecent().args[0].message).toContain('to Opening Balance Equity');
    expect(api.saveOpeningBalances).toHaveBeenCalled();
    expect(api.postOpeningBalances).toHaveBeenCalledWith({ acknowledgeDifference: true });
    expect(posted).toBe(1);
  });

  it('Skip with saved drafts (D-56): confirms the count, zeroes every draft via the batch PUT, then posts { skip: true }', async () => {
    await setup();
    const confirm = fixture.debugElement.injector.get(ConfirmationService);
    const spy = spyOn(confirm, 'confirm').and.callFake((o: any) => { o.accept(); return confirm; });
    c.skip = true;
    fixture.detectChanges();
    expect(q('skip-drafts')!.textContent).toContain('4 saved draft balance(s)');
    api.saveOpeningBalances.and.returnValue(okRes(data({ groups: [] })));
    api.postOpeningBalances.and.returnValue(okRes({ summary: data().summary }));
    c.confirmPost();
    expect(spy.calls.mostRecent().args[0].message).toBe('This will clear 4 saved draft balances and post zero opening balances.');
    const sent = api.saveOpeningBalances.calls.mostRecent().args[0];
    expect(sent.map(e => e.accountUuid).sort()).toEqual(['a1111', 'a1200', 'a2100', 'a3100']);
    expect(sent.every(e => e.debitAmount === 0 && e.creditAmount === 0)).toBeTrue();
    expect(api.postOpeningBalances).toHaveBeenCalledWith({ skip: true });
  });

  it('a batch / post 400 with result.errors marks the rows', async () => {
    await setup();
    api.saveOpeningBalances.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400, error: {
      success: false, message: 'Account 1200 is inactive.', result: { errors: [{ accountUuid: 'a1200', accountCode: '1200', message: 'Account 1200 is inactive.' }] } } })));
    c.saveDraft();
    fixture.detectChanges();
    expect(q('ob-1200')!.querySelector('[data-testid="row-problem"]')!.textContent).toContain('Account 1200 is inactive.');
    c.onAmount(c.rows.find(x => x.accountCode === '1200')!, 'debit');
    expect(c.problemOf(c.rows.find(x => x.accountCode === '1200')!)).toBeNull();
  });

  it('shows the saved draft count (summary.entryCount)', async () => {
    await setup(data({ summary: { ...data().summary, entryCount: 4 } }));
    expect(q('entry-count')!.textContent).toContain('4 draft entries saved');
  });

  it('Skip (new company) posts { skip: true } with no entries', async () => {
    const empty = data();
    empty.groups = empty.groups.map(g => ({ ...g, rows: g.rows.map(x => ({ ...x, debitAmount: 0, creditAmount: 0 })) }));
    await setup(empty);
    const confirm = fixture.debugElement.injector.get(ConfirmationService);
    spyOn(confirm, 'confirm').and.callFake((o: any) => { o.accept(); return confirm; });
    c.skip = true;
    fixture.detectChanges();
    expect(q('totals')).toBeNull();
    expect(c.canPost).toBeTrue();
    api.postOpeningBalances.and.returnValue(okRes({ summary: data().summary }));
    c.confirmPost();
    expect(api.postOpeningBalances).toHaveBeenCalledWith({ skip: true });
    expect(api.saveOpeningBalances).not.toHaveBeenCalled();
  });

  it('a post refusal is shown in the server words', async () => {
    await setup();
    c.acknowledge = true;
    const add = spyOn(messages, 'add');
    api.postOpeningBalances.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400, error: { message: 'The opening balance date must fall within the first fiscal year.' } })));
    c.post();
    expect(add).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'The opening balance date must fall within the first fiscal year.' }));
  });

  it('without a conversion date Post is off and a notice says so', async () => {
    await setup(data({ conversionDate: null }));
    expect(q('no-date')).not.toBeNull();
    c.acknowledge = true;
    expect(c.canPost).toBeFalse();
  });

  it('read-only after posting: no inputs, no Post', async () => {
    await setup(data({ isPosted: true }));
    expect(q('posted-note')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('p-inputnumber')).toBeNull();
    expect(q('post')).toBeNull();
    expect(q('difference-warning')).toBeNull();
  });

  it('skipped (new company): its own note, read-only', async () => {
    await setup(data({ isPosted: true, isSkipped: true }));
    expect(q('skipped-note')).not.toBeNull();
    expect(q('posted-note')).toBeNull();
    expect(q('post')).toBeNull();
  });

  it('sub-ledger reference: customer / supplier / stock next to the GL default balances, "differs" when they differ', async () => {
    await setup();
    expect(q('sub-ar')!.querySelector('[data-testid="differs"]')).toBeNull();
    expect(q('sub-ap')!.querySelector('[data-testid="differs"]')).not.toBeNull();
    expect(q('sub-inv')!.textContent).toContain('—');
    expect(q('sub-inv')!.querySelector('[data-testid="differs"]')).toBeNull();
  });
});
