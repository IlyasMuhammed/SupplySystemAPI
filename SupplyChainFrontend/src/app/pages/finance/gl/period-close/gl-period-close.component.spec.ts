import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { GlPeriodCloseComponent } from './gl-period-close.component';
import { GlJournalService, PeriodCloseOverview, PeriodCloseStatus } from '../../../../services/gl-journal.service';
import { AccountingService } from '../../../../services/accounting.service';
import { AuthService } from '../../../service/auth.service';
import { fakeApi, fakeAuth, okRes } from '../gl.testing';

const period = (n: number, status: string, drafts = 0, allowed?: string[]): PeriodCloseStatus => ({
  uuid: 'p' + n, periodNumber: n, periodName: 'P' + n, startDate: '2026-01-01', endDate: '2026-01-31', status, postedEntryCount: 10, draftEntryCount: drafts,
  hasUnpostedDrafts: drafts > 0, ...(allowed ? { allowedActions: allowed } : {})
});
const overview = (over: Partial<PeriodCloseOverview> = {}): PeriodCloseOverview => ({
  fiscalYear: { uuid: 'y1', fiscalYearName: 'FY 2026', startDate: '2026-01-01', endDate: '2026-12-31', isClosed: false },
  periods: [period(9, 'Locked', 0, ['Reopen', 'FinalClose']), period(10, 'Open', 3, ['Lock', 'AdjustmentOnly']), period(11, 'AdjustmentOnly', 0, ['Lock', 'Reopen'])],
  canCloseYear: false, yearCloseBlockers: ['All periods must be Locked or Closed'],
  defaultRetainedEarningsAccount: { uuid: 're', accountCode: '3201', accountName: 'Retained Earnings' },
  ...over
});

describe('GlPeriodCloseComponent (A47 tasks 393–401)', () => {
  let fixture: ComponentFixture<GlPeriodCloseComponent>;
  let c: GlPeriodCloseComponent;
  let api: jasmine.SpyObj<GlJournalService>;
  let confirm: jasmine.Spy;
  let toast: jasmine.Spy;
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(o: PeriodCloseOverview = overview(), perms = ['GL_PERIOD_CLOSE_VIEW', 'GL_PERIOD_CLOSE_EXECUTE', 'GL_PERIOD_CLOSE_ADMIN']) {
    api = jasmine.createSpyObj<GlJournalService>('GlJournalService',
      ['getPeriodCloseStatus', 'closePeriod', 'reopenPeriod', 'finalClosePeriod', 'previewYearEnd', 'executeYearEnd']);
    api.getPeriodCloseStatus.and.returnValue(of(o));
    const acc = fakeApi();
    acc.getFiscalYears.and.returnValue(okRes([
      { uuid: 'y0', fiscalYearName: 'FY 2025', startDate: '2025-01-01', endDate: '2025-12-31', isClosed: true, periods: [] },
      { uuid: 'y1', fiscalYearName: 'FY 2026', startDate: '2026-01-01', endDate: '2026-12-31', isClosed: false, periods: [] }
    ] as any));
    acc.getPostableAccounts.and.returnValue(of([{ uuid: 're', accountCode: '3201', accountName: 'Retained Earnings', rootType: 'Equity' },
      { uuid: 'cash', accountCode: '1111', accountName: 'Cash', rootType: 'Asset' }] as any));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlPeriodCloseComponent],
      providers: [provideNoopAnimations(), provideRouter([]), { provide: GlJournalService, useValue: api }, { provide: AccountingService, useValue: acc },
        { provide: AuthService, useValue: fakeAuth(perms) }]
    }).compileComponents();
    fixture = TestBed.createComponent(GlPeriodCloseComponent);
    c = fixture.componentInstance;
    confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm').and.callFake((o: any) => { o.accept(); return null as any; });
    toast = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('opens the oldest open year; periods with counts, status, the server allowedActions; drafts warning (tasks 394–397)', async () => {
    await setup();
    expect(c.yearUuid).toBe('y1');
    expect(api.getPeriodCloseStatus).toHaveBeenCalledWith('y1');
    expect(q('status-11')!.textContent).toContain('Adjustments only');
    expect(q('Reopen-9')).not.toBeNull();
    expect(q('FinalClose-9')).not.toBeNull();
    expect(q('Lock-10')).not.toBeNull();
    // PC-001 — a lock is refused while drafts remain: the button is disabled and the warning names the period.
    expect(c.blocked('Lock', c.overview!.periods[1])).toBeTrue();
    expect(q('drafts-10')!.textContent).toContain('3 unposted drafts');
  });

  it('actions call the right route with a confirmation; Final close warns it is final', async () => {
    await setup();
    const res = { period: period(9, 'Closed'), oldStatus: 'Locked', newStatus: 'Closed' };
    api.finalClosePeriod.and.returnValue(of(res));
    c.confirmAction(c.overview!.periods[0], 'FinalClose');
    expect(confirm.calls.mostRecent().args[0].message).toContain('cannot be undone');
    expect(api.finalClosePeriod).toHaveBeenCalledWith('p9');
    api.closePeriod.and.returnValue(of(res));
    c.run(c.overview!.periods[2], 'Lock');
    expect(api.closePeriod).toHaveBeenCalledWith('p11', 'Locked');
    c.run(c.overview!.periods[1], 'AdjustmentOnly');
    expect(api.closePeriod).toHaveBeenCalledWith('p10', 'AdjustmentOnly');
    api.reopenPeriod.and.returnValue(of(res));
    c.run(c.overview!.periods[2], 'Reopen');
    expect(api.reopenPeriod).toHaveBeenCalledWith('p11');
    api.closePeriod.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { success: false, message: 'Period has 3 unposted draft entries' } })));
    c.run(c.overview!.periods[1], 'Lock');
    expect(toast).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'Period has 3 unposted draft entries' }));
  });

  it('without allowedActions: D-11 by state, final close only for the admin code', async () => {
    await setup(overview({ periods: [period(9, 'Locked'), period(10, 'Open')] }), ['GL_PERIOD_CLOSE_EXECUTE']);
    expect(c.actionsFor(c.overview!.periods[0])).toEqual(['Reopen']);
    expect(c.actionsFor(c.overview!.periods[1])).toEqual(['Lock', 'AdjustmentOnly']);
    await setup(overview({ periods: [period(9, 'Locked')] }), ['GL_PERIOD_CLOSE_VIEW']);
    expect(c.actionsFor(c.overview!.periods[0])).toEqual([]);
  });

  it('year-end: retained earnings defaults to DEFAULT_RETAINED_EARNINGS, blockers shown, execute disabled until allowed (task 398)', async () => {
    await setup();
    expect(c.retainedUuid).toBe('re');
    expect(c.equityOptions.map(o => o.value)).toEqual(['re']);
    fixture.detectChanges();
    expect(q('blockers')!.textContent).toContain('All periods must be Locked or Closed');
    expect(c.canExecuteYear).toBeFalse();
  });

  it('preview dialog with lines and net income (task 399); execute confirms and shows the summary (tasks 400–401)', async () => {
    await setup(overview({ canCloseYear: true, yearCloseBlockers: [] }));
    api.previewYearEnd.and.returnValue(of({ totalRevenue: 180000, totalExpenses: 110000, netIncome: 70000, entryDate: '2026-12-31', periodNumber: 13,
      retainedEarningsAccount: { uuid: 're', accountCode: '3201', accountName: 'Retained Earnings' }, canExecute: true, blockers: [],
      lines: [{ account: { uuid: 'r', accountCode: '4101', accountName: 'Sales' }, rootType: 'Revenue', balance: 180000, closingDebit: 180000, closingCredit: 0 }] }));
    c.preview();
    expect(api.previewYearEnd).toHaveBeenCalledWith('y1', 're');
    expect(c.previewVisible).toBeTrue();
    expect(c.previewDebit).toBe(180000);
    expect(c.canExecuteYear).toBeTrue();
    api.executeYearEnd.and.returnValue(of({ closingEntry: { uuid: 'ce', entryNumber: 'CLOSE/2026/0001' }, totalRevenueClosing: 180000,
      totalExpenseClosing: 110000, netIncomeTransferred: 70000, alreadyClosed: false, fiscalYear: { uuid: 'y1', fiscalYearName: 'FY 2026', startDate: '', endDate: '', isClosed: true } }));
    c.confirmExecute();
    expect(confirm.calls.mostRecent().args[0].message).toContain('cannot be undone');
    expect(api.executeYearEnd).toHaveBeenCalledWith('y1', 're');
    expect(c.resultVisible).toBeTrue();
    expect(c.result!.closingEntry!.entryNumber).toBe('CLOSE/2026/0001');
  });

  it('a closed year: no actions, shows the closing entry; no Execute without the admin code', async () => {
    await setup(overview({ fiscalYear: { uuid: 'y1', fiscalYearName: 'FY 2026', startDate: '', endDate: '', isClosed: true },
      closingEntry: { uuid: 'ce', entryNumber: 'CLOSE/2026/0001' } }));
    fixture.detectChanges();
    expect(q('year-done')!.textContent).toContain('CLOSE/2026/0001');
    expect(c.actionsFor(c.overview!.periods[0])).toEqual([]);
    await setup(overview({ canCloseYear: true, yearCloseBlockers: [] }), ['GL_PERIOD_CLOSE_EXECUTE']);
    expect(q('execute')).toBeNull();
    expect(q('preview')).not.toBeNull();
  });
});
