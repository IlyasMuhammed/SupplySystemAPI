import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse, HttpHeaders, HttpResponse } from '@angular/common/http';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { GlAccountsComponent, deleteBlockedReason } from './gl-accounts.component';
import { GlAccountDialogComponent } from './gl-account-dialog.component';
import { AccountingService } from '../../../../services/accounting.service';
import { AuthService } from '../../../service/auth.service';
import { acc, fakeApi, fakeAuth, okRes, sampleTree } from '../gl.testing';
import { allNodes } from '../gl.shared';

describe('GlAccountsComponent (A42 CoA tree)', () => {
  let fixture: ComponentFixture<GlAccountsComponent>;
  let c: GlAccountsComponent;
  let api: jasmine.SpyObj<AccountingService>;
  let messages: MessageService;

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const codes = () => c.rows.map(r => r.node.accountCode);

  async function setup(perms = ['GL_VIEW', 'GL_ACCOUNT_MANAGE'], embedded = false) {
    api = fakeApi();
    api.getChart.and.returnValue(of({ roots: sampleTree(), summary: { totalAccounts: 10, postableAccounts: 3, lastModifiedAt: '2026-10-12T09:00:00Z' } }));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlAccountsComponent],
      providers: [provideNoopAnimations(), MessageService, { provide: AccountingService, useValue: api }, { provide: AuthService, useValue: fakeAuth(perms) }]
    }).compileComponents();
    messages = TestBed.inject(MessageService);
    fixture = TestBed.createComponent(GlAccountsComponent);
    c = fixture.componentInstance;
    c.embedded = embedded;
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('opens with the five roots expanded one level; toggling collapses and expands', async () => {
    await setup();
    expect(codes()).toEqual(['1', '11', '2', '21']);
    c.toggle(c.rows[1]); // expand 11
    expect(codes()).toEqual(['1', '11', '111', '2', '21']);
    c.toggle(c.rows[0]); // collapse 1
    expect(codes()).toEqual(['1', '2', '21']);
    fixture.detectChanges();
    expect(q('row-1')).not.toBeNull();
    expect(q('row-11')).toBeNull();
  });

  it('shows the balance on leaves only, with thousand separators and 2 dp', async () => {
    await setup();
    c.expandAll();
    fixture.detectChanges();
    expect(q('row-1112')!.textContent).toContain('500.00');
    expect(q('row-1111')!.textContent).toContain('250.50');
    expect(q('row-111')!.querySelector('.gl-num')!.textContent!.trim()).toBe('');
  });

  it('search by code or name shows matches with their ancestors; level filter limits depth', async () => {
    await setup();
    c.search = 'cash';
    c.rebuild();
    expect(codes()).toEqual(['1', '11', '111', '1111']);
    c.search = '';
    c.maxLevel = 1;
    c.rebuild();
    expect(codes()).toEqual(['1', '2']);
  });

  it('footer: totals, postable count and last modified', async () => {
    await setup();
    expect(q('total-accounts')!.textContent).toContain('10');
    expect(q('postable-accounts')!.textContent).toContain('3');
    expect(q('footer')!.textContent).toContain('Last modified');
  });

  it('Delete is disabled with the reason for system accounts, parents and accounts with postings', async () => {
    await setup();
    const all = allNodes(c.roots);
    const byCode = (code: string) => all.find(n => n.accountCode === code)!;
    expect(deleteBlockedReason(byCode('1'))).toBe('Cannot delete a system account.');
    expect(deleteBlockedReason(byCode('11'))).toBe('Cannot delete an account that has child accounts.');
    expect(deleteBlockedReason(byCode('1111'))).toBe('Cannot delete an account with posted transactions.');
    expect(deleteBlockedReason(byCode('1112'))).toBeNull();
    // The server's canDelete / deleteBlockedReason on a node win over the client-side reason.
    expect(deleteBlockedReason({ ...byCode('1112'), canDelete: false, deleteBlockedReason: 'Used as the default Bank account.' }))
      .toBe('Used as the default Bank account.');
    expect(deleteBlockedReason({ ...byCode('1111'), canDelete: true })).toBeNull();

    c.openRowMenu(new MouseEvent('click'), byCode('11'));
    const del = c.rowItems.find(i => i.label === 'Delete')!;
    expect(del.disabled).toBeTrue();
    expect(c.rowItems.some(i => i.label === 'Cannot delete an account that has child accounts.')).toBeTrue();
    c.openRowMenu(new MouseEvent('click'), byCode('1112'));
    expect(c.rowItems.find(i => i.label === 'Delete')!.disabled).toBeFalse();
  });

  it('delete asks first and shows a server refusal as is', async () => {
    await setup();
    const confirm = fixture.debugElement.injector.get(ConfirmationService);
    spyOn(confirm, 'confirm').and.callFake((o: any) => { o.accept(); return confirm; });
    const add = spyOn(messages, 'add');
    api.deleteAccount.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { message: 'Cannot delete an account with posted transactions.' } })));
    c.confirmDelete(acc('1112', 4, false));
    expect(add).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: 'Cannot delete an account with posted transactions.' }));
    api.deleteAccount.and.returnValue(okRes(null));
    c.confirmDelete(acc('1112', 4, false));
    expect(api.getChart).toHaveBeenCalledTimes(2);
  });

  it('read-only without GL_ACCOUNT_MANAGE / GL_SETUP_MANAGE: no Add, Edit/Delete disabled', async () => {
    await setup(['GL_VIEW']);
    expect(q('add-account')).toBeNull();
    c.openRowMenu(new MouseEvent('click'), allNodes(c.roots).find(n => n.accountCode === '1112')!);
    expect(c.rowItems.every(i => i.disabled)).toBeTrue();
  });

  it('GL_SETUP_MANAGE also manages accounts', async () => {
    await setup(['GL_VIEW', 'GL_SETUP_MANAGE']);
    expect(q('add-account')).not.toBeNull();
  });

  it('Export downloads with the server file name', async () => {
    await setup();
    const blob = new Blob(['code']);
    api.exportChart.and.returnValue(of(new HttpResponse({ body: blob, headers: new HttpHeaders({ 'Content-Disposition': 'attachment; filename="chart-of-accounts.csv"' }) })));
    const click = spyOn(HTMLAnchorElement.prototype, 'click');
    c.export('csv');
    expect(api.exportChart).toHaveBeenCalledWith('csv');
    expect(click).toHaveBeenCalled();
  });

  it('embedded (wizard step 2): no sf-page, emits changed after a save', async () => {
    await setup(undefined, true);
    expect(fixture.nativeElement.querySelector('sf-page')).toBeNull();
    let changed = 0;
    c.changed.subscribe(() => changed++);
    c.onSaved({ ...acc('1113', 4, false), parentUuid: 'u111' } as any);
    expect(changed).toBe(1);
    expect(c.expanded.has('u111')).toBeTrue();
  });

  it('a failed load shows the error', async () => {
    await setup();
    api.getChart.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
    c.load();
    fixture.detectChanges();
    expect(q('load-error')).not.toBeNull();
  });
});

describe('GlAccountDialogComponent (A42 account form)', () => {
  let fixture: ComponentFixture<GlAccountDialogComponent>;
  let c: GlAccountDialogComponent;
  let api: jasmine.SpyObj<AccountingService>;

  async function setup() {
    api = fakeApi();
    api.suggestCode.and.returnValue(okRes({ code: '1113' }));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlAccountDialogComponent],
      providers: [provideNoopAnimations(), { provide: AccountingService, useValue: api }]
    }).compileComponents();
    fixture = TestBed.createComponent(GlAccountDialogComponent);
    c = fixture.componentInstance;
    c.accounts = allNodes(sampleTree());
    fixture.detectChanges();
  }

  function open(editing: any = null, preset: string | null = null) {
    c.editing = editing;
    c.presetParentUuid = preset;
    c.visible = true;
    c.ngOnChanges({ visible: { currentValue: true } as any });
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('parents are groups of levels 1–3 only', async () => {
    await setup();
    const parents = c.parentOptions.map(o => o.value);
    expect(parents).toEqual(jasmine.arrayContaining(['u1', 'u11', 'u111', 'u2']));
    expect(parents).not.toContain('u1111');
  });

  it('choosing a parent fills level, root type, suggested code and the type list of its root', async () => {
    await setup();
    open(null, 'u111');
    expect(api.suggestCode).toHaveBeenCalledWith('u111');
    expect(c.draft.accountCode).toBe('1113');
    expect(c.level).toBe(4);
    expect(c.isLeaf).toBeTrue();
    expect(c.parent!.rootType).toBe('Asset');
    expect(c.typeOptions.map(o => o.value)).toContain('Bank');
    expect(c.typeOptions.map(o => o.value)).not.toContain('Payable');
  });

  it('a level 2/3 account is a group (no type)', async () => {
    await setup();
    open(null, 'u1');
    expect(c.level).toBe(2);
    expect(c.isLeaf).toBeFalse();
  });

  it('validates like the server before sending and creates with the typed values', async () => {
    await setup();
    open(null, 'u111');
    c.draft.accountCode = '1111';
    c.save();
    expect(c.shownProblem).toBe('Account code 1111 already exists.');
    expect(api.createAccount).not.toHaveBeenCalled();
    c.draft.accountCode = '1113';
    c.draft.accountName = 'HBL Savings';
    c.draft.accountType = 'Bank';
    api.createAccount.and.returnValue(okRes({ ...acc('1113', 4, false), parentUuid: 'u111' } as any));
    let saved = 0;
    c.saved.subscribe(() => saved++);
    c.save();
    expect(api.createAccount).toHaveBeenCalledWith(jasmine.objectContaining({ parentUuid: 'u111', accountCode: '1113', accountName: 'HBL Savings', accountType: 'Bank' }));
    expect(saved).toBe(1);
  });

  it('edit: code and parent are fixed; only the editable fields are sent; server refusal shown', async () => {
    await setup();
    const leaf = allNodes(sampleTree()).find(n => n.accountCode === '1112')!;
    open({ ...leaf, parentUuid: 'u111' });
    expect(c.draft.accountCode).toBe('1112');
    c.draft.accountName = 'Meezan — Current';
    api.updateAccount.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400, error: { message: 'Account type Payable is not valid for Asset accounts.' } })));
    c.save();
    expect(api.updateAccount).toHaveBeenCalledWith(leaf.uuid, jasmine.objectContaining({ accountName: 'Meezan — Current' }));
    expect(api.updateAccount.calls.mostRecent().args[1]).not.toEqual(jasmine.objectContaining({ accountCode: jasmine.anything() }));
    expect(c.shownProblem).toBe('Account type Payable is not valid for Asset accounts.');
  });
});
