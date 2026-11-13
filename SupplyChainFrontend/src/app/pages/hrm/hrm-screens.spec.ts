import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';

import { ModuleAdminService, ModuleService } from '../../services/module.service';
import { API, drain, mountHrm, Mounted } from './core/hrm.testing';
import { DepartmentsComponent, DeptNode, subtreeOf } from './org/departments.component';
import { HrmSetupComponent } from './setup/hrm-setup.component';
import { HrSettingsComponent } from './setup/hr-settings.component';
import { LeaveDashboardComponent, monthGrid } from './leave/leave-dashboard.component';
import { LeaveBalancesComponent } from './leave/leave-balances.component';
import { BulkRevisionComponent, FormulaTesterComponent, formulaVariables } from './salary/salary-tools.component';
import { STAGES, VacancyBoardComponent } from './talent/vacancy-board.component';
import { CandidateDetailComponent } from './talent/candidate-detail.component';
import { ReviewFormComponent, weightedRating } from './talent/review-form.component';
import { PerformanceDashboardComponent, completionPercent, distributionRows } from './talent/performance-dashboard.component';
import { MyProfileComponent } from './self/my-profile.component';
import { HrmApi } from './core/hrm.api';
import { Field, HrmFormComponent, formProblem, markFilesSaved } from './core/hrm-form.component';
import { PERSONAL_FIELDS } from './employees/employee.fields';
import { CANDIDATE_FIELDS } from './configs/tal.config';
import * as ORG from './configs/org.config';
import * as PAY from './configs/pay.config';
import * as SELF from './configs/self.config';
import * as ATT from './configs/att.config';

const click = (m: Mounted<any>, id: string) => (m.q(id)!.querySelector('button') ?? m.q(id)!).click();
const node = (uuid: string, children: DeptNode[] = [], parentUuid: string | null = null): DeptNode => ({ uuid, code: uuid.toUpperCase(), name: `Dept ${uuid}`, parentUuid, isActive: true, employeeCount: 1, children });

describe('DepartmentsComponent (077)', () => {
  const TREE = [node('a', [node('b', [node('c', [], 'b')], 'a')]), node('d')];

  it('moves by drag, and refuses to drop into its own subtree', async () => {
    const m = await mountHrm(DepartmentsComponent, { perms: ['HRM_ORG_MANAGE'] });
    m.flush('hrm/departments/tree', TREE);
    expect(subtreeOf(TREE[0]).has('c')).toBeTrue();
    const evt = (): any => ({ preventDefault() {}, stopPropagation() {}, dataTransfer: { setData() {}, effectAllowed: '' } });
    m.c.dragStart(evt(), TREE[0]);
    expect(m.c.allowed(TREE[0].children[0])).toBeFalse();
    m.c.drop(evt(), TREE[0].children[0]);
    m.http.expectNone(`${API}hrm/departments/a`);
    m.c.dragStart(evt(), TREE[1]);
    expect(m.c.allowed(TREE[0])).toBeTrue();
    m.c.drop(evt(), TREE[0]);
    const put = m.expectReq('hrm/departments/d', 'PUT');
    expect(put.request.body).toEqual(jasmine.objectContaining({ parentUuid: 'a', code: 'D', name: 'Dept d' }));
    put.flush({ success: false, message: 'DEPARTMENT_CYCLE: That would make a loop.' }, { status: 400, statusText: 'Bad' });
    expect(m.toast).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'That would make a loop.' }));
  });

  it('keyboard alternative: Move offers only legal parents, top level included, and saves through the same route', async () => {
    const m = await mountHrm(DepartmentsComponent, { perms: ['HRM_ORG_MANAGE'] });
    m.flush('hrm/departments/tree', TREE);
    const b = TREE[0].children[0];
    const values = m.c.moveOptions(b).map(o => o.value);
    expect(values).toEqual(['', 'a', 'd']);
    expect(m.q('move-b')!.querySelector('button')).not.toBeNull();
    click(m, 'move-b');
    m.detect();
    expect(m.c.moveValues['parent']).toBe('a');
    m.c.moveValues['parent'] = '';
    m.c.saveMove();
    expect(m.expectReq('hrm/departments/b', 'PUT').request.body.parentUuid).toBeNull();
  });

  it('a viewer sees the tree with no way to change it', async () => {
    const m = await mountHrm(DepartmentsComponent, { perms: ['HRM_ORG_VIEW'] });
    m.flush('hrm/departments/tree', TREE);
    expect(m.q('node-a')).not.toBeNull();
    expect(m.q('new')).toBeNull();
    expect(m.q('move-a')).toBeNull();
    expect(m.q('node-a')!.querySelector('.hrm-node')!.getAttribute('draggable')).toBe('false');
  });
});

describe('HrmSetupComponent (E-3, FD-9)', () => {
  const STATUS = { licensed: true, enabled: false, payrollCostCenter: null, mode: 'CENTRAL', costCenters: [{ uuid: 'cc1', code: 'HO', name: 'Head office', isActive: true }] };
  const ALL = ['HRM_SETTINGS_MANAGE', 'HRM_ORG_MANAGE', 'MODULES_MANAGE'];

  async function open(perms: string[], status: any, enable = of({ success: true } as any)) {
    const spy = { enable: jasmine.createSpy('enable').and.returnValue(enable) };
    const modules = { refresh: jasmine.createSpy('refresh') };
    const m = await mountHrm(HrmSetupComponent, { perms, providers: [{ provide: ModuleAdminService, useValue: spy }, { provide: ModuleService, useValue: modules }] });
    m.flush('hrm/setup/status', status);
    return { m, spy, modules };
  }

  it('existing cost center: sets it, then switches on, refreshes the menu and moves on', async () => {
    const { m, spy, modules } = await open(ALL, STATUS);
    const nav = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    expect(m.c.choice).toBe('existing');
    expect((m.q('finish')!.querySelector('button') as HTMLButtonElement).disabled).toBeTrue();
    m.c.selected = 'cc1';
    m.c.mode = 'BY_DEPARTMENT';
    m.detect();
    click(m, 'finish');
    const req = m.expectReq('hrm/setup/payroll-cost-center', 'POST');
    expect(req.request.body).toEqual({ costCenterUuid: 'cc1', mode: 'BY_DEPARTMENT' });
    expect(spy.enable).not.toHaveBeenCalled();
    req.flush({ success: true, result: {} });
    expect(spy.enable).toHaveBeenCalledWith('MODULE_HRM');
    expect(modules.refresh).toHaveBeenCalled();
    expect(nav).toHaveBeenCalledWith(['/portal/pages/hrm/org/branches']);
  });

  it('no cost centers yet: straight to create new, sent inline', async () => {
    const { m } = await open(ALL, { ...STATUS, costCenters: [] });
    expect(m.c.choice).toBe('new');
    m.c.newCc = { code: '', name: '' };
    m.c.finish();
    expect(m.c.error).toBe('Code is required.');
    m.c.newCc = { code: 'PR', name: 'Payroll' };
    m.c.finish();
    expect(m.expectReq('hrm/setup/payroll-cost-center', 'POST').request.body).toEqual({ newCostCenter: { code: 'PR', name: 'Payroll' }, mode: 'CENTRAL' });
  });

  it('no create permission and none to pick: says who can', async () => {
    const { m } = await open(['HRM_SETTINGS_MANAGE', 'MODULES_MANAGE'], { ...STATUS, costCenters: [] });
    expect(m.text('cannot-create')).toContain('no cost centers');
    expect((m.q('finish')!.querySelector('button') as HTMLButtonElement).disabled).toBeTrue();
  });

  it('the server’s 422 still refuses the switch: plain words, not a code', async () => {
    const refusal = throwError(() => ({ status: 422, error: { message: 'PAYROLL_COST_CENTER_REQUIRED: Set one.' } }));
    const { m } = await open(['MODULES_MANAGE'], { ...STATUS, payrollCostCenter: { uuid: 'cc1', code: 'HO', name: 'Head office' } }, refusal);
    expect(m.text('cc-set')).toContain('HO');
    click(m, 'finish');
    m.detect();
    expect(m.text('error')).toContain('still has no payroll cost center');
  });

  it('not licensed, and already on, each say so with no switch', async () => {
    const a = await open(ALL, { ...STATUS, licensed: false });
    expect(a.m.text('not-licensed')).toContain('plan');
    expect(a.m.q('finish')).toBeNull();
    const b = await open(ALL, { ...STATUS, enabled: true, payrollCostCenter: { uuid: 'cc1', code: 'HO', name: 'H' } });
    expect(b.m.text('already-on')).toContain('HO');
    expect(b.m.q('finish')).toBeNull();
  });
});

describe('HrSettingsComponent', () => {
  it('saves only the writable settings; a read-only holder gets a read-only view', async () => {
    const m = await mountHrm(HrSettingsComponent, { perms: ['HRM_SETTINGS_MANAGE'] });
    m.flush('hrm/settings', { payrollCostCenterUuid: 'cc1', payrollCostCenterCode: 'HO', payrollCostCenterMode: 'CENTRAL', postDatePolicy: 'PayDate', employeeNumberPrefix: 'EMP', countryCode: 'PK', defaultExpenseSettlement: 'WithPayroll', enablementCompletedAt: 'x' });
    drain(m.http); m.detect();
    click(m, 'save');
    const put = m.expectReq('hrm/settings', 'PUT');
    expect(put.request.body.payrollCostCenterUuid).toBe('cc1');
    expect(put.request.body.enablementCompletedAt).toBeUndefined();
    expect(put.request.body.payrollCostCenterCode).toBeUndefined();
    // REV49B-2: HrmSettingsUpdate has no CountryCode: shown, never sent.
    expect('countryCode' in put.request.body).toBeFalse();
    expect(m.text('country')).toContain('PK');
    const r = await mountHrm(HrSettingsComponent, { perms: ['HRM_PAYROLL_VIEW'] });
    r.flush('hrm/settings', { payrollCostCenterName: 'Head office', payrollCostCenterMode: 'CENTRAL' });
    drain(r.http); r.detect();
    expect(r.text('readonly')).toContain('Head office');
    expect(r.q('save')).toBeNull();
    expect(r.q('scopes')).toBeNull();
  });
});

describe('HrSettingsComponent user picker (FE-4)', () => {
  it('reads the scope users route, searches on the server', async () => {
    const m = await mountHrm(HrSettingsComponent, { perms: ['HRM_SETTINGS_MANAGE'] });
    m.flush('hrm/user-scopes/users', [{ userId: 7, name: 'Ali', email: 'a@x.test' }]);
    expect(m.c.users).toEqual([{ label: 'Ali (a@x.test)', value: 7 }]);
    m.c.userSearch = ' ali ';
    m.c.loadUsers();
    expect(m.http.match(r => r.url === `${API}hrm/user-scopes/users` && r.params.get('search') === 'ali').length).toBe(1);
  });
});

describe('Form limits match the server (REV49B-9)', () => {
  it('names 60 and last name required for people; names 100 and codes 20 for masters', () => {
    const emp = PERSONAL_FIELDS;
    const first = emp.find(f => f.key === 'firstName')!;
    const last = emp.find(f => f.key === 'lastName')!;
    expect([first.maxLength, last.maxLength, last.required]).toEqual([60, 60, true]);
    const cand = CANDIDATE_FIELDS.find(f => f.key === 'lastName')!;
    expect([cand.maxLength, cand.required]).toEqual([60, true]);
    for (const cfg of [ORG.BRANCHES, ORG.DESIGNATIONS, ORG.GRADES, PAY.COMPONENTS, SELF.STRUCTURES, ATT.HOLIDAYS]) {
      const fields = cfg.form!.fields;
      const name = fields.find(f => f.key === 'name');
      const code = fields.find(f => f.key === 'code');
      if (name) expect(name.maxLength).withContext(`${cfg.id} name`).toBeLessThanOrEqual(100);
      if (code) expect(code.maxLength).withContext(`${cfg.id} code`).toBeLessThanOrEqual(20);
    }
    expect(formProblem([{ key: 'n', label: 'Name', maxLength: 3 }], { n: 'abcd' })).toBe('Name can be at most 3 characters.');
    expect(formProblem([{ key: 'n', label: 'Name', required: true }], { n: '  ' })).toBe('Name is required.');
  });
});

describe('Form files (D-25)', () => {
  const FIELDS: Field[] = [{ key: 'f', label: 'File', type: 'file', interfaceCode: 'HRM_LEAVE_REQUEST' }];
  const pickFile = (m: Mounted<any>) => { const ev = { target: { files: [new File(['x'], 'a.pdf')] } } as any; m.c.pick(FIELDS[0], ev); };

  async function form(values: Record<string, any>) {
    const m = await mountHrm(HrmFormComponent, { perms: [] });
    m.fixture.componentRef.setInput('fields', FIELDS);
    m.fixture.componentRef.setInput('values', values);
    m.detect();
    return m;
  }
  const answer = (m: Mounted<any>, id: string) => m.http.expectOne(`${API}attachments/upload`).flush({ success: true, result: id });

  it('replacing an upload removes the earlier one; never a file the row already held', async () => {
    const values = { f: 'held' };
    const m = await form(values);
    pickFile(m);
    answer(m, 'u1');
    m.http.expectNone(`${API}hrm/attachments/held`);
    expect(values.f).toBe('u1');
    pickFile(m);
    answer(m, 'u2');
    expect(m.http.expectOne(`${API}hrm/attachments/u1`).request.method).toBe('DELETE');
    m.fixture.destroy();
    expect(m.http.expectOne(`${API}hrm/attachments/u2`).request.method).toBe('DELETE');
  });

  it('a saved form keeps its file', async () => {
    const values = { f: null };
    const m = await form(values);
    pickFile(m);
    answer(m, 'u1');
    markFilesSaved(values);
    m.fixture.destroy();
    m.http.expectNone(`${API}hrm/attachments/u1`);
  });

  it('HRM files read through the HRM route', async () => {
    const m = await form({ f: null });
    TestBed.inject(HrmApi).attachmentBlob('a1').subscribe();
    m.http.expectOne(`${API}hrm/attachments/a1/content`);
  });
});

describe('Leave pages (155, 157)', () => {
  it('month grid: leading blanks, a request spans its dates', () => {
    const g = monthGrid(2026, 11, [{ fromDate: '2026-11-02', toDate: '2026-11-03', employeeName: 'Ali' }]);
    const days = g.filter(Boolean) as any[];
    expect(days.length).toBe(30);
    expect(days[1].names).toEqual(['Ali']);
    expect(days[2].names).toEqual(['Ali']);
    expect(days[3].names).toEqual([]);
    expect(g.length).toBe(30 + new Date(2026, 10, 1).getDay());
  });

  it('calendar loads the month and moves', async () => {
    const m = await mountHrm(LeaveDashboardComponent, { perms: ['HRM_LEAVE_VIEW'] });
    drain(m.http);
    m.detect();
    m.c.year = 2026; m.c.month = 12;
    m.c.shift(1);
    expect(m.c.year).toBe(2027);
    expect(m.c.month).toBe(1);
    const req = m.http.expectOne(r => r.url === `${API}hrm/leave/calendar`);
    expect(req.request.params.get('from')).toBe('2027-01-01');
    expect(req.request.params.get('to')).toBe('2027-01-31');
  });

  it('balances wait for an employee; the ledger opens per type', async () => {
    const m = await mountHrm(LeaveBalancesComponent, { perms: ['HRM_LEAVE_VIEW'] });
    drain(m.http);
    m.c.employeeUuid = 'e1';
    m.c.load();
    m.http.expectOne(r => r.url === `${API}hrm/leave/balances` && r.params.get('employeeUuid') === 'e1').flush({ success: true, result: [{ leaveTypeUuid: 't1', leaveTypeName: 'Annual', balance: 3 }] });
    m.detect();
    expect(m.text('balances')).toContain('Annual');
    click(m, 'ledger-t1');
    expect(m.http.expectOne(r => r.url === `${API}hrm/leave/ledger`).request.params.get('leaveTypeUuid')).toBe('t1');
  });
});

describe('Salary tools (181, 192)', () => {
  it('asks only for the variables the formula uses; the server decides', async () => {
    expect(formulaVariables('ROUND(MAX(basic * 0.1, HOUSE), 2)')).toEqual(['BASIC', 'HOUSE']);
    const m = await mountHrm(FormulaTesterComponent, { perms: ['HRM_PAYROLL_CONFIG'] });
    m.c.formula = 'BASIC * 0.1';
    m.c.sync();
    m.c.values['BASIC'] = 1000;
    m.c.test();
    const req = m.expectReq('hrm/salary-formula/test', 'POST');
    expect(req.request.body).toEqual({ formula: 'BASIC * 0.1', variables: { BASIC: 1000 } });
    req.flush({ success: true, result: { valid: true, result: 100 } });
    m.detect();
    expect(m.text('result')).toContain('100');
    m.c.test();
    m.expectReq('hrm/salary-formula/test', 'POST').flush({ success: true, result: { valid: false, error: 'Unknown name HOUSE' } });
    m.detect();
    expect(m.text('error')).toBe('Unknown name HOUSE');
  });

  it('bulk revision: percent or amount, not both; the list of people goes as uuids', async () => {
    const m = await mountHrm(BulkRevisionComponent, { perms: ['HRM_SALARY_EDIT'] });
    drain(m.http);
    Object.assign(m.c.values, { percent: 10, amount: 500, reason: 'Annual', employees: [{ employeeUuid: 'e1' }] });
    m.c.save();
    expect(m.c.error).toBe('Give a percent or an amount, not both.');
    m.c.values['amount'] = null;
    m.c.save();
    const req = m.expectReq('hrm/salary-assignments/bulk-revision', 'POST');
    expect(req.request.body).toEqual(jasmine.objectContaining({ percent: 10, employeeUuids: ['e1'], componentCode: 'BASIC' }));
    req.flush({ success: true, result: [{}] });
    m.detect();
    expect(m.text('done')).toContain('1 draft assignment');
  });
});

describe('VacancyBoardComponent (209)', () => {
  const BOARD = [{ stage: 'New', applications: [{ uuid: 'a1', candidateUuid: 'c1', candidateName: 'Ali', stage: 'New' }] }, { stage: 'Offer', applications: [{ uuid: 'a2', candidateUuid: 'c2', candidateName: 'Sara', stage: 'Offer' }] }];

  async function open(perms: string[]) {
    const m = await mountHrm(VacancyBoardComponent, { perms, params: { uuid: 'v1' } });
    m.flush('hrm/vacancies/v1', { uuid: 'v1', title: 'Clerk', status: 'Open', positions: 1 });
    m.flush('hrm/vacancies/v1/board', BOARD);
    return m;
  }

  it('every stage is a column, in order, even when the server sends only some', async () => {
    const m = await open(['HRM_RECRUITMENT_VIEW']);
    expect(m.c.columns.map(c => c.stage)).toEqual(STAGES);
    expect(m.text('col-New')).toContain('Ali');
  });

  it('keyboard alternative: the card\'s own list moves it through the same route as a drag', async () => {
    const m = await open(['HRM_RECRUITMENT_MANAGE']);
    const select = m.q('stage-a1') as HTMLSelectElement;
    expect(select.tagName).toBe('SELECT');
    expect([...select.options].map(o => o.value)).not.toContain('Hired');
    select.value = 'Screening';
    select.dispatchEvent(new Event('change'));
    expect(m.expectReq('hrm/applications/a1/stage', 'PATCH').request.body).toEqual({ stage: 'Screening' });
    expect(select.value).toBe('New');
  });

  it('a drag does the same; a refused move shows the server words', async () => {
    const m = await open(['HRM_RECRUITMENT_MANAGE']);
    const ev: any = { preventDefault() {}, dataTransfer: { setData() {} } };
    m.c.dragStart(ev, BOARD[0].applications[0]);
    m.c.drop(ev, 'Interview');
    m.refuse('hrm/applications/a1/stage', 409, 'APPLICATION_STAGE_TRANSITION: Screen first.', 'PATCH');
    expect(m.toast).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'Screen first.' }));
  });

  it('Rejected asks why; Hired only through Hire on an Offer card', async () => {
    const m = await open(['HRM_RECRUITMENT_MANAGE', 'HRM_EMPLOYEE_EDIT']);
    m.c.moveTo(BOARD[0].applications[0], 'Hired');
    m.http.expectNone(`${API}hrm/applications/a1/stage`);
    m.c.moveTo(BOARD[0].applications[0], 'Rejected');
    await m.fixture.whenStable();
    m.c.saveDlg();
    expect(m.c.dlgError).toBe('Why is required.');
    m.c.dlgValues['rejectionReason'] = 'No fit';
    m.c.saveDlg();
    expect(m.expectReq('hrm/applications/a1/stage', 'PATCH').request.body).toEqual({ stage: 'Rejected', rejectionReason: 'No fit' });
    m.detect();
    expect(m.q('hire-a1')).toBeNull();
    expect(m.q('hire-a2')).not.toBeNull();
    m.c.openConvert(BOARD[1].applications[0]);
    drain(m.http);
    await m.fixture.whenStable();
    m.c.saveDlg();
    expect(m.expectReq('hrm/applications/a2/convert', 'POST').request.body.joinDate).toBeTruthy();
  });

  it('a viewer gets no list and cannot drag', async () => {
    const m = await open(['HRM_RECRUITMENT_VIEW']);
    expect(m.q('stage-a1')).toBeNull();
    expect(m.q('card-a1')!.getAttribute('draggable')).toBe('false');
    expect(m.q('apply')).toBeNull();
  });
});

describe('CandidateDetailComponent (210, 211)', () => {
  it('resume in the viewer, interviews and masked offer per application', async () => {
    const m = await mountHrm(CandidateDetailComponent, { perms: ['HRM_RECRUITMENT_VIEW'], params: { uuid: 'c1' } });
    m.flush('hrm/candidates/c1', { uuid: 'c1', firstName: 'Ali', lastName: 'Khan', resumeAttachmentUuid: 'att1', applications: [{ uuid: 'a1', vacancyTitle: 'Clerk', stage: 'Interview' }] });
    m.flush('hrm/applications/a1/interviews', [{ uuid: 'i1', scheduledAt: '2026-11-01T10:00:00Z', result: 'Pending' }]);
    m.flush('hrm/applications/a1/offers', [{ uuid: 'o1', offeredSalary: null, status: 'Draft' }]);
    expect(m.text('app-a1')).toContain('Clerk');
    expect(m.text('app-a1')).toContain('Hidden');
    expect(m.q('resume')).not.toBeNull();
    expect(m.q('upload')).toBeNull();
    expect(m.q('interview-a1')).toBeNull();
  });

  it('schedule posts under the application; feedback puts on the interview', async () => {
    const m = await mountHrm(CandidateDetailComponent, { perms: ['HRM_RECRUITMENT_MANAGE'], params: { uuid: 'c1' } });
    m.flush('hrm/candidates/c1', { uuid: 'c1', firstName: 'Ali', applications: [{ uuid: 'a1', vacancyTitle: 'Clerk', stage: 'Interview' }] });
    m.flush('hrm/applications/a1/interviews', [{ uuid: 'i1', scheduledAt: '2026-11-01T10:00:00', result: 'Pending' }]);
    m.flush('hrm/applications/a1/offers', []);
    m.c.openInterview({ uuid: 'a1' });
    drain(m.http);
    m.c.dlgValues['scheduledAt'] = '2026-11-05T10:00';
    m.c.saveDlg();
    m.expectReq('hrm/applications/a1/interviews', 'POST').flush({ success: true, result: {} });
    drain(m.http);
    m.c.openFeedback({ uuid: 'a1' }, { uuid: 'i1', scheduledAt: '2026-11-01T10:00:00Z', result: 'Pending' });
    drain(m.http);
    m.c.dlgValues['result'] = 'Passed';
    m.c.saveDlg();
    expect(m.expectReq('hrm/interviews/i1', 'PUT').request.body.result).toBe('Passed');
  });

  it('REV49B-6: the picker is local wall time; it goes out as the same instant in UTC and comes back the same', async () => {
    const m = await mountHrm(CandidateDetailComponent, { perms: ['HRM_RECRUITMENT_MANAGE'], params: { uuid: 'c1' } });
    m.flush('hrm/candidates/c1', { uuid: 'c1', firstName: 'Ali', applications: [{ uuid: 'a1', vacancyTitle: 'Clerk', stage: 'Interview' }] });
    drain(m.http);
    m.c.openInterview({ uuid: 'a1' });
    drain(m.http);
    m.c.dlgValues['scheduledAt'] = '2026-11-05T10:00';
    m.c.saveDlg();
    const sent = m.expectReq('hrm/applications/a1/interviews', 'POST').request.body.scheduledAt;
    expect(sent).toBe(new Date(2026, 10, 5, 10, 0).toISOString());
    expect(sent.endsWith('Z')).toBeTrue();
    // The server answers the stored UTC value with no zone; editing shows the wall time that was picked.
    m.c.openFeedback({ uuid: 'a1' }, { uuid: 'i1', scheduledAt: sent.replace('.000Z', ''), result: 'Pending' });
    expect(m.c.dlgValues['scheduledAt']).toBe('2026-11-05T10:00');
  });

  it('REV49B-5: no salary field on a new offer without HRM_SALARY_EDIT', async () => {
    const m = await mountHrm(CandidateDetailComponent, { perms: ['HRM_RECRUITMENT_MANAGE'], params: { uuid: 'c1' } });
    m.flush('hrm/candidates/c1', { uuid: 'c1', firstName: 'Ali', applications: [] });
    m.c.openOffer({ uuid: 'a1' });
    expect(m.c.dlgFields.map(f => f.key)).not.toContain('offeredSalary');
    const e = await mountHrm(CandidateDetailComponent, { perms: ['HRM_RECRUITMENT_MANAGE', 'HRM_SALARY_EDIT'], params: { uuid: 'c1' } });
    e.flush('hrm/candidates/c1', { uuid: 'c1', firstName: 'Ali', applications: [] });
    e.c.openOffer({ uuid: 'a1' });
    expect(e.c.dlgFields.map(f => f.key)).toContain('offeredSalary');
  });
});

describe('ReviewFormComponent (222)', () => {
  const REVIEW = { uuid: 'r1', employeeName: 'Ali', cycleUuid: 'cy', employeeUuid: 'e1', status: 'Draft', goals: [{ uuid: 'g1', title: 'Sell', weight: 60, managerRating: 4, status: 'Open' }, { uuid: 'g2', title: 'Train', weight: 40, managerRating: 5, status: 'Open' }] };

  it('computes the weighted rating; unrated is no number', () => {
    expect(weightedRating(REVIEW.goals)).toBe(4.4);
    expect(weightedRating([{ weight: 1, managerRating: null }])).toBeNull();
    expect(weightedRating([])).toBeNull();
    expect(weightedRating([{ weight: 1, managerRating: 2 }, { weight: 9, managerRating: null, status: 'Dropped' }])).toBe(2);
  });

  it('manager: rates, saves goals and review, then submits; unrated goals say so', async () => {
    const m = await mountHrm(ReviewFormComponent, { perms: ['HRM_PERFORMANCE_MANAGE'], params: { uuid: 'r1' } });
    m.flush('hrm/reviews/r1', REVIEW);
    expect(m.text('rating')).toBe('4.4');
    click(m, 'submit');
    m.expectReq('hrm/reviews/r1', 'PUT').flush({ success: true, result: {} });
    m.expectReq('hrm/goals/g1', 'PUT').flush({ success: true, result: {} });
    m.expectReq('hrm/goals/g2', 'PUT').flush({ success: true, result: {} });
    m.expectReq('hrm/reviews/r1/submit', 'POST').flush({ success: false, message: 'REVIEW_GOALS_UNRATED: x' }, { status: 409, statusText: 'C' });
    m.detect();
    expect(m.text('error')).toContain('Every goal needs a manager rating');
  });

  it('adds a goal for this employee and cycle (222)', async () => {
    const m = await mountHrm(ReviewFormComponent, { perms: ['HRM_PERFORMANCE_MANAGE'], params: { uuid: 'r1' } });
    m.flush('hrm/reviews/r1', REVIEW);
    click(m, 'add-goal');
    m.c.saveGoal();
    expect(m.c.goalError).toBe('Goal is required.');
    m.c.goalValues['title'] = 'Grow';
    m.c.saveGoal();
    expect(m.expectReq('hrm/goals', 'POST').request.body).toEqual(jasmine.objectContaining({ title: 'Grow', employeeUuid: 'e1', cycleUuid: 'cy', weight: 1 }));
  });

  it('reviewer with only self-service edits as manager side; submit offered (FE-3)', async () => {
    const m = await mountHrm(ReviewFormComponent, { perms: ['HRM_SELF_SERVICE'], params: { uuid: 'r1' }, data: { back: 'me/assigned-reviews' } });
    m.flush('hrm/employees/me', { uuid: 'me1' });
    m.flush('hrm/reviews/r1', { ...REVIEW, reviewerUuid: 'me1' });
    expect(m.c.manager).toBeTrue();
    expect(m.c.back).toContain('me/assigned-reviews');
    expect(m.q('submit')).not.toBeNull();
    expect(m.q('add-goal')).not.toBeNull();
    click(m, 'save');
    expect(m.expectReq('hrm/reviews/r1', 'PUT').request.body.reviewerUuid).toBeUndefined();
  });

  it('nobody is on the manager side of their own review, even with MANAGE (FE-3)', async () => {
    const m = await mountHrm(ReviewFormComponent, { perms: ['HRM_SELF_SERVICE', 'HRM_PERFORMANCE_MANAGE'], params: { uuid: 'r1' } });
    m.flush('hrm/employees/me', { uuid: 'e1' });
    m.flush('hrm/reviews/r1', REVIEW);
    expect(m.c.employee).toBeTrue();
    expect(m.c.manager).toBeFalse();
    expect(m.q('submit')).toBeNull();
    expect(m.q('add-goal')).toBeNull();
  });

  it('holding only self-service, not my review and not my reviewee: read only', async () => {
    const m = await mountHrm(ReviewFormComponent, { perms: ['HRM_SELF_SERVICE'], params: { uuid: 'r1' } });
    m.flush('hrm/employees/me', { uuid: 'other' });
    m.flush('hrm/reviews/r1', REVIEW);
    expect(m.q('save')).toBeNull();
    expect(m.q('submit')).toBeNull();
  });

  it('employee: own side only, comments still open when Submitted, acknowledges', async () => {
    const m = await mountHrm(ReviewFormComponent, { perms: ['HRM_SELF_SERVICE'], params: { uuid: 'r1' }, data: { back: 'me/reviews' } });
    m.flush('hrm/employees/me', { uuid: 'e1' });
    m.flush('hrm/reviews/r1', { ...REVIEW, status: 'Submitted' });
    expect(m.q('save')).not.toBeNull();
    await m.fixture.whenStable();
    m.detect();
    expect(m.q('submit')).toBeNull();
    expect((m.q('mgr-g1') as HTMLInputElement).disabled).toBeTrue();
    // REV49B-7: comments are saved by PUT first, then acknowledge goes with no body.
    m.c.r.employeeComments = 'Fair review';
    click(m, 'acknowledge');
    const put = m.expectReq('hrm/reviews/r1', 'PUT');
    expect(put.request.body).toEqual({ employeeComments: 'Fair review' });
    m.http.expectNone(`${API}hrm/reviews/r1/acknowledge`);
    put.flush({ success: true, result: {} });
    const ack = m.expectReq('hrm/reviews/r1/acknowledge', 'POST');
    expect(ack.request.body).toEqual({});
  });
});

describe('Performance dashboard (223) and my profile', () => {
  it('completion and the real distribution shape', async () => {
    // REV49B-3: submitted already includes acknowledged.
    expect(completionPercent({ total: 10, submitted: 5, acknowledged: 2 })).toBe(50);
    expect(completionPercent({ total: 4, submitted: 4, acknowledged: 4 })).toBe(100);
    expect(completionPercent(null)).toBe(0);
    expect(distributionRows([{ rating: 4, count: 2 }, { rating: 0, count: 1 }]).map(r => r.rating)).toEqual(['0', '4']);
    expect(distributionRows({ 2: 4 })[0].pct).toBe(100);
    const m = await mountHrm(PerformanceDashboardComponent, { perms: ['HRM_PERFORMANCE_VIEW'] });
    m.flush('hrm/review-cycles', [{ uuid: 'cy', name: '2026' }]);
    m.http.expectOne(r => r.url === `${API}hrm/performance/dashboard`).flush({ success: true, result: { completion: { total: 4, submitted: 2, acknowledged: 1 }, ratingDistribution: [{ rating: 3, count: 2 }] } });
    m.detect();
    expect(m.text('pct')).toBe('50%');
    expect(m.text('bar-3')).toContain('2');
  });

  it('no linked employee says what to do', async () => {
    const m = await mountHrm(MyProfileComponent, { perms: ['HRM_SELF_SERVICE'] });
    m.refuse('hrm/employees/me', 404, 'HRM_NO_EMPLOYEE: None.');
    expect(m.text('no-employee')).toContain('not linked');
  });
});
