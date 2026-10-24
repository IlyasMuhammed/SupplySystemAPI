import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { ConfirmationService, MessageService } from 'primeng/api';
import { BehaviorSubject, of, throwError } from 'rxjs';

import { GlJournalEntryDetailComponent } from './gl-journal-entry-detail.component';
import { GlJournalService, JournalEntryDetail } from '../../../../services/gl-journal.service';
import { AuthService } from '../../../service/auth.service';
import { fakeAuth } from '../gl.testing';

const ENTRY = (over: Partial<JournalEntryDetail> = {}): JournalEntryDetail => ({
  uuid: 'e1', entryNumber: 'JV/2026/0003', journalType: { uuid: 't', code: 'MISC', name: 'Miscellaneous Journal', type: 'Miscellaneous' },
  entryDate: '2026-10-18', status: 'Draft', totalDebit: 2200, totalCredit: 2200, currencyCode: 'PKR', isManual: true, entryType: 'MANUAL_JOURNAL',
  lines: [
    { lineUuid: 'l1', lineNumber: 1, account: { uuid: 'a1', accountCode: '1111', accountName: 'Cash' }, debit: 2200, credit: 0, currencyCode: 'PKR', amountCurrency: 2200 },
    { lineUuid: 'l2', lineNumber: 2, account: { uuid: 'a2', accountCode: '4101', accountName: 'Sales' }, debit: 0, credit: 2200, currencyCode: 'PKR', amountCurrency: -2200,
      reconcileGroupUuid: 'g1', reconcileGroupNumber: 'REC/2026/0001', reconciled: true }
  ],
  ...over
});

describe('GlJournalEntryDetailComponent (A47 tasks 260–263)', () => {
  let fixture: ComponentFixture<GlJournalEntryDetailComponent>;
  let c: GlJournalEntryDetailComponent;
  let api: jasmine.SpyObj<GlJournalService>;
  let toast: jasmine.Spy;
  let navigate: jasmine.Spy;
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(entry: JournalEntryDetail, perms = ['GL_VIEW', 'GL_JOURNAL_EDIT', 'GL_JOURNAL_DELETE', 'GL_JOURNAL_POST', 'GL_JOURNAL_REVERSE']) {
    api = jasmine.createSpyObj<GlJournalService>('GlJournalService', ['getEntry', 'postEntry', 'reverseEntry', 'deleteEntry']);
    api.getEntry.and.returnValue(of(entry));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlJournalEntryDetailComponent],
      providers: [provideNoopAnimations(), provideRouter([]), { provide: GlJournalService, useValue: api }, { provide: AuthService, useValue: fakeAuth(perms) },
        { provide: ActivatedRoute, useValue: { paramMap: new BehaviorSubject(convertToParamMap({ uuid: 'e1' })) } }]
    }).compileComponents();
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture = TestBed.createComponent(GlJournalEntryDetailComponent);
    c = fixture.componentInstance;
    spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm').and.callFake((o: any) => { o.accept(); return null as any; });
    toast = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('a draft: lines, totals balanced, Post / Edit / Delete; no Reverse; the group links to reconciliation', async () => {
    await setup(ENTRY());
    expect(q('status')!.textContent).toContain('Draft');
    expect(q('total-debit')!.textContent).toContain('2,200.00');
    expect(q('lines')!.textContent).toContain('balanced');
    expect(q('post')).not.toBeNull();
    expect(q('edit')).not.toBeNull();
    expect(q('delete')).not.toBeNull();
    expect(q('reverse')).toBeNull();
    expect(q('group-2')!.textContent).toContain('REC/2026/0001');
    expect(q('source')!.textContent).toContain('Manual entry');
  });

  it('REV-18: "not balanced" when the 2-dp base totals differ by a cent', async () => {
    const e = ENTRY();
    await setup({ ...e, lines: [{ ...e.lines[0], debit: 2200.01 }, e.lines[1]] });
    expect(c.totals.balanced).toBeFalse();
    expect(q('lines')!.textContent).toContain('not balanced');
  });

  it('Post asks, posts, shows the posted entry', async () => {
    await setup(ENTRY());
    api.postEntry.and.returnValue(of(ENTRY({ status: 'Posted' })));
    c.confirmPost();
    expect(api.postEntry).toHaveBeenCalledWith('e1');
    fixture.detectChanges();
    expect(q('status')!.textContent).toContain('Posted');
    expect(q('post')).toBeNull();
  });

  it('server flags win: canPost false hides Post even with the permission', async () => {
    await setup(ENTRY({ canPost: false, canEdit: false, canDelete: false }));
    expect(q('post')).toBeNull();
    expect(q('edit')).toBeNull();
    expect(q('delete')).toBeNull();
  });

  it('posted: read-only, Reverse needs a reason (≤ 200), sends the optional date, reloads', async () => {
    await setup(ENTRY({ status: 'Posted', canReverse: true }));
    expect(q('edit')).toBeNull();
    c.openReverse();
    c.reverse();
    expect(c.reverseError).toBe('Give the reason for the reversal.');
    c.reverseReason = 'x'.repeat(201);
    c.reverse();
    expect(c.reverseError).toContain('200');
    c.reverseReason = 'Wrong account';
    c.reverseDate = new Date(2026, 9, 31);
    api.reverseEntry.and.returnValue(of({ reversal: ENTRY({ uuid: 'e2', entryNumber: 'JV/2026/0004' }), original: { uuid: 'e1', entryNumber: 'JV/2026/0003' },
      reconcileGroupNumbers: ['REC/2026/0002'], warning: null }));
    c.reverse();
    expect(api.reverseEntry).toHaveBeenCalledWith('e1', { reason: 'Wrong account', reversalDate: '2026-10-31' });
    expect(toast).toHaveBeenCalledWith(jasmine.objectContaining({ detail: jasmine.stringContaining('by JV/2026/0004') }));
    expect(api.getEntry).toHaveBeenCalledTimes(2);
  });

  it('a gateway journal: the reverse dialog warns the source document is untouched; source links to the invoice; period refusal shown', async () => {
    await setup(ENTRY({ status: 'Posted', isManual: false, entryType: 'SALES_INVOICE', sourceDocumentUuid: 'inv1', entryNumber: 'JE-2026-00012' }));
    expect(q('source-link')).not.toBeNull();
    expect(c.sourceRoute).toEqual(['/portal/pages/finance/sales-invoices', 'inv1']);
    c.openReverse();
    fixture.detectChanges();
    await fixture.whenStable();
    const warning = q('gateway-warning') ?? document.querySelector('[data-testid="gateway-warning"]');
    expect(warning?.textContent).toContain('source document stays as it is');
    c.reverseReason = 'dup';
    api.reverseEntry.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { success: false, message: 'Reversal period is locked' } })));
    c.reverse();
    expect(c.reverseError).toBe('Reversal period is locked');
  });

  it('reversed: shows "Reversed by"; reversal shows "Reversal of"', async () => {
    await setup(ENTRY({ status: 'Reversed', reversedByEntry: { uuid: 'e2', entryNumber: 'JV/2026/0004' }, reversalReason: 'Wrong' }));
    expect(q('reversed-by')!.textContent).toContain('JV/2026/0004');
    expect(q('reverse')).toBeNull();
    await setup(ENTRY({ status: 'Posted', reversesEntry: { uuid: 'e0', entryNumber: 'JV/2026/0001' } }));
    expect(q('reverses')!.textContent).toContain('JV/2026/0001');
  });

  it('delete a draft asks first and goes back to the list', async () => {
    await setup(ENTRY());
    api.deleteEntry.and.returnValue(of({ deleted: true }));
    c.confirmDelete();
    expect(api.deleteEntry).toHaveBeenCalledWith('e1');
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/finance/gl/journal-entries']);
  });
});
