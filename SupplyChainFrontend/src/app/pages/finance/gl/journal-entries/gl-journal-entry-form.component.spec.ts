import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { GlJournalEntryFormComponent } from './gl-journal-entry-form.component';
import { GlJournalService, JournalEntryDetail, JournalType } from '../../../../services/gl-journal.service';
import { AccountingService } from '../../../../services/accounting.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { OrgCurrencyService } from '../../../../services/org-currency.service';
import { AuthService } from '../../../service/auth.service';
import { fakeApi, fakeAuth, okRes } from '../gl.testing';

const TYPES: JournalType[] = [
  { uuid: 'tSale', code: 'SALE', name: 'Sales Journal', type: 'Sale', sequencePrefix: 'SALE', requiresPartner: true, autoPost: false, isSystem: true, isActive: true, sortOrder: 10 },
  { uuid: 'tMisc', code: 'MISC', name: 'Miscellaneous Journal', type: 'Miscellaneous', sequencePrefix: 'JV', requiresPartner: false, autoPost: false,
    isSystem: true, isActive: true, sortOrder: 50, defaultDebitAccount: { uuid: 'a1', accountCode: '1111', accountName: 'Cash' } },
  { uuid: 'tOpen', code: 'OPEN', name: 'Opening Journal', type: 'Opening', sequencePrefix: 'OPEN', requiresPartner: false, autoPost: true, isSystem: true, isActive: true, sortOrder: 60 }
];

const DRAFT: JournalEntryDetail = {
  uuid: 'e1', entryNumber: 'JV/2026/0003', journalType: { uuid: 'tMisc', code: 'MISC', name: 'Misc', type: 'Miscellaneous' }, entryDate: '2026-10-18',
  status: 'Draft', totalDebit: 2200, totalCredit: 2200, currencyCode: 'PKR', isManual: true, canEdit: true, reference: 'R1',
  lines: [
    { lineUuid: 'l1', lineNumber: 1, account: { uuid: 'a1', accountCode: '1111', accountName: 'Cash' }, debit: 2200, credit: 0, currencyCode: 'PKR', amountCurrency: 2200 },
    { lineUuid: 'l2', lineNumber: 2, account: { uuid: 'a2', accountCode: '4101', accountName: 'Sales' }, debit: 0, credit: 2200, currencyCode: 'PKR', amountCurrency: -2200 }
  ]
};

describe('GlJournalEntryFormComponent (A47 tasks 255–259)', () => {
  let fixture: ComponentFixture<GlJournalEntryFormComponent>;
  let c: GlJournalEntryFormComponent;
  let api: jasmine.SpyObj<GlJournalService>;
  let navigate: jasmine.Spy;
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(uuid: string | null = null, perms = ['GL_VIEW', 'GL_JOURNAL_CREATE', 'GL_JOURNAL_EDIT', 'GL_JOURNAL_POST'], entry = DRAFT) {
    api = jasmine.createSpyObj<GlJournalService>('GlJournalService', ['getJournalTypes', 'getEntry', 'createEntry', 'updateEntry', 'postEntry']);
    api.getJournalTypes.and.returnValue(of(TYPES));
    api.getEntry.and.returnValue(of(entry));
    const acc = fakeApi();
    acc.getPostableAccounts.and.returnValue(of([{ uuid: 'a1', accountCode: '1111', accountName: 'Cash' }, { uuid: 'a2', accountCode: '4101', accountName: 'Sales' }] as any));
    acc.getFiscalYears.and.returnValue(okRes([{ uuid: 'y', fiscalYearName: 'FY', startDate: '2026-01-01', endDate: '2026-12-31', reportingCurrencyCode: 'PKR', periods: [] } as any]));
    const cur = jasmine.createSpyObj<OrgCurrencyService>('OrgCurrencyService', ['getCurrencies', 'getSettings']);
    cur.getCurrencies.and.returnValue(okRes([{ code: 'PKR', isActive: true }, { code: 'USD', isActive: true }]) as any);
    cur.getSettings.and.returnValue(okRes({ saleBaseCurrencyCode: 'PKR' }) as any);
    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners']);
    partners.getPartners.and.returnValue(okRes({ data: [{ uuid: 'c1', companyName: 'Acme' }], totalRecords: 1 }) as any);
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlJournalEntryFormComponent],
      providers: [provideNoopAnimations(), provideRouter([]), { provide: GlJournalService, useValue: api }, { provide: AccountingService, useValue: acc },
        { provide: OrgCurrencyService, useValue: cur }, { provide: BusinessPartnerService, useValue: partners },
        { provide: AuthService, useValue: fakeAuth(perms) },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap(uuid ? { uuid } : {}) } } }]
    }).compileComponents();
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture = TestBed.createComponent(GlJournalEntryFormComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('a new entry starts on MISC with its default debit account and two lines; running totals and balance warning', async () => {
    await setup();
    expect(c.draft.journalTypeUuid).toBe('tMisc');
    expect(c.draft.lines.length).toBe(2);
    expect(c.draft.lines[0].accountUuid).toBe('a1');
    c.draft.lines[0].debit = 100;
    c.draft.lines[1].accountUuid = 'a2';
    c.draft.lines[1].credit = 90;
    fixture.detectChanges();
    expect(q('total-debit')!.textContent).toContain('100.00');
    expect(q('total-credit')!.textContent).toContain('90.00');
    expect(q('difference')!.textContent).toContain('10.00');
    expect(q('balance-warning')).not.toBeNull();
    c.balanceOn(1);
    fixture.detectChanges();
    expect(c.draft.lines[1].credit).toBe(100);
    expect(q('balance-warning')).toBeNull();
  });

  it('add / remove lines (never below two); typing a debit clears the credit', async () => {
    await setup();
    c.addLine();
    expect(c.draft.lines.length).toBe(3);
    c.removeLine(2);
    c.removeLine(1);
    expect(c.draft.lines.length).toBe(2);
    const l = c.draft.lines[0];
    l.credit = 5; l.debit = 7;
    c.onAmount(l, 'debit');
    expect(l.credit).toBeNull();
  });

  it('save draft: refused while unbalanced; then POST with clientRequestId, blank lines left out, navigates to the entry', async () => {
    await setup();
    c.draft.lines[0].debit = 100;
    c.draft.lines[1].accountUuid = 'a2';
    c.draft.lines[1].credit = 50;
    c.save(false);
    expect(api.createEntry).not.toHaveBeenCalled();
    expect(c.shownProblem).toContain('differ by 50.00');
    c.draft.lines[1].credit = 100;
    c.addLine();
    api.createEntry.and.returnValue(of({ ...DRAFT, uuid: 'new1', entryNumber: 'JV/2026/0004' }));
    c.save(false);
    const body = api.createEntry.calls.mostRecent().args[0];
    expect(body.journalTypeUuid).toBe('tMisc');
    expect(body.clientRequestId).toBe(c.clientRequestId);
    expect(body.postImmediately).toBeFalse();
    expect(body.lines).toEqual([{ accountUuid: 'a1', debit: 100, credit: 0, description: null }, { accountUuid: 'a2', debit: 0, credit: 100, description: null }]);
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/finance/gl/journal-entries', 'new1']);
    // A retry uses the same id (idempotent).
    c.save(false);
    expect(api.createEntry.calls.mostRecent().args[0].clientRequestId).toBe(body.clientRequestId);
  });

  it('a Sale type needs a customer on every line; the partner goes with partnerType Customer', async () => {
    await setup();
    c.draft.journalTypeUuid = 'tSale';
    c.onTypeChange();
    expect(c.fixedPartnerType).toBe('Customer');
    c.draft.lines[0].debit = 10;
    c.draft.lines[1] = { ...c.draft.lines[1], accountUuid: 'a2', credit: 10 };
    c.save(true);
    expect(c.shownProblem).toContain('partner');
    c.draft.lines.forEach(l => c.pickPartner(l, { uuid: 'c1', companyName: 'Acme' } as any));
    api.createEntry.and.returnValue(of({ ...DRAFT, status: 'Posted' }));
    c.save(true);
    const body = api.createEntry.calls.mostRecent().args[0];
    expect(body.postImmediately).toBeTrue();
    expect(body.lines[0]).toEqual(jasmine.objectContaining({ partnerUuid: 'c1', partnerType: 'Customer' }));
  });

  it('a foreign currency needs the rate; the Opening type flags isOpeningEntry and only offers Save and post', async () => {
    await setup();
    c.draft.currencyCode = 'USD';
    c.draft.lines[0].debit = 10;
    c.draft.lines[1] = { ...c.draft.lines[1], accountUuid: 'a2', credit: 10 };
    expect(c.foreign).toBeTrue();
    expect(c.problem).toContain('exchange rate');
    c.draft.exchangeRate = 280;
    c.draft.journalTypeUuid = 'tOpen';
    fixture.detectChanges();
    expect(q('save-draft')).toBeNull();
    expect(q('save-post')).not.toBeNull();
    api.createEntry.and.returnValue(of(DRAFT));
    c.save(true);
    expect(api.createEntry.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ currencyCode: 'USD', exchangeRate: 280, isOpeningEntry: true, isClosingEntry: false }));
  });

  it('server refusals: the message and every listed error', async () => {
    await setup();
    c.draft.lines[0].debit = 10;
    c.draft.lines[1] = { ...c.draft.lines[1], accountUuid: 'a2', credit: 10 };
    api.createEntry.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400, error: { success: false, message: 'Period is locked',
      result: { errorCode: 'JE-007', errors: [{ errorCode: 'JE-007', message: 'Period is locked' }, { errorCode: 'JE-010', message: 'Account is inactive', lineNumber: 2 }] } } })));
    c.save(false);
    fixture.detectChanges();
    expect(q('problem')!.textContent).toContain('Period is locked');
    expect(c.serverErrors).toEqual(['Line 2: Account is inactive']);
  });

  it('edit: loads the draft, PUTs the header and lines, Save and post posts after saving', async () => {
    await setup('e1');
    expect(c.draft.reference).toBe('R1');
    expect(c.draft.lines.map(l => [l.accountUuid, l.debit, l.credit])).toEqual([['a1', 2200, null], ['a2', null, 2200]]);
    api.updateEntry.and.returnValue(of(DRAFT));
    api.postEntry.and.returnValue(of({ ...DRAFT, status: 'Posted' }));
    c.save(true);
    expect(api.updateEntry).toHaveBeenCalledWith('e1', jasmine.objectContaining({ entryDate: '2026-10-18', reference: 'R1' }));
    expect(api.postEntry).toHaveBeenCalledWith('e1');
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/finance/gl/journal-entries', 'e1']);
  });

  it('REV-16: an auto-post type without GL_JOURNAL_POST disables Save and post with a hint and sends nothing', async () => {
    await setup(null, ['GL_JOURNAL_CREATE']);
    c.draft.journalTypeUuid = 'tOpen';
    c.draft.lines[0].debit = 10;
    c.draft.lines[1] = { ...c.draft.lines[1], accountUuid: 'a2', credit: 10 };
    fixture.detectChanges();
    expect(c.autoPostBlocked).toBeTrue();
    expect(q('autopost-no-perm')!.textContent).toContain('posted when saved');
    expect(q('save-draft')).toBeNull();
    c.save(true);
    expect(api.createEntry).not.toHaveBeenCalled();
    expect(c.shownProblem).toContain('permission');
  });

  it('REV-17: editing a closing-adjustment draft on an inactive (not listed) type keeps its flags', async () => {
    await setup('e1', undefined, { ...DRAFT, journalType: { uuid: 'tOld', code: 'OLD', name: 'Old', type: 'Miscellaneous' }, isClosingEntry: true });
    expect(c.type).toBeNull();
    api.updateEntry.and.returnValue(of(DRAFT));
    c.save(false);
    expect(api.updateEntry).toHaveBeenCalledWith('e1', jasmine.objectContaining({ isClosingEntry: true, isOpeningEntry: false }));
  });

  it('a posted entry cannot be edited; without GL_JOURNAL_POST there is no Save and post', async () => {
    await setup('e1', ['GL_JOURNAL_EDIT'], { ...DRAFT, status: 'Posted' });
    expect(c.loadError).toContain('posted');
    await setup(null, ['GL_JOURNAL_CREATE']);
    expect(q('save-post')).toBeNull();
    expect(q('save-draft')).not.toBeNull();
  });
});
