import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { ConfirmationService, MessageService } from 'primeng/api';
import { throwError } from 'rxjs';

import { GlFiscalYearsComponent, defaultYearEnd, periodActions, yearProblem } from './gl-fiscal-years.component';
import { AccountingService, FiscalYear } from '../../../../services/accounting.service';
import { OrgCurrencyService } from '../../../../services/org-currency.service';
import { AuthService } from '../../../service/auth.service';
import { fakeApi, fakeAuth, okRes } from '../gl.testing';

const year = (over: Partial<FiscalYear> = {}): FiscalYear => ({
  uuid: 'y1', fiscalYearName: 'FY 2026-27', startDate: '2026-07-01', endDate: '2027-06-30', isClosed: false, reportingCurrencyCode: 'PKR',
  hasPostings: false,
  periods: [
    { uuid: 'p2', periodNumber: 2, periodName: 'Aug 2026', startDate: '2026-08-01', endDate: '2026-08-31', status: 'Locked' },
    { uuid: 'p1', periodNumber: 1, periodName: 'Jul 2026', startDate: '2026-07-01', endDate: '2026-07-31', status: 'Open' },
    { uuid: 'p3', periodNumber: 3, periodName: 'Sep 2026', startDate: '2026-09-01', endDate: '2026-09-30', status: 'Closed' }
  ],
  ...over
});

describe('GlFiscalYearsComponent (A42 fiscal years)', () => {
  let fixture: ComponentFixture<GlFiscalYearsComponent>;
  let c: GlFiscalYearsComponent;
  let api: jasmine.SpyObj<AccountingService>;

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(years: FiscalYear[] = [year()], perms = ['GL_VIEW', 'GL_SETUP_MANAGE']) {
    api = fakeApi();
    api.getFiscalYears.and.returnValue(okRes(years));
    const currency = jasmine.createSpyObj<OrgCurrencyService>('OrgCurrencyService', ['getCurrencies', 'getSettings']);
    currency.getCurrencies.and.returnValue(okRes([{ code: 'PKR', isActive: true }, { code: 'USD', isActive: true }]) as any);
    currency.getSettings.and.returnValue(okRes({ saleBaseCurrencyCode: 'PKR' }) as any);
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlFiscalYearsComponent],
      providers: [provideNoopAnimations(), MessageService, { provide: AccountingService, useValue: api },
        { provide: OrgCurrencyService, useValue: currency }, { provide: AuthService, useValue: fakeAuth(perms) }]
    }).compileComponents();
    fixture = TestBed.createComponent(GlFiscalYearsComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('a card per year with dates, currency, Active status and periods in order with status pills', async () => {
    await setup();
    const card = q('year-FY 2026-27')!;
    expect(card.textContent).toContain('PKR');
    expect(q('year-status')!.textContent).toContain('Active');
    expect(c.years[0].periods.map(p => p.periodNumber)).toEqual([1, 2, 3]);
    expect(q('period-3')!.querySelector('[data-testid="period-status"]')!.className).toContain('er');
  });

  it('period actions follow D-18: Open → Lock/Close, Locked → Reopen/Close, Closed → none', async () => {
    expect(periodActions('Open')).toEqual(['Lock', 'Close']);
    expect(periodActions('Locked')).toEqual(['Reopen', 'Close']);
    expect(periodActions('Closed')).toEqual([]);
    await setup();
    expect(q('lock-1')).not.toBeNull();
    expect(q('reopen-2')).not.toBeNull();
    expect(q('close-3')).toBeNull();
  });

  it('a period change asks first; Close warns it is final', async () => {
    await setup();
    const confirm = fixture.debugElement.injector.get(ConfirmationService);
    const spy = spyOn(confirm, 'confirm').and.callFake((o: any) => { o.accept(); return confirm; });
    api.updatePeriod.and.returnValue(okRes({ ...c.years[0].periods[0], status: 'Closed' as const }));
    c.confirmPeriod(c.years[0].periods[0], 'Close');
    expect(spy.calls.mostRecent().args[0].message).toContain('cannot be reopened');
    expect(api.updatePeriod).toHaveBeenCalledWith('p1', { status: 'Closed' });
    // D-51 — a neighbour may have moved: the years are reloaded after every period PUT.
    expect(api.getFiscalYears).toHaveBeenCalledTimes(2);
  });

  it('new year: currency defaults to the base currency, the end date to 12 months, then creates it', async () => {
    await setup([]);
    expect(q('no-years')).not.toBeNull();
    c.openNew();
    expect(c.draft.reportingCurrencyCode).toBe('PKR');
    c.draft.fiscalYearName = 'FY 2026-27';
    c.draft.startDate = new Date(2026, 6, 1);
    c.draft.endDate = null;
    c.onStartChange();
    expect(c.draft.endDate!.getMonth()).toBe(5);
    expect(c.draft.endDate!.getDate()).toBe(30);
    api.createFiscalYear.and.returnValue(okRes(year()));
    c.save();
    expect(api.createFiscalYear).toHaveBeenCalledWith({ fiscalYearName: 'FY 2026-27', startDate: '2026-07-01', endDate: '2027-06-30', reportingCurrencyCode: 'PKR' });
  });

  it('the next year starts the day after the last one ends', async () => {
    await setup();
    c.openNew();
    expect(c.draft.startDate!.getFullYear()).toBe(2027);
    expect(c.draft.startDate!.getMonth()).toBe(6);
    expect(c.draft.startDate!.getDate()).toBe(1);
    expect(defaultYearEnd(new Date(2027, 0, 1)).getDate()).toBe(31);
  });

  it('client checks and server refusals (overlap) are shown in the dialog', async () => {
    await setup();
    expect(yearProblem({ fiscalYearName: '', startDate: null, endDate: null, reportingCurrencyCode: null })).toContain('name');
    const ok = { startDate: new Date(2026, 6, 1), endDate: new Date(2027, 5, 30), reportingCurrencyCode: 'PKR' };
    expect(yearProblem({ ...ok, fiscalYearName: 'x'.repeat(100) })).toBeNull();
    expect(yearProblem({ ...ok, fiscalYearName: 'x'.repeat(101) })).toBe('The name can be at most 100 characters.');
    // A new year may be left unnamed (the server names it); at most 12 months.
    expect(yearProblem({ ...ok, fiscalYearName: '' }, true)).toBeNull();
    expect(yearProblem({ ...ok, fiscalYearName: 'FY', endDate: new Date(2027, 6, 1) })).toBe('A fiscal year can be at most 12 months long.');
    expect(yearProblem({ fiscalYearName: 'X', startDate: new Date(2026, 1, 1), endDate: new Date(2026, 0, 1), reportingCurrencyCode: 'PKR' }))
      .toBe('The end date must be after the start date.');
    c.openNew();
    c.draft = { fiscalYearName: 'FY', startDate: new Date(2026, 6, 1), endDate: new Date(2027, 5, 30), reportingCurrencyCode: 'PKR' };
    api.createFiscalYear.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { message: 'Fiscal year dates overlap with an existing year.' } })));
    c.save();
    fixture.detectChanges();
    expect(q('fy-problem')?.textContent ?? c.shownProblem).toContain('overlap');
  });

  it('edit: dates are not sent once the year has postings', async () => {
    await setup([year({ hasPostings: true })]);
    c.openEdit(c.years[0]);
    expect(c.datesLocked).toBeTrue();
    api.updateFiscalYear.and.returnValue(okRes(year()));
    c.save();
    expect(api.updateFiscalYear).toHaveBeenCalledWith('y1', { fiscalYearName: 'FY 2026-27', reportingCurrencyCode: 'PKR' });
  });

  it('close year confirms and shows the server 409 as is', async () => {
    await setup();
    const confirm = fixture.debugElement.injector.get(ConfirmationService);
    spyOn(confirm, 'confirm').and.callFake((o: any) => { o.accept(); return confirm; });
    const add = spyOn(TestBed.inject(MessageService), 'add');
    api.closeFiscalYear.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { message: 'All periods must be closed first.' } })));
    c.confirmCloseYear(c.years[0]);
    expect(add).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'All periods must be closed first.' }));
  });

  it('read-only: no New / Edit / period actions', async () => {
    await setup([year()], ['GL_VIEW']);
    expect(q('new-year')).toBeNull();
    expect(q('lock-1')).toBeNull();
  });
});
