import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Subject, of, throwError } from 'rxjs';

import { GlReconciliationComponent } from './gl-reconciliation.component';
import { GlJournalService, OpenItem, ReconcileGroup, ReconcileGroupListItem } from '../../../../services/gl-journal.service';
import { AccountingService } from '../../../../services/accounting.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { AuthService } from '../../../service/auth.service';
import { fakeApi, fakeAuth, okRes } from '../gl.testing';

const item = (id: string, residual: number, over: Partial<OpenItem> = {}): OpenItem => ({
  lineUuid: id, entry: { uuid: 'e' + id, entryNumber: 'SALE/2026/000' + id }, entryDate: '2026-10-01', lineNumber: 1,
  debit: residual > 0 ? residual : 0, credit: residual < 0 ? -residual : 0, amountResidual: residual, partnerName: 'Acme', ...over
});
const group = (over: Partial<ReconcileGroup> = {}): ReconcileGroup => ({
  uuid: 'g1', groupNumber: 'REC/2026/0001', reconcileType: 'Full', amount: 500, residual: 0,
  account: { uuid: 'ar', accountCode: '1121', accountName: 'AR' }, lines: [], ...over
});
const row = (n: string, over: Partial<ReconcileGroupListItem> = {}): ReconcileGroupListItem => ({
  uuid: 'g' + n, groupNumber: 'REC/2026/000' + n, reconcileType: 'Full', amount: 500, currencyCode: 'PKR', reconciledAt: '2026-10-20T10:00:00Z',
  lineCount: 2, account: { uuid: 'ar', accountCode: '1121', accountName: 'AR' }, ...over
});

describe('GlReconciliationComponent (A47 tasks 315–321)', () => {
  let fixture: ComponentFixture<GlReconciliationComponent>;
  let c: GlReconciliationComponent;
  let api: jasmine.SpyObj<GlJournalService>;
  let confirm: jasmine.Spy;
  let toast: jasmine.Spy;
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(items: OpenItem[], perms = ['GL_RECONCILE_VIEW', 'GL_RECONCILE_EXECUTE'], query: Record<string, string> = { accountUuid: 'ar' },
                       groups: ReconcileGroupListItem[] = [row('1'), row('2', { reconcileType: 'Partial' })]) {
    api = jasmine.createSpyObj<GlJournalService>('GlJournalService',
      ['getOpenItems', 'suggestMatches', 'reconcile', 'getReconcileGroup', 'getReconcileGroups', 'unreconcile']);
    api.getOpenItems.and.returnValue(of(items));
    api.getReconcileGroup.and.returnValue(of(group()));
    api.getReconcileGroups.and.returnValue(of({ data: groups, totalRecords: 30, page: 1, pageSize: 25 }));
    const acc = fakeApi();
    acc.getPostableAccounts.and.returnValue(of([{ uuid: 'cash', accountCode: '1111', accountName: 'Cash', accountType: 'Cash' },
      { uuid: 'ar', accountCode: '1121', accountName: 'AR', accountType: 'Receivable' }] as any));
    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners']);
    partners.getPartners.and.returnValue(okRes({ data: [{ uuid: 'c1', companyName: 'Acme' }] }) as any);
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlReconciliationComponent],
      providers: [provideNoopAnimations(), provideRouter([]), { provide: GlJournalService, useValue: api }, { provide: AccountingService, useValue: acc },
        { provide: BusinessPartnerService, useValue: partners }, { provide: AuthService, useValue: fakeAuth(perms) },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } }]
    }).compileComponents();
    fixture = TestBed.createComponent(GlReconciliationComponent);
    c = fixture.componentInstance;
    confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm').and.callFake((o: any) => { o.accept(); return null as any; });
    toast = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('?accountUuid loads the open items; receivable accounts are offered first', async () => {
    await setup([item('1', 1000), item('2', -600)]);
    expect(api.getOpenItems).toHaveBeenCalledWith(jasmine.objectContaining({ accountUuid: 'ar', partnerUuid: null }));
    expect(c.accountOptions[0].value).toBe('ar');
    expect(q('item-1')).not.toBeNull();
  });

  it('selection totals: debit, credit, residual and full / partial (task 318)', async () => {
    await setup([item('1', 1000), item('2', -600), item('3', -400)]);
    c.toggle(c.items[0], true);
    c.toggle(c.items[1], true);
    fixture.detectChanges();
    expect(q('sel-debit')!.textContent).toContain('1,000.00');
    expect(q('sel-credit')!.textContent).toContain('600.00');
    expect(q('sel-residual')!.textContent).toContain('400.00');
    expect(q('sel-kind')!.textContent).toContain('partial');
    c.toggle(c.items[2], true);
    fixture.detectChanges();
    expect(q('sel-kind')!.textContent).toContain('full');
    c.toggleAll(false);
    expect(c.selected.length).toBe(0);
  });

  it('suggest matches, then add a suggestion to the selection (task 319)', async () => {
    await setup([item('1', 1000), item('2', -600), item('3', -400)]);
    c.toggle(c.items[0], true);
    api.suggestMatches.and.returnValue(of([{ lineUuids: ['2', '3'], total: -1000, residualAfter: 0, exact: true }]));
    c.suggest();
    expect(api.suggestMatches).toHaveBeenCalledWith('ar', ['1']);
    fixture.detectChanges();
    expect(q('suggestion-0')!.textContent).toContain('Exact');
    expect(q('suggestion-0')!.textContent).toContain('SALE/2026/0002');
    c.applySuggestion(c.suggestions![0]);
    expect(c.selected.map(i => i.lineUuid)).toEqual(['1', '2', '3']);
  });

  it('reconcile confirms (full vs partial), posts the line uuids, reloads items and groups (task 320)', async () => {
    await setup([item('1', 500), item('2', -500)]);
    c.toggleAll(true);
    api.reconcile.and.returnValue(of(group()));
    const groupCalls = api.getReconcileGroups.calls.count();
    c.confirmReconcile();
    expect(confirm.calls.mostRecent().args[0].message).toContain('full reconciliation');
    expect(api.reconcile).toHaveBeenCalledWith(['1', '2']);
    expect(api.getOpenItems).toHaveBeenCalledTimes(2);
    expect(api.getReconcileGroups.calls.count()).toBe(groupCalls + 1);
    expect(c.focusGroupUuid).toBe('g1');
    expect(c.selected.length).toBe(0);
  });

  it('a partly reconciled line blocks a new reconcile (RC-004) before the call', async () => {
    await setup([item('1', 400, { reconcileGroupUuid: 'g2', reconcileGroupNumber: 'REC/2026/0002' }), item('3', -400)]);
    c.toggleAll(true);
    expect(c.problem).toContain('unreconcile');
    c.confirmReconcile();
    expect(api.reconcile).not.toHaveBeenCalled();
    fixture.detectChanges();
    expect(q('partial-1')!.textContent).toContain('REC/2026/0002');
  });

  it('C-8: the account\'s groups, paged, with the partner filter (task 321)', async () => {
    await setup([]);
    expect(api.getReconcileGroups).toHaveBeenCalledWith(jasmine.objectContaining({ accountUuid: 'ar', partnerUuid: null, page: 1, pageSize: 25 }));
    expect(c.groupTotal).toBe(30);
    expect(q('group-REC/2026/0001')).not.toBeNull();
    expect(q('group-REC/2026/0002')!.textContent).toContain('Partial');
    c.onGroupsLazyLoad({ first: 25, rows: 25 });
    expect(api.getReconcileGroups.calls.mostRecent().args[0]!.page).toBe(2);
    c.partner = { uuid: 'c1', companyName: 'Acme' } as any;
    c.refresh();
    expect(api.getReconcileGroups.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ partnerUuid: 'c1', page: 1 }));
    expect(api.getOpenItems.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ partnerUuid: 'c1' }));
  });

  it('unreconcile a group asks first, then reloads items and groups; refusals shown', async () => {
    await setup([]);
    api.unreconcile.and.returnValue(of({ groupNumber: 'REC/2026/0001', linesRestored: 2 }));
    const before = api.getReconcileGroups.calls.count();
    c.confirmUnreconcile(c.groups[0]);
    expect(confirm.calls.mostRecent().args[0].message).toContain('2 lines');
    expect(api.unreconcile).toHaveBeenCalledWith('g1');
    expect(api.getReconcileGroups.calls.count()).toBe(before + 1);
    expect(toast).toHaveBeenCalledWith(jasmine.objectContaining({ detail: jasmine.stringContaining('2 lines') }));
    api.unreconcile.and.returnValue(throwError(() => new HttpErrorResponse({ status: 404, error: { success: false, message: 'Reconciliation not found' } })));
    c.unreconcile(c.groups[0]);
    expect(toast).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'Reconciliation not found' }));
  });

  it('a group link without its account reads the group for the account and highlights it', async () => {
    await setup([], undefined, { groupUuid: 'g1' });
    expect(api.getReconcileGroup).toHaveBeenCalledWith('g1');
    expect(c.accountUuid).toBe('ar');
    expect(c.focusGroupUuid).toBe('g1');
  });

  it('REV-18: a slow earlier open-items answer is dropped', async () => {
    await setup([item('1', 100)]);
    const slow = new Subject<OpenItem[]>();
    api.getOpenItems.and.returnValues(slow, of([item('9', -5)]));
    c.load();
    c.load();
    slow.next([item('8', 1)]);
    expect(c.items.map(i => i.lineUuid)).toEqual(['9']);
  });

  it('view only: no Reconcile, no Unreconcile', async () => {
    await setup([item('1', 400)], ['GL_RECONCILE_VIEW']);
    expect(q('reconcile')).toBeNull();
    expect(q('group-REC/2026/0001')).not.toBeNull();
    expect(q('unreconcile-REC/2026/0001')).toBeNull();
    c.confirmUnreconcile(c.groups[0]);
    expect(api.unreconcile).not.toHaveBeenCalled();
  });
});
