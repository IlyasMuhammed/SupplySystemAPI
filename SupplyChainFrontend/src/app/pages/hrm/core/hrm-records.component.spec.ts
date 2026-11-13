import { HrmRecordsComponent, RecordsConfig } from './hrm-records.component';
import { API, drain, mountHrm, page, until } from './hrm.testing';
import { hrmError, hrmErrorAsync, hrmErrorCode, hrmLabel } from './hrm.shared';
import { cleanBody, formDefaults, formProblem } from './hrm-form.component';
import * as ATT from '../configs/att.config';
import * as LIFE from '../configs/life.config';
import * as ORG from '../configs/org.config';
import * as PAY from '../configs/pay.config';
import * as SELF from '../configs/self.config';
import * as TAL from '../configs/tal.config';

const mount = (config: RecordsConfig, perms: string[]) => mountHrm(HrmRecordsComponent, { perms, data: { config } });

describe('A49 shared words', () => {
  it('strips the code from a coded error and keeps the code readable (contract §0)', () => {
    const err = { error: { message: 'BRANCH_CODE_TAKEN: That code is used by another branch.' } };
    expect(hrmErrorCode(err)).toBe('BRANCH_CODE_TAKEN');
    expect(hrmError(err, 'x')).toBe('That code is used by another branch.');
    expect(hrmError({ status: 0 }, 'x')).toContain('could not be reached');
    expect(hrmError({ status: 500, error: '<html>' }, 'plain')).toBe('plain');
  });

  it('reads a refusal out of a Blob', async () => {
    const blob = new Blob([JSON.stringify({ success: false, message: 'PAYROLL_NOT_PAYABLE: Not yet.' })]);
    expect(await hrmErrorAsync({ status: 409, error: blob }, 'x')).toBe('Not yet.');
    expect(await hrmErrorAsync({ status: 500, error: new Blob(['nonsense']) }, 'fallback')).toBe('fallback');
  });

  it('labels enum spellings for people', () => {
    expect(hrmLabel('UnderReview')).toBe('Under review');
    expect(hrmLabel('M')).toBe('Male');
    expect(hrmLabel('BY_DEPARTMENT')).toBe('By department');
  });

  it('attachment refusals are in plain words', () => {
    expect(hrmError({ error: { message: 'HRM_ATTACHMENT_INVALID: x' } }, 'f')).toContain('Choose the file again');
    expect(hrmError({ error: { message: 'ATTACHMENT_IN_USE: x' } }, 'f')).toContain('it was kept');
  });

  it('form rules: required, range, rows, conditional', () => {
    const f = [{ key: 'a', label: 'A', required: true }, { key: 'n', label: 'N', type: 'number' as const, min: 1 },
      { key: 'x', label: 'X', required: true, visible: (v: any) => v.a === 'show' }, { key: 'r', label: 'R', type: 'rows' as const, rows: [{ key: 'c', label: 'C', required: true }] }];
    expect(formProblem(f, formDefaults(f))).toBe('A is required.');
    expect(formProblem(f, { a: 'y', n: 0 })).toBe('N cannot be below 1.');
    expect(formProblem(f, { a: 'show' })).toBe('X is required.');
    expect(formProblem(f, { a: 'y', r: [{ c: '' }] })).toBe('R row 1: C is required.');
    expect(cleanBody(f, { a: ' y ', n: '3', x: 'hidden', r: [] })).toEqual(jasmine.objectContaining({ a: 'y', n: 3, x: null }));
  });
});

describe('HrmRecordsComponent (A49 engine)', () => {
  afterEach(() => { document.querySelectorAll('.p-dialog-mask').forEach(e => e.remove()); });

  it('lists, shows the status, hides create and edit without the code (076)', async () => {
    const m = await mount(ORG.BRANCHES, ['HRM_ORG_VIEW']);
    m.flush('hrm/branches', [{ uuid: 'b1', code: 'LHR', name: 'Lahore', isActive: true }]);
    expect(m.text('count')).toBe('1');
    expect(m.text('row-b1')).toContain('Lahore');
    expect(m.text('row-b1')).toContain('Active');
    expect(m.q('new')).toBeNull();
    expect(m.q('edit-b1')).toBeNull();
    expect(m.q('active-b1')).toBeNull();
  });

  it('creates through the sdlg dialog with its full markup and shows the server sentence without the code', async () => {
    const m = await mount(ORG.BRANCHES, ['HRM_ORG_VIEW', 'HRM_ORG_MANAGE']);
    m.flush('hrm/branches', []);
    (m.c as any).openNew();
    await m.fixture.whenStable();
    m.detect();
    const dialog = document.body.querySelector('.sdlg') as HTMLElement;
    expect(dialog).withContext('sdlg dialog').not.toBeNull();
    expect(dialog.querySelector('.sdlg-header .sdlg-header-icon')).not.toBeNull();
    expect(dialog.querySelector('.sdlg-body')).not.toBeNull();
    expect(dialog.querySelector('.sdlg-footer .sdlg-footer-btns')).not.toBeNull();

    (m.c as any).saveForm();
    expect((m.c as any).formError).toBe('Code is required.');
    Object.assign((m.c as any).formValues, { code: 'LHR', name: 'Lahore' });
    (m.c as any).saveForm();
    const req = m.expectReq('hrm/branches', 'POST');
    expect(req.request.body).toEqual(jasmine.objectContaining({ code: 'LHR', name: 'Lahore', warehouseUuid: null }));
    req.flush({ success: false, message: 'BRANCH_CODE_TAKEN: That code is taken.' }, { status: 409, statusText: 'Conflict' });
    expect((m.c as any).formError).toBe('That code is taken.');
  });

  it('deactivate confirms, a refusal is a toast, and Show inactive asks the server', async () => {
    const m = await mount(ORG.DESIGNATIONS, ['HRM_ORG_MANAGE']);
    m.flush('hrm/designations', [{ uuid: 'd1', code: 'MGR', name: 'Manager', level: 3, isActive: true }]);
    m.q('active-d1')!.querySelector('button')!.click();
    expect(m.confirm).toHaveBeenCalled();
    const patch = m.expectReq('hrm/designations/d1/active', 'PATCH');
    expect(patch.request.body).toEqual({ isActive: false });
    patch.flush({ success: false, message: 'DESIGNATION_IN_USE: People hold it.' }, { status: 409, statusText: 'Conflict' });
    expect(m.toast).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: 'People hold it.' }));
    m.q('inactive')!.click();
    const list = m.http.expectOne(r => r.url === `${API}hrm/designations`);
    expect(list.request.params.get('includeInactive')).toBe('true');
  });

  it('grade band masked: Hidden, not an empty look (S2)', async () => {
    const m = await mount(ORG.GRADES, ['HRM_ORG_VIEW']);
    m.flush('hrm/grades', [{ uuid: 'g1', code: 'G1', name: 'One', minSalary: null, maxSalary: null, salaryMasked: true, isActive: true }]);
    expect(m.text('row-g1')).toContain('Hidden');
  });

  it('pages and chips go to the server (paged envelope)', async () => {
    const m = await mount(SELF.EMPLOYEES, ['HRM_EMPLOYEE_VIEW']);
    m.flush('hrm/employees', page([{ uuid: 'e1', employeeNumber: 'EMP-0001', fullName: 'Ali', status: 'Active', cnicMasked: '*****-*******-3' }], { totalRecords: 30, totalPages: 2 }));
    drain(m.http);
    m.detect();
    expect(m.text('count')).toBe('30');
    expect(m.text('pager')).toContain('Page 1 of 2');
    expect(m.text('row-e1')).toContain('*****-*******-3');
    m.q('next')!.querySelector('button')!.click();
    expect(m.http.expectOne(r => r.url === `${API}hrm/employees`).request.params.get('page')).toBe('2');
    m.detect();
    m.q('chip-Probation')!.click();
    const req = m.http.expectOne(r => r.url === `${API}hrm/employees`);
    expect(req.request.params.get('status')).toBe('Probation');
    expect(req.request.params.get('page')).toBe('1');
  });

  it('exact CNIC search only with the sensitive code; no New without edit (079)', async () => {
    const m = await mount(SELF.EMPLOYEES, ['HRM_EMPLOYEE_VIEW']);
    m.flush('hrm/employees', page([]));
    drain(m.http); m.detect();
    expect(m.q('filter-cnic')).toBeNull();
    expect(m.q('cmd-new')).toBeNull();
    const n = await mount(SELF.EMPLOYEES, ['HRM_EMPLOYEE_VIEW', 'HRM_EMPLOYEE_EDIT', 'HRM_EMPLOYEE_SENSITIVE_VIEW']);
    n.flush('hrm/employees', page([]));
    drain(n.http); n.detect();
    expect(n.q('filter-cnic')).not.toBeNull();
    expect(n.q('cmd-new')).not.toBeNull();
  });

  it('REV49-12: an exact CNIC is POSTed in the body and never put in the URL', async () => {
    const m = await mount(SELF.EMPLOYEES, ['HRM_EMPLOYEE_VIEW', 'HRM_EMPLOYEE_SENSITIVE_VIEW']);
    m.flush('hrm/employees', page([]));
    drain(m.http); m.detect();
    (m.c as any).q['cnic'] = '35202-1234567-3';
    m.c.load();
    const req = m.http.expectOne(r => r.method === 'POST' && r.url === `${API}hrm/employees/search`);
    expect(req.request.body.cnic).toBe('35202-1234567-3');
    expect(req.request.urlWithParams).not.toContain('35202');
    req.flush({ success: true, result: page([]) });
    drain(m.http);
  });

  it('reject needs a reason and posts it (131)', async () => {
    const m = await mount(ATT.OVERTIME, ['HRM_OVERTIME_APPROVE']);
    m.http.expectOne(r => r.url === `${API}hrm/overtime` && r.params.get('status') === 'Pending').flush({ success: true, result: [{ uuid: 'o1', employeeName: 'Ali', status: 'Pending', minutes: 90 }] });
    drain(m.http); m.detect();
    expect(m.text('row-o1')).toContain('1h 30m');
    m.q('approve-o1')!.querySelector('button')!.click();
    m.expectReq('hrm/overtime/o1/approve', 'POST').flush({ success: true, result: {} });
    m.http.expectOne(r => r.url === `${API}hrm/overtime`).flush({ success: true, result: [{ uuid: 'o1', employeeName: 'Ali', status: 'Pending', minutes: 90 }] });
    m.detect();
    m.q('reject-o1')!.querySelector('button')!.click();
    await m.fixture.whenStable();
    (m.c as any).submitPrompt();
    expect((m.c as any).promptError).toBe('Why is required.');
    (m.c as any).promptValues.note = 'Not agreed';
    (m.c as any).submitPrompt();
    expect(m.expectReq('hrm/overtime/o1/reject', 'POST').request.body).toEqual(jasmine.objectContaining({ note: 'Not agreed' }));
  });

  it('statutory screens say plainly the values are unverified; only the verifier can mark them (199, G-21)', async () => {
    const m = await mount(PAY.STATUTORY_RATES, ['HRM_PAYROLL_CONFIG']);
    m.flush('hrm/statutory-rates', [{ uuid: 'r1', rateType: 'EOBI', name: 'EOBI', isVerified: false }]);
    expect(m.text('notice')).toContain('UNVERIFIED');
    expect(m.text('row-r1')).toContain('UNVERIFIED');
    expect(m.q('verify-r1')).toBeNull();
    const v = await mount(PAY.TAX_TABLES, ['HRM_STATUTORY_VERIFY']);
    v.flush('hrm/tax-tables', [{ uuid: 't1', code: 'T', name: 'T', isVerified: false }]);
    expect(v.q('verify-t1')).not.toBeNull();
    expect(v.q('new')).toBeNull();
  });

  it('a download refusal shows the server words, read out of the blob', async () => {
    const m = await mount(LIFE.LETTERS, ['HRM_LETTER_ISSUE']);
    m.flush('hrm/letters', [{ uuid: 'l1', letterNumber: 'L-1', employeeName: 'Ali', kind: 'Custom' }]);
    drain(m.http); m.detect();
    m.q('pdf-l1')!.querySelector('button')!.click();
    m.expectReq('hrm/letters/l1/pdf').flush(new Blob([JSON.stringify({ message: 'Nope, not yours.' })]), { status: 404, statusText: 'NF' });
    await until(() => m.toast.calls.count() > 0);
    expect(m.toast).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: 'Nope, not yours.' }));
  });

  it('claim pay only for a separate payment, not for the company account (396)', async () => {
    const m = await mount(LIFE.CLAIMS, ['HRM_EXPENSE_PAY', 'HRM_EXPENSE_APPROVE']);
    m.http.expectOne(r => r.url === `${API}hrm/expense-claims`).flush({ success: true, result: page([
      { uuid: 'c1', claimNumber: 'X1', status: 'Approved', settlementMethod: 'SeparatePayment', paymentMode: 'OwnAccount' },
      { uuid: 'c2', claimNumber: 'X2', status: 'Approved', settlementMethod: 'WithPayroll', paymentMode: 'OwnAccount' },
      { uuid: 'c3', claimNumber: 'X3', status: 'Submitted', settlementMethod: 'SeparatePayment', paymentMode: 'OwnAccount', postingState: 'Failed' }
    ]) });
    drain(m.http); m.detect();
    expect(m.q('pay-c1')).not.toBeNull();
    expect(m.q('pay-c2')).toBeNull();
    expect(m.q('approve-c3')).not.toBeNull();
    expect(m.q('retry-c3')).not.toBeNull();
  });

  it('CSV import reports the result (122)', async () => {
    const m = await mount(ATT.ATTENDANCE, ['HRM_ATTENDANCE_VIEW', 'HRM_ATTENDANCE_MANAGE']);
    m.http.expectOne(r => r.url === `${API}hrm/attendance/days`).flush({ success: true, result: page([]) });
    drain(m.http); m.detect();
    const input = m.q('file-import') as HTMLInputElement;
    const dt = new DataTransfer();
    dt.items.add(new File(['x'], 'a.csv'));
    input.files = dt.files;
    input.dispatchEvent(new Event('change'));
    const up = m.expectReq('hrm/attendance/events/import', 'POST');
    expect(up.request.body instanceof FormData).toBeTrue();
    up.flush({ success: true, result: { total: 3, imported: 1, duplicates: 1, errors: [{ row: 2, message: 'Unknown employee' }] } });
    expect(m.toast).toHaveBeenCalledWith(jasmine.objectContaining({ detail: '1 of 3 imported, 1 duplicates, 1 refused (first: row 2, Unknown employee).' }));
  });

  it('monthly summary per employee from a day row (122)', async () => {
    const m = await mount(ATT.ATTENDANCE, ['HRM_ATTENDANCE_VIEW']);
    m.http.expectOne(r => r.url === `${API}hrm/attendance/days`).flush({ success: true, result: page([{ uuid: 'd1', employeeUuid: 'e1', employeeName: 'Ali', date: '2026-03-09', status: 'Present', workedMinutes: 480 }]) });
    drain(m.http); m.detect();
    m.q('summary-d1')!.querySelector('button')!.click();
    const req = m.http.expectOne(r => r.url.includes('hrm/attendance/summary'));
    expect(req.request.url).toContain('employeeUuid=e1&year=2026&month=3');
    req.flush({ success: true, result: { workingDays: 22, presentDays: 20, workedMinutes: 600 } });
    m.detect();
    expect(m.text('view')).toContain('10h 00m');
  });

  it('my interviews: feedback prefilled, interviewer-only body; no link to scoped routes', async () => {
    const ROW = { uuid: 'i1', scheduledAt: '2026-11-05T10:00:00Z', result: 'Pending', score: 40, feedback: 'ok', candidateName: 'Ali', vacancyTitle: 'Clerk', applicationStage: 'Interview', applicationUuid: 'a1' };
    const m = await mount(SELF.MY_INTERVIEWS, ['HRM_RECRUITMENT_VIEW']);
    m.flush('hrm/interviews/me', [ROW]);
    expect(m.q('row-i1')!.querySelector('a')).toBeNull();
    m.q('feedback-i1')!.querySelector('button')!.click();
    await m.fixture.whenStable();
    expect((m.c as any).promptValues).toEqual(jasmine.objectContaining({ result: 'Pending', score: 40, feedback: 'ok' }));
    (m.c as any).promptValues.result = 'Passed';
    (m.c as any).submitPrompt();
    expect(m.expectReq('hrm/interviews/i1', 'PUT').request.body).toEqual(jasmine.objectContaining({ result: 'Passed', scheduledAt: ROW.scheduledAt }));
  });

  it('my interviews: a MANAGE holder sends itself as interviewer, so a full update cannot unassign; failure stops the save', async () => {
    const m = await mount(SELF.MY_INTERVIEWS, ['HRM_RECRUITMENT_MANAGE']);
    m.flush('hrm/interviews/me', [{ uuid: 'i1', scheduledAt: '2026-11-05T10:00:00Z', result: 'Pending' }]);
    m.q('feedback-i1')!.querySelector('button')!.click();
    await m.fixture.whenStable();
    (m.c as any).submitPrompt();
    m.expectReq('hrm/employees/me').flush({ success: false, message: 'HRM_NO_EMPLOYEE: No record.' }, { status: 404, statusText: 'NF' });
    m.http.expectNone(`${API}hrm/interviews/i1`);
    expect((m.c as any).promptError).toBe('No record.');
    (m.c as any).submitPrompt();
    m.expectReq('hrm/employees/me').flush({ success: true, result: { uuid: 'me1' } });
    expect(m.expectReq('hrm/interviews/i1', 'PUT').request.body.interviewerUuid).toBe('me1');
  });

  it('assigned reviews link to the review form, never to employees/{uuid}', async () => {
    const m = await mount(SELF.ASSIGNED_REVIEWS, ['HRM_SELF_SERVICE']);
    m.flush('hrm/reviews/assigned', [{ uuid: 'r1', employeeUuid: 'e1', employeeName: 'Ali', status: 'Draft' }]);
    const links = [...m.q('row-r1')!.querySelectorAll('a')].map(a => a.getAttribute('href'));
    expect(links).toEqual(['/portal/pages/hrm/me/assigned-reviews/r1']);
  });

  it('a load refusal is shown, no spinner left', async () => {
    const m = await mount(TAL.COURSES, ['HRM_TRAINING_VIEW']);
    m.refuse('hrm/courses', 403, 'The module is off.');
    expect(m.text('load-error')).toContain('The module is off.');
    expect(m.q('loading')).toBeNull();
  });

  it('my leave: balances as tiles and the request form posts to the self route (156)', async () => {
    const m = await mount(SELF.MY_LEAVE, ['HRM_SELF_SERVICE']);
    m.http.expectOne(r => r.url === `${API}hrm/leave/balances/me`).flush({ success: true, result: [{ leaveTypeName: 'Annual', balance: 12, used: 2, allocated: 14, pending: 0 }] });
    m.flush('hrm/leave/requests/me', []);
    expect(m.text('tiles')).toContain('Annual');
    expect(m.text('tiles')).toContain('12');
    expect(m.q('cmd-request')).not.toBeNull();
    expect(SELF.LEAVE_REQUEST_FIELDS.find(f => f.key === 'attachmentUuid')!.interfaceCode).toBe('HRM_LEAVE_REQUEST');
    expect(formProblem(SELF.LEAVE_REQUEST_FIELDS, { leaveTypeUuid: 'x', fromDate: '2026-11-02', toDate: '2026-11-01' })).toBe('To cannot be before From.');
    expect(formProblem(SELF.LEAVE_REQUEST_FIELDS, { leaveTypeUuid: 'x', halfDay: true, fromDate: '2026-11-01', toDate: '2026-11-02' })).toBe('A half day is a single date.');
  });

  it('every config asks the contract path for its list, with no request for anything else on open', async () => {
    const all: RecordsConfig[] = [ORG.BRANCHES, ORG.COST_CENTERS, ORG.DESIGNATIONS, ORG.GRADES, ATT.SHIFTS, ATT.HOLIDAYS, ATT.ATTENDANCE, ATT.OVERTIME, ATT.LEAVE_TYPES, ATT.LEAVE_APPROVALS,
      PAY.COMPONENTS, PAY.TAX_TABLES, PAY.STATUTORY_RATES, PAY.PAYROLL_RUNS, PAY.ADJUSTMENTS, TAL.VACANCIES, TAL.CANDIDATES, TAL.CYCLES, TAL.REVIEWS, TAL.COURSES, TAL.ENROLLMENTS,
      LIFE.LOANS, LIFE.SETTLEMENTS, LIFE.INCREMENTS, LIFE.LETTER_TEMPLATES, LIFE.LETTERS, LIFE.TAX_CERTIFICATES, LIFE.CLAIMS, LIFE.CATEGORIES, LIFE.REMITTANCES, LIFE.REMITTANCE_TRACKER,
      SELF.MY_ATTENDANCE, SELF.MY_PAYSLIPS, SELF.MY_EXPENSES, SELF.MY_LOANS, SELF.MY_REVIEWS, SELF.MY_TRAINING, SELF.MY_LETTERS, SELF.MY_TAX_CERTS, SELF.MY_INTERVIEWS, SELF.ASSIGNED_REVIEWS, SELF.STRUCTURES, SELF.COST_CENTER_MAP];
    const paths = new Set<string>();
    for (const cfg of all) {
      const m = await mount(cfg, []);
      const req = m.http.expectOne(r => r.url === API + cfg.listPath, cfg.id);
      paths.add(cfg.listPath);
      req.flush({ success: true, result: [] });
      drain(m.http);
      m.detect();
      expect(m.text('empty')).withContext(cfg.id).not.toBe('');
      m.http.verify();
    }
    expect(paths.size).toBe(all.length);
  });
});
