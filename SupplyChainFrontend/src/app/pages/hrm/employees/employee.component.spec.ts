import { TestBed, fakeAsync, flush, tick } from '@angular/core/testing';
import { Router } from '@angular/router';

import { EmployeeDetailComponent, REVEAL_MS } from './employee-detail.component';
import { EmployeeNewComponent } from './employee-new.component';
import { employeeToForm, formToEmployee } from './employee.fields';
import { ModuleService } from '../../../services/module.service';
import { drain, mountHrm, Mounted } from '../core/hrm.testing';

const EMP = { uuid: 'e1', employeeNumber: 'EMP-0001', firstName: 'Ali', lastName: 'Khan', fullName: 'Ali Khan', status: 'Active', cnicMasked: '*****-*******-3', ntnMasked: '****1234', emergencyContacts: [{ name: 'Sara', phone: '1' }],
  currentAddress: { line1: 'St 1', city: 'Lahore', postalCode: '54000' }, currentEmployment: { designationName: 'Manager', departmentName: 'Sales', branchName: 'LHR' }, allowedStatuses: ['OnNotice', 'Suspended', 'Terminated'], canEdit: true, canRevealSensitive: true, userId: 7, joinDate: '2026-01-01' };
const click = (m: Mounted<any>, id: string) => (m.q(id)!.querySelector('button') ?? m.q(id)!).click();

async function open(perms: string[], emp: any = EMP) {
  const m = await mountHrm(EmployeeDetailComponent, { perms, params: { uuid: 'e1' }, providers: [{ provide: ModuleService, useValue: { isFeatureEnabled: () => true } }] });
  m.flush('hrm/employees/e1', emp);
  return m;
}

describe('Employee fields (A49 §3)', () => {
  it('PUT carries the whole record, keeps what the form cannot show, and an empty CNIC keeps the stored one', () => {
    const v = { ...employeeToForm(EMP) };
    expect(v['cnic']).toBeNull();
    expect(v['currentLine1']).toBe('St 1');
    v['currentCity'] = 'Karachi';
    const body = formToEmployee(v, EMP);
    expect(body['cnic']).toBeNull();
    expect(body['userId']).toBe(7);
    expect(body['currentAddress']).toEqual(jasmine.objectContaining({ line1: 'St 1', city: 'Karachi', postalCode: '54000' }));
    expect(body['emergencyContacts']).toEqual(EMP.emergencyContacts);
  });
});

describe('EmployeeNewComponent (080)', () => {
  it('sends the hire record inside the body and opens the new employee', async () => {
    const m = await mountHrm(EmployeeNewComponent, { perms: ['HRM_EMPLOYEE_EDIT'] });
    drain(m.http);
    await m.fixture.whenStable();
    m.detect();
    const nav = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    m.c.save();
    expect(m.c.error).toBe('First name is required.');
    Object.assign(m.c.values, { firstName: 'Ali', lastName: 'Khan', departmentUuid: 'd', designationUuid: 'x', branchUuid: 'b', cnic: '35202-1234567-3' });
    m.c.save();
    const req = m.expectReq('hrm/employees', 'POST');
    expect(req.request.body.employment).toEqual(jasmine.objectContaining({ departmentUuid: 'd', changeReason: 'Hire' }));
    expect(req.request.body.employment.status).toBeUndefined();
    expect(req.request.body.status).toBe('Probation');
    expect(req.request.body.cnic).toBe('35202-1234567-3');
    req.flush({ success: true, result: { uuid: 'e9' } });
    expect(nav).toHaveBeenCalledWith(['/portal/pages/hrm/employees', 'e9']);
  });

  it('shows the duplicate CNIC sentence', async () => {
    const m = await mountHrm(EmployeeNewComponent, { perms: ['HRM_EMPLOYEE_EDIT'] });
    drain(m.http);
    Object.assign(m.c.values, { firstName: 'Ali', lastName: 'Khan', departmentUuid: 'd', designationUuid: 'x', branchUuid: 'b' });
    m.c.save();
    m.refuse('hrm/employees', 409, 'EMPLOYEE_CNIC_DUPLICATE: This CNIC is already on file.', 'POST');
    expect(m.text('error')).toBe('This CNIC is already on file.');
  });
});

describe('EmployeeDetailComponent (080, 191, 285)', () => {
  it('shows CNIC masked and does not ask for the real value until Reveal (S3, D-23)', fakeAsync(() => {
    let m!: Mounted<EmployeeDetailComponent>;
    open(['HRM_EMPLOYEE_VIEW', 'HRM_EMPLOYEE_SENSITIVE_VIEW']).then(x => m = x);
    tick();
    expect(m.text('cnic')).toBe('*****-*******-3');
    m.http.expectNone(r => r.url.endsWith('/sensitive'));
    click(m, 'reveal');
    m.flush('hrm/employees/e1/sensitive', { cnic: '35202-1234567-3', ntn: '7654321' });
    expect(m.text('cnic')).toBe('35202-1234567-3');
    expect(m.text('reveal-note')).toContain('logged');
    tick(REVEAL_MS);
    m.detect();
    expect(m.text('cnic')).toBe('*****-*******-3');
    flush();
  }));

  it('no Reveal without the server saying so; no tabs without their codes', async () => {
    const m = await open(['HRM_EMPLOYEE_VIEW'], { ...EMP, canRevealSensitive: false, canEdit: false });
    expect(m.q('reveal')).toBeNull();
    expect(m.q('edit')).toBeNull();
    expect(m.q('tab-bank')).toBeNull();
    expect(m.q('tab-salary')).toBeNull();
    expect(m.q('tab-employment')).not.toBeNull();
  });

  it('bank account masked, reveal only with BANK_VIEW and only by its own route', async () => {
    const m = await open(['HRM_BANK_VIEW', 'HRM_BANK_EDIT']);
    click(m, 'tab-bank');
    m.detect();
    m.flush('hrm/employees/e1/bank-accounts', [{ uuid: 'a1', bankName: 'HBL', accountNumberMasked: '****1234', ibanMasked: '****5678', isPrimary: true, isActive: true, paymentMethod: 'Bank' }]);
    expect(m.text('acct-a1')).toBe('****1234');
    click(m, 'reveal-a1');
    m.flush('hrm/employees/e1/bank-accounts/a1/sensitive', { accountNumber: '0011223344', iban: 'PK36' });
    expect(m.text('acct-a1')).toBe('0011223344');
    m.c.hideBank({ uuid: 'a1' });
    m.detect();
    expect(m.text('acct-a1')).toBe('****1234');
    const editOnly = await open(['HRM_BANK_EDIT']);
    click(editOnly, 'tab-bank');
    editOnly.detect();
    editOnly.flush('hrm/employees/e1/bank-accounts', [{ uuid: 'a1', bankName: 'HBL', accountNumberMasked: '****1234', isActive: true }]);
    expect(editOnly.q('reveal-a1')).toBeNull();
    expect(editOnly.q('add-bank')).not.toBeNull();
  });

  it('status change offers only the moves the server allows; termination needs date and reason', async () => {
    const m = await open(['HRM_EMPLOYEE_EDIT']);
    drain(m.http);
    m.c.openStatus();
    expect(m.c.dlgFields[0].options!.map(o => o.value)).toEqual(EMP.allowedStatuses);
    m.c.dlgValues['status'] = 'Terminated';
    m.c.dlgValues['effectiveDate'] = null;
    m.c.saveDlg();
    expect(m.c.dlgError).toBe('Effective is required.');
    m.c.dlgValues['effectiveDate'] = '2026-11-30';
    m.c.dlgValues['departureReason'] = 'Resignation';
    m.c.saveDlg();
    expect(m.expectReq('hrm/employees/e1/status', 'PATCH').request.body).toEqual(jasmine.objectContaining({ status: 'Terminated', departureReason: 'Resignation' }));
  });

  it('transfer and promotion go to their own routes; cost split must total 100', async () => {
    const m = await open(['HRM_EMPLOYMENT_MANAGE', 'HRM_EMPLOYEE_VIEW']);
    click(m, 'tab-employment');
    m.detect();
    m.flush('hrm/employees/e1/employment', [{ uuid: 'r1', effectiveFrom: '2026-01-01', employmentType: 'Permanent', departmentName: 'Sales', designationName: 'Mgr', branchName: 'LHR', changeReason: 'Hire' }]);
    m.c.openTransfer();
    drain(m.http);
    m.c.dlgValues['departmentUuid'] = 'd2';
    m.c.saveDlg();
    m.refuse('hrm/employees/e1/employment/transfer', 400, 'EMPLOYMENT_TRANSFER_EMPTY: Nothing changes.', 'POST');
    expect(m.c.dlgError).toBe('Nothing changes.');
    m.c.openRecord();
    drain(m.http);
    Object.assign(m.c.dlgValues, { departmentUuid: 'd', designationUuid: 'x', branchUuid: 'b', costSplits: [{ costCenterUuid: 'c', percent: 40 }] });
    m.c.saveDlg();
    expect(m.c.dlgError).toBe('The cost split must total 100%.');
  });

  it('salary: fills from the structure, drafts, submits, then approve needs its code (191)', async () => {
    const m = await open(['HRM_SALARY_VIEW', 'HRM_SALARY_EDIT']);
    click(m, 'tab-salary');
    m.detect();
    m.flush('hrm/employees/e1/salary', [{ uuid: 's1', structureName: 'Std', effectiveFrom: '2026-01-01', approvalStatus: 'Draft', gross: 100000, lines: [{ componentCode: 'BASIC', amount: 100000 }] }, { uuid: 's2', structureName: 'Std', effectiveFrom: '2026-02-01', approvalStatus: 'PendingApproval', gross: null, lines: [] }]);
    expect(m.text('asg-s1')).toContain('100,000.00');
    expect(m.text('asg-s2')).toContain('Hidden');
    expect(m.q('submit-s1')).not.toBeNull();
    expect(m.q('approve-s2')).toBeNull();
    m.c.openSalary();
    drain(m.http);
    m.c.dlgValues['structureUuid'] = 'st';
    m.c.fillFromStructure();
    m.flush('hrm/salary-structures/st', { lines: [{ componentUuid: 'c1', valueOverride: 5000 }] });
    expect(m.c.dlgValues['lines']).toEqual([{ componentUuid: 'c1', amount: 5000 }]);
    m.c.saveDlg();
    expect(m.expectReq('hrm/employees/e1/salary', 'POST').request.body.structureUuid).toBe('st');
  });

  it('payroll history is a tab with its own read (285)', async () => {
    const m = await open(['HRM_PAYROLL_VIEW', 'HRM_EMPLOYEE_VIEW']);
    click(m, 'tab-payroll');
    m.detect();
    m.flush('hrm/employees/e1/payroll-history', [{ runUuid: 'r1', runNumber: 'PR-1', periodStart: '2026-11-01', periodEnd: '2026-11-30', status: 'Paid', gross: 2, totalDeductions: 1, net: 1 }]);
    expect(m.text('payroll-history')).toContain('PR-1');
  });

  it('payslip: by payslip uuid, else by line; neither for a blocked line or before Finalized (FE-5)', async () => {
    const m = await open(['HRM_PAYROLL_VIEW', 'HRM_EMPLOYEE_VIEW']);
    click(m, 'tab-payroll');
    m.detect();
    const row = { runUuid: 'r1', runNumber: 'PR-1', periodStart: 'a', periodEnd: 'b', gross: 2, totalDeductions: 1, net: 1 };
    m.flush('hrm/employees/e1/payroll-history', [
      { ...row, lineUuid: 'l1', payslipUuid: 'p1', status: 'Paid' }, { ...row, lineUuid: 'l2', payslipUuid: null, status: 'Finalized' },
      { ...row, lineUuid: 'l3', payslipUuid: null, status: 'Posted', isBlocked: true }, { ...row, lineUuid: 'l4', payslipUuid: null, status: 'Approved' }, { ...row, lineUuid: 'l5', payslipUuid: null, status: 'Reversed' }
    ]);
    expect(m.q('payslip-l1')).not.toBeNull();
    expect(m.q('payslip-l3')).toBeNull();
    expect(m.q('payslip-l4')).toBeNull();
    expect(m.q('payslip-l5')).toBeNull();
    m.c.viewPayslip({ ...row, lineUuid: 'l1', payslipUuid: 'p1' });
    expect(m.c.viewer.path).toBe('hrm/payslips/p1/pdf');
    m.c.viewPayslip({ ...row, lineUuid: 'l2', payslipUuid: null });
    expect(m.c.viewer.path).toBe('hrm/payroll-runs/r1/lines/l2/payslip');
  });

  it('an employee outside scope (404) shows the server sentence', async () => {
    const m = await mountHrm(EmployeeDetailComponent, { perms: ['HRM_EMPLOYEE_VIEW'], params: { uuid: 'e1' } });
    m.refuse('hrm/employees/e1', 404, 'Employee not found.');
    expect(m.text('load-error')).toBe('Employee not found.');
  });
});
