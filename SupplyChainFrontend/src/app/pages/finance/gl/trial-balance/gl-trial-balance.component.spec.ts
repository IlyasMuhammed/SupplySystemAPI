import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse, HttpHeaders, HttpResponse } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { GlTrialBalanceComponent } from './gl-trial-balance.component';
import { GlAccountLedgerComponent } from './gl-account-ledger.component';
import { GlJournalService, TrialBalanceReport } from '../../../../services/gl-journal.service';
import { AccountingService } from '../../../../services/accounting.service';
import { fakeApi, okRes } from '../gl.testing';

const REPORT: TrialBalanceReport = {
  fromDate: '2026-01-01', toDate: '2026-12-31', currencyCode: 'PKR', includeOpeningBalances: true, includeClosingEntries: false,
  totalDebit: 180000, totalCredit: 180000, isBalanced: true,
  lines: [
    { account: { uuid: 'a1', accountCode: '1111', accountName: 'Petty Cash' }, rootType: 'Asset', openingDebit: 0, openingCredit: 0,
      periodDebit: 180000, periodCredit: 0, closingDebit: 180000, closingCredit: 0, balance: 180000 },
    { account: { uuid: 'a2', accountCode: '4101', accountName: 'Sales Revenue' }, rootType: 'Revenue', openingDebit: 0, openingCredit: 0,
      periodDebit: 0, periodCredit: 180000, closingDebit: 0, closingCredit: 180000, balance: -180000 }
  ]
};

const YEAR = { uuid: 'y1', fiscalYearName: 'FY 2026', startDate: '2026-01-01', endDate: '2026-12-31', isClosed: false, reportingCurrencyCode: 'PKR', hasPostings: true,
  periods: [{ uuid: 'p10', periodNumber: 10, periodName: 'Oct 2026', startDate: '2026-10-01', endDate: '2026-10-31', status: 'Open' as const }] };

describe('GlTrialBalanceComponent (A47 tasks 347–355)', () => {
  let fixture: ComponentFixture<GlTrialBalanceComponent>;
  let c: GlTrialBalanceComponent;
  let api: jasmine.SpyObj<GlJournalService>;
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(years: any[] = [YEAR]) {
    api = jasmine.createSpyObj<GlJournalService>('GlJournalService', ['getTrialBalance', 'exportTrialBalance', 'getAccountLedger']);
    api.getTrialBalance.and.returnValue(of(REPORT));
    api.getAccountLedger.and.returnValue(of({ openingBalance: 0, closingBalance: 180000, page: { data: [], totalRecords: 0, page: 1, pageSize: 50 } }));
    const acc = fakeApi();
    acc.getFiscalYears.and.returnValue(okRes(years));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlTrialBalanceComponent],
      providers: [provideNoopAnimations(), provideRouter([]), { provide: GlJournalService, useValue: api }, { provide: AccountingService, useValue: acc }]
    }).compileComponents();
    fixture = TestBed.createComponent(GlTrialBalanceComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('defaults to the current fiscal year; whole year sends fiscalYearUuid, a period its dates (D-FE-9)', async () => {
    await setup();
    expect(c.yearUuid).toBe('y1');
    expect(c.buildQuery()).toEqual({ fiscalYearUuid: 'y1', includeOpeningBalances: true, includeClosingEntries: false, includeZeroBalances: false });
    c.periodUuid = 'p10';
    c.includeClosing = true;
    expect(c.buildQuery()).toEqual(jasmine.objectContaining({ fromDate: '2026-10-01', toDate: '2026-10-31', includeClosingEntries: true }));
  });

  it('date range mode checks the dates with the server texts', async () => {
    await setup();
    c.mode = 'range';
    c.from = '2026-10-31';
    c.to = '2026-10-01';
    expect(c.queryProblem).toBe('The to date must not be before the from date.');
    c.generate();
    expect(api.getTrialBalance).not.toHaveBeenCalled();
    c.to = '2026-11-30';
    c.generate();
    expect(api.getTrialBalance).toHaveBeenCalledWith(jasmine.objectContaining({ fromDate: '2026-10-31', toDate: '2026-11-30' }));
  });

  it('no fiscal year readable: date range of this month', async () => {
    await setup([]);
    expect(c.mode).toBe('range');
    expect(c.from.endsWith('-01')).toBeTrue();
  });

  it('table: negatives in parentheses, totals, balanced indicator (tasks 351, 352, 355)', async () => {
    await setup();
    c.generate();
    fixture.detectChanges();
    expect(q('bal-4101')!.textContent).toContain('(180,000.00)');
    expect(q('bal-1111')!.textContent).toContain('180,000.00');
    expect(q('tb-total-debit')!.textContent).toContain('180,000.00');
    expect(q('tb-total-balance')!.textContent).toContain('0.00');
    expect(q('balanced')!.textContent).toContain('is balanced');
    api.getTrialBalance.and.returnValue(of({ ...REPORT, isBalanced: false, totalCredit: 170000 }));
    c.generate();
    fixture.detectChanges();
    expect(q('balanced')!.textContent).toContain('not balanced');
  });

  it('drill-down opens the account ledger for the report dates (task 353)', async () => {
    await setup();
    c.generate();
    c.drill(REPORT.lines[1]);
    expect(c.ledgerVisible).toBeTrue();
    expect(c.ledgerAccount!.accountCode).toBe('4101');
  });

  it('export CSV: the server file; a missing route falls back to a CSV built here (task 354)', async () => {
    await setup();
    c.generate();
    const blobRes = new HttpResponse({ body: new Blob(['a,b']), headers: new HttpHeaders({ 'content-disposition': 'attachment; filename=tb.csv' }) });
    api.exportTrialBalance.and.returnValue(of(blobRes));
    const click = spyOn(HTMLAnchorElement.prototype, 'click');
    c.exportCsv();
    expect(api.exportTrialBalance).toHaveBeenCalledWith(jasmine.objectContaining({ fiscalYearUuid: 'y1' }));
    api.exportTrialBalance.and.returnValue(throwError(() => new HttpErrorResponse({ status: 404 })));
    c.exportCsv();
    expect(click).toHaveBeenCalledTimes(2);
  });

  it('a server refusal is shown', async () => {
    await setup();
    api.getTrialBalance.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400, error: { message: 'Enter a from and a to date, or a fiscal year.' } })));
    c.generate();
    fixture.detectChanges();
    expect(q('load-error')!.textContent).toContain('fiscal year');
  });
});

describe('GlAccountLedgerComponent (A47 task 353)', () => {
  it('reads the ledger page with the dates and shows opening / running / closing in parentheses', async () => {
    const api = jasmine.createSpyObj<GlJournalService>('GlJournalService', ['getAccountLedger']);
    api.getAccountLedger.and.returnValue(of({ openingBalance: -100, closingBalance: 50, totalDebit: 150, totalCredit: 0,
      page: { data: [{ lineUuid: 'l1', entry: { uuid: 'e1', entryNumber: 'JV/2026/0001' }, entryDate: '2026-10-02', debit: 150, credit: 0, runningBalance: 50, status: 'Reversed' }],
        totalRecords: 120, page: 1, pageSize: 50 } }));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlAccountLedgerComponent],
      providers: [provideNoopAnimations(), provideRouter([]), { provide: GlJournalService, useValue: api }]
    }).compileComponents();
    const fixture = TestBed.createComponent(GlAccountLedgerComponent);
    const c = fixture.componentInstance;
    c.account = { uuid: 'a1', accountCode: '1111', accountName: 'Cash' };
    c.fromDate = '2026-10-01';
    c.toDate = '2026-10-31';
    c.ngOnChanges();
    fixture.detectChanges();
    expect(api.getAccountLedger).toHaveBeenCalledWith('a1', jasmine.objectContaining({ fromDate: '2026-10-01', toDate: '2026-10-31', page: 1, pageSize: 50 }));
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="ledger-opening"]')!.textContent).toContain('(100.00)');
    expect(el.querySelector('[data-testid="lline-l1"]')!.textContent).toContain('Reversed');
    expect(c.total).toBe(120);
    c.onLazyLoad({ first: 50, rows: 50 });
    expect(api.getAccountLedger.calls.mostRecent().args[1]!.page).toBe(2);
    fixture.destroy();
  });
});
