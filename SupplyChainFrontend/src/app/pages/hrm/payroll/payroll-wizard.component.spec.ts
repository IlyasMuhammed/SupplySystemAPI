import { TestBed, discardPeriodicTasks, fakeAsync, flush, tick } from '@angular/core/testing';
import { Router } from '@angular/router';

import { PayrollWizardComponent, postingProblem, stepFor } from './payroll-wizard.component';
import { ReadinessPanelComponent } from './readiness-panel.component';
import { API, Mounted, mountHrm, page, until } from '../core/hrm.testing';

const ALL = ['HRM_SCOPE_ALL', 'HRM_PAYROLL_VIEW', 'HRM_PAYROLL_RUN', 'HRM_PAYROLL_APPROVE', 'HRM_PAYROLL_FINALIZE', 'HRM_PAYROLL_PAY', 'HRM_PAYROLL_REVERSE'];
const ACTIONS: Record<string, string[]> = {
  Draft: ['calculate', 'cancel'], Calculated: ['calculate', 'review', 'cancel'], UnderReview: ['approve', 'reopen', 'cancel'], Approved: ['finalize', 'reopen', 'cancel'],
  Finalized: ['bank-file', 'reverse', 'payslips'], Posted: ['bank-file', 'pay', 'reverse', 'payslips'], Paid: ['bank-file', 'payslips']
};

const run = (status: string, extra: Record<string, unknown> = {}) => ({
  uuid: 'r1', runNumber: 'PR-2026-11', periodStart: '2026-11-01', periodEnd: '2026-11-30', payDate: '2026-11-30', payrollGroup: '', status, currencyCode: 'PKR',
  totalEmployees: 2, blockedEmployees: 0, totalGross: 300000, totalDeductions: 30000, totalNet: 270000, calculatedAt: status === 'Draft' ? null : '2026-11-30T10:00:00Z',
  postings: [], allowedActions: ACTIONS[status] ?? [], costCenterMode: 'CENTRAL', ...extra
});

const LINE = { uuid: 'l1', employeeUuid: 'e1', employeeNumber: 'EMP-1', employeeName: 'Ali', departmentName: 'Sales', workingDays: 30, paidDays: 30, workedDays: 30, gross: 150000, totalDeductions: 15000, net: 135000, isBlocked: false, paymentMethod: 'Bank',
  components: [{ code: 'BASIC', name: 'Basic', type: 'Earning', amount: 150000 }, { code: 'TAX', name: 'Income tax', type: 'Deduction', amount: 15000 }, { code: 'EOBI_ER', name: 'EOBI', type: 'EmployerContribution', amount: 1000 }] };
const BLOCKED = { ...LINE, uuid: 'l2', employeeName: 'Sara', isBlocked: true, blockReason: 'No approved salary assignment', net: 0 };
const READY = { isReady: true, noTarget: false, internalGlActive: true, quickBooksActive: false, internalGlReady: true, quickBooksReady: false, missingGlRoles: [], missingQboAccounts: [], missingCostCenters: [], unverifiedStatutory: [] };

async function open(status: string, perms = ALL, extra: Record<string, unknown> = {}): Promise<Mounted<PayrollWizardComponent>> {
  const m = await mountHrm(PayrollWizardComponent, { perms, params: { uuid: 'r1' } });
  m.flush('hrm/payroll-runs/r1', run(status, extra));
  return m;
}

const click = (m: Mounted<any>, id: string) => (m.q(id)!.querySelector('button') ?? m.q(id)!).click();
const lines = (m: Mounted<any>, data: unknown[]) => { m.http.expectOne(r => r.url === `${API}hrm/payroll-runs/r1/lines`).flush({ success: true, result: page(data) }); m.detect(); };

describe('PayrollWizardComponent (A49 tasks 279–284, 286)', () => {
  it('status decides the step', () => {
    expect(['Draft', 'Calculated', 'UnderReview', 'Approved', 'Finalized', 'Posted', 'Paid'].map(stepFor)).toEqual([2, 3, 4, 5, 6, 6, 6]);
    expect(postingProblem([{ status: 'Failed' }])).toBeTrue();
    expect(postingProblem([{ status: 'NoTarget' }, { status: 'Posted' }])).toBeFalse();
  });

  it('step 1: checks the period, creates, and the URL becomes the run (279)', async () => {
    const m = await mountHrm(PayrollWizardComponent, { perms: ALL });
    const nav = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    expect(m.q('s1')).not.toBeNull();
    m.c.newValues = { periodStart: '2026-11-30', periodEnd: '2026-11-01', payDate: '2026-11-30', payrollGroup: null };
    m.c.create();
    expect(m.c.actionError).toBe('Period end cannot be before its start.');
    m.c.newValues = { periodStart: '2026-11-01', periodEnd: '2026-11-30', payDate: '2026-11-30', payrollGroup: null };
    m.c.create();
    const req = m.expectReq('hrm/payroll-runs', 'POST');
    expect(req.request.body.periodStart).toBe('2026-11-01');
    req.flush({ success: true, result: run('Draft') });
    expect(nav).toHaveBeenCalledWith(['/portal/pages/hrm/payroll/runs', 'r1'], { replaceUrl: true });
    expect(m.c.step).toBe(2);
  });

  it('step 1: the server sentence for a duplicate run (409 PAYROLL_RUN_EXISTS)', async () => {
    const m = await mountHrm(PayrollWizardComponent, { perms: ALL });
    m.c.newValues = { periodStart: '2026-11-01', periodEnd: '2026-11-30', payDate: '2026-11-30', payrollGroup: null };
    m.c.create();
    m.refuse('hrm/payroll-runs', 409, 'PAYROLL_RUN_EXISTS: November already has a run.', 'POST');
    expect(m.text('action-error')).toBe('November already has a run.');
  });

  it('step 2: calculates, summary follows (280)', async () => {
    const m = await open('Draft');
    expect(m.q('s2')).not.toBeNull();
    expect(m.q('summary')).toBeNull();
    click(m, 'calculate');
    m.expectReq('hrm/payroll-runs/r1/calculate', 'POST').flush({ success: true, result: run('Calculated') });
    m.detect();
    expect(m.c.step).toBe(3);
    lines(m, [LINE]);
    expect(m.text('k-gross')).toBe('300,000.00');
    expect(m.text('k-net')).toBe('270,000.00');
  });

  it('step 2: over 200 employees answers 202, the bar follows progress, then the run reloads (280)', fakeAsync(() => {
    let m!: Mounted<PayrollWizardComponent>;
    open('Draft').then(x => m = x);
    tick();
    m.c.calculate();
    m.expectReq('hrm/payroll-runs/r1/calculate', 'POST').flush({ success: true, result: { calculationQueued: true } }, { status: 202, statusText: 'Accepted' });
    tick(0);
    m.flush('hrm/payroll-runs/r1/progress', { status: 'Running', running: true, total: 400, processed: 100 });
    expect(m.c.progressPct).toBe(25);
    expect(m.text('progress')).toContain('100 of 400');
    tick(2000);
    m.flush('hrm/payroll-runs/r1/progress', { status: 'Done', running: false, total: 400, processed: 400 });
    m.expectReq('hrm/payroll-runs/r1', 'GET').flush({ success: true, result: run('Calculated') });
    expect(m.c.progress).toBeNull();
    expect(m.c.step).toBe(3);
    discardPeriodicTasks();
    flush();
    m.http.match(() => true);
  }));

  it('step 3: employee by employee, blocked highlighted with why, search goes to the server (281)', async () => {
    const m = await open('Calculated');
    lines(m, [LINE, BLOCKED]);
    expect(m.text('line-total')).toBe('2');
    expect(m.q('line-l2')!.classList.contains('blocked')).toBeTrue();
    expect(m.text('block-l2')).toContain('No approved salary assignment');
    click(m, 'blocked-only');
    expect(m.http.expectOne(r => r.url === `${API}hrm/payroll-runs/r1/lines`).request.params.get('blockedOnly')).toBe('true');
    m.c.openLine(LINE);
    expect(m.c.earnings(LINE).length).toBe(1);
    expect(m.c.deductions(LINE)[0].code).toBe('TAX');
    expect(m.c.employer(LINE)[0].code).toBe('EOBI_ER');
  });

  it('step 3 → 4: send for approval needs the approver code (no button otherwise)', async () => {
    const viewer = await open('Calculated', ['HRM_PAYROLL_VIEW']);
    lines(viewer, []);
    expect(viewer.q('review')).toBeNull();
    expect(viewer.q('calculate')).toBeNull();
    const m = await open('Calculated');
    lines(m, []);
    click(m, 'review');
    m.expectReq('hrm/payroll-runs/r1/review', 'POST').flush({ success: true, result: run('UnderReview') });
    m.detect();
    expect(m.c.step).toBe(4);
  });

  it('step 4: blocked lines stop approval until left out, and the choice is sent (282)', async () => {
    const m = await open('UnderReview', ALL, { blockedEmployees: 1 });
    lines(m, [BLOCKED]);
    expect(m.text('has-blocked')).toContain('1 employees');
    expect((m.q('approve')!.querySelector('button') as HTMLButtonElement).disabled).toBeTrue();
    m.c.excludeBlocked = true;
    m.detect();
    click(m, 'approve');
    const req = m.expectReq('hrm/payroll-runs/r1/approve', 'POST');
    expect(req.request.body).toEqual({ excludeBlocked: true });
    req.flush({ success: true, result: run('Approved') });
    expect(m.c.step).toBe(5);
    m.http.match(() => true);
  });

  it('step 5: readiness blocks finalize and names what is missing with where to fix it (283, 333)', async () => {
    const m = await open('Approved');
    m.flush('hrm/payroll-runs/r1/posting-readiness', { ...READY, isReady: false, internalGlReady: false, missingGlRoles: ['DEFAULT_SALARY_EXPENSE'], quickBooksActive: true, missingCostCenters: [{ code: 'CC1', name: 'Head office' }], unverifiedStatutory: [{ kind: 'EOBI', name: 'EOBI (UNVERIFIED)' }], currencyMismatch: { orgCurrency: 'PKR', qboCurrency: 'USD' } });
    expect(m.text('missing-gl')).toContain('DEFAULT_SALARY_EXPENSE');
    expect(m.text('missing-cc')).toContain('CC1');
    expect(m.text('currency')).toContain('USD');
    expect(m.text('unverified')).toContain('EOBI');
    expect((m.q('finalize')!.querySelector('button') as HTMLButtonElement).disabled).toBeTrue();
  });

  it('step 5: ready → finalize; the server’s statutory refusal is shown with its list', async () => {
    const m = await open('Approved');
    m.flush('hrm/payroll-runs/r1/posting-readiness', READY);
    expect(m.text('ready')).toContain('Ready to post');
    click(m, 'finalize');
    m.expectReq('hrm/payroll-runs/r1/finalize', 'POST').flush({ success: false, message: 'PAYROLL_STATUTORY_UNVERIFIED: Rates are unverified.', result: [{ kind: 'PF', name: 'Provident fund' }] }, { status: 409, statusText: 'Conflict' });
    m.detect();
    expect(m.text('action-error')).toContain('Rates are unverified.');
    expect(m.text('action-error')).toContain('PF Provident fund');
  });

  it('no target: says so plainly and export stays there (G-19)', async () => {
    const m = await open('Approved');
    m.flush('hrm/payroll-runs/r1/posting-readiness', { ...READY, noTarget: true, internalGlActive: false });
    expect(m.text('no-target')).toContain('No accounting system');
    expect((m.q('finalize')!.querySelector('button') as HTMLButtonElement).disabled).toBeFalse();
    expect(m.q('export-csv')).not.toBeNull();
    click(m, 'export-csv');
    m.expectReq('hrm/payroll-runs/r1/journal-export/csv').flush(new Blob(['x']));
    const f = await open('Finalized', ALL, { allowedActions: ['bank-file', 'pay', 'reverse', 'payslips'], postings: [{ kind: 'PayrollRun', status: 'NoTarget' }] });
    lines(f, []);
    expect(f.text('no-posting')).toContain('No posting required');
    expect(f.q('pay')).not.toBeNull();
    expect(f.q('posting-problem')).toBeNull();
  });

  it('step 6: bank file refused: server words out of the blob; pay needs a reference (284)', async () => {
    const m = await open('Posted');
    lines(m, []);
    click(m, 'bank-file');
    m.expectReq('hrm/payroll-runs/r1/bank-file').flush(new Blob([JSON.stringify({ message: 'PAYROLL_NOT_PAYABLE: Not payable yet.' })]), { status: 409, statusText: 'Conflict' });
    await until(() => !!m.c.actionError);
    m.detect();
    expect(m.text('action-error')).toBe('Not payable yet.');
    click(m, 'pay');
    m.c.saveDlg();
    expect(m.c.dlgError).toBe('Bank reference is required.');
    m.c.dlgValues['reference'] = 'TX-1';
    m.c.saveDlg();
    const req = m.expectReq('hrm/payroll-runs/r1/pay', 'POST');
    expect(req.request.body).toEqual(jasmine.objectContaining({ reference: 'TX-1', paidFromAccount: 'Bank' }));
    req.flush({ success: true, result: run('Paid', { paidAt: '2026-11-30T00:00:00Z', paymentReference: 'TX-1' }) });
    m.http.match(() => true);
    m.detect();
    expect(m.text('paid')).toContain('TX-1');
  });

  it('retry only when a posting failed and allowed; no pay from Finalized with a live target', async () => {
    const m = await open('Finalized', ALL, { allowedActions: ['bank-file', 'reverse', 'payslips', 'retry-posting'], postings: [{ kind: 'PayrollRun', status: 'Failed' }], postingNote: 'QuickBooks is down' });
    lines(m, []);
    expect(m.text('posting-problem')).toContain('QuickBooks is down');
    expect(m.q('pay')).toBeNull();
    click(m, 'retry');
    m.expectReq('hrm/payroll-runs/r1/retry-posting', 'POST').flush({ success: true, result: run('Posted') });
    m.http.match(() => true);
    expect(m.c.run.status).toBe('Posted');
  });

  it('reverse needs a reason and links the new draft (G-12)', async () => {
    const m = await open('Posted');
    lines(m, []);
    click(m, 'reverse');
    m.c.saveDlg();
    expect(m.c.dlgError).toBe('Why is required.');
    m.c.dlgValues['reason'] = 'Wrong rates';
    m.c.saveDlg();
    m.expectReq('hrm/payroll-runs/r1/reverse', 'POST').flush({ success: true, result: run('Reversed', { allowedActions: [], reversalReason: 'Wrong rates', correctionRunUuid: 'r2' }) });
    m.http.match(() => true);
    m.detect();
    expect(m.text('terminal')).toContain('Wrong rates');
    expect(m.c.correctionUuid).toBe('r2');
  });

  it('warns about untracked attendance and server warnings on the run and the line; it does not block', async () => {
    const m = await open('UnderReview', ALL, { untrackedAttendanceEmployees: 3, warnings: ['Two rates expire this month.'] });
    lines(m, [{ ...LINE, attendanceUntracked: true }]);
    expect(m.text('untracked')).toContain('3 employees are paid in full');
    expect(m.text('run-warnings')).toContain('Two rates expire');
    expect(m.q('line-l1')!.classList.contains('untracked')).toBeTrue();
    expect(m.text('untracked-l1')).toContain('No attendance');
    expect((m.q('approve')!.querySelector('button') as HTMLButtonElement).disabled).toBeFalse();
  });

  it('a viewer sees no actions at any step', async () => {
    const m = await open('Posted', ['HRM_PAYROLL_VIEW']);
    lines(m, []);
    for (const id of ['pay', 'bank-file', 'reverse', 'payslips', 'retry']) expect(m.q(id)).withContext(id).toBeNull();
    expect(m.q('journal')).not.toBeNull();
  });

  it('readiness panel shows a load refusal, not a blank', async () => {
    const m = await mountHrm(ReadinessPanelComponent, { perms: ALL });
    m.c.runUuid = 'r1';
    m.c.ngOnChanges();
    m.detect();
    m.refuse('hrm/payroll-runs/r1/posting-readiness', 403, 'No access.');
    expect(m.text('readiness-error')).toBe('No access.');
  });
});
