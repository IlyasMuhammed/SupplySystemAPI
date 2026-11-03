import { of } from 'rxjs';

import { Field } from '../core/hrm-form.component';
import { RecordsConfig } from '../core/hrm-records.component';
import { ENUMS, fullYearOptions, hours, money, today } from '../core/hrm.shared';
import { HRM_ROUTE } from '../core/hrm.nav';
import { claimView } from './life.config';

// Self-service (HRM_SELF_SERVICE) and the remaining lists: employees (079), structures (191's master), QuickBooks class map (331).

const year = new Date().getFullYear();
const SELF = ['HRM_SELF_SERVICE'];

export const MY_ATTENDANCE: RecordsConfig = {
  id: 'my-attendance', crumbs: ['HR', 'Me', 'Attendance'], title: 'My attendance', listPath: 'hrm/attendance/days/me', empty: 'No attendance in this range.',
  tilesFrom: { path: 'hrm/attendance/summary', params: () => ({ year, month: new Date().getMonth() + 1 }), map: (s: any) => s ? [{ l: 'Present this month', v: s.presentDays, s: `of ${s.workingDays} working days` }, { l: 'Late', v: s.lateDays }, { l: 'Absent', v: s.absentDays }, { l: 'On leave', v: s.leaveDays }, { l: 'Worked', v: hours(s.workedMinutes) }] : [] },
  filters: [{ key: 'from', label: 'From', type: 'date' }, { key: 'to', label: 'To', type: 'date', def: today() }],
  columns: [
    { key: 'date', label: 'Date', kind: 'date' }, { key: 'shiftName', label: 'Shift' }, { key: 'firstInUtc', label: 'In', kind: 'datetime' }, { key: 'lastOutUtc', label: 'Out', kind: 'datetime' },
    { key: 'workedMinutes', label: 'Worked', value: r => hours(r.workedMinutes) }, { key: 'lateMinutes', label: 'Late (min)', kind: 'num' }, { key: 'status', label: 'Status', kind: 'pill' }
  ],
  commands: [
    { id: 'in', label: 'Clock in', icon: 'pi pi-sign-in', primary: true, perm: SELF, path: 'hrm/attendance/events/self', body: () => ({ eventType: 'IN' }), done: 'Clocked in.' },
    { id: 'out', label: 'Clock out', icon: 'pi pi-sign-out', perm: SELF, path: 'hrm/attendance/events/self', body: () => ({ eventType: 'OUT' }), done: 'Clocked out.' }
  ]
};

export const LEAVE_REQUEST_FIELDS: Field[] = [
  { key: 'leaveTypeUuid', label: 'Leave type', type: 'lookup', lookup: 'leaveTypes', required: true },
  { key: 'halfDay', label: 'Half day', type: 'bool' },
  { key: 'fromDate', label: 'From', type: 'date', required: true, def: today() },
  { key: 'toDate', label: 'To', type: 'date', required: true, def: today(), check: (to, v) => to && v['fromDate'] && to < v['fromDate'] ? 'To cannot be before From.' : (v['halfDay'] && to !== v['fromDate'] ? 'A half day is a single date.' : null) },
  { key: 'reason', label: 'Reason', type: 'textarea', maxLength: 500 },
  { key: 'attachmentUuid', label: 'Attachment', type: 'file', interfaceCode: 'HRM_LEAVE_REQUEST', hint: 'Some leave types need one after a number of days.' }
];

export const MY_LEAVE: RecordsConfig = {
  id: 'my-leave', crumbs: ['HR', 'Me', 'Leave'], title: 'My leave', sub: 'Holidays and weekly offs do not count against your balance.', listPath: 'hrm/leave/requests/me', empty: 'You have not requested any leave.',
  tilesFrom: { path: 'hrm/leave/balances/me', params: () => ({ year }), map: (rows: any[]) => (rows ?? []).map(b => ({ l: b.leaveTypeName, v: b.balance, s: b.pending ? `${b.pending} pending` : `${b.used} used of ${b.allocated}` })) },
  columns: [
    { key: 'requestNumber', label: 'Request', kind: 'mono' }, { key: 'leaveTypeName', label: 'Type' }, { key: 'dates', label: 'Dates', value: r => r.fromDate === r.toDate ? r.fromDate : `${r.fromDate} to ${r.toDate}`, sub: r => r.halfDay ? 'Half day' : '' }, { key: 'days', label: 'Days', kind: 'num' },
    { key: 'status', label: 'Status', kind: 'pill', sub: r => r.decisionNote }
  ],
  actions: [{ id: 'cancel', label: 'Cancel', icon: 'pi pi-ban', danger: true, when: r => r.status === 'Pending' || r.status === 'Approved', path: r => `hrm/leave/requests/${r.uuid}/cancel`, confirm: r => `Cancel ${r.requestNumber}? The days go back to your balance.`, done: 'Leave cancelled.' }],
  commands: [{ id: 'request', label: 'Request leave', icon: 'pi pi-plus', primary: true, perm: SELF, path: 'hrm/leave/requests', done: 'Your request is submitted.', fields: LEAVE_REQUEST_FIELDS }]
};

export const MY_PAYSLIPS: RecordsConfig = {
  id: 'my-payslips', crumbs: ['HR', 'Me', 'Payslips'], title: 'My payslips', listPath: 'hrm/payslips/me', empty: 'No payslips for this year.',
  filters: [{ key: 'year', label: 'Year', type: 'select', options: fullYearOptions(), def: year, required: true }],
  columns: [{ key: 'runNumber', label: 'Run', kind: 'mono' }, { key: 'period', label: 'Period', value: r => `${r.periodStart} to ${r.periodEnd}` }, { key: 'net', label: 'Net pay', kind: 'money' }],
  actions: [{ id: 'view', label: 'View', icon: 'pi pi-eye', preview: r => `hrm/payslips/${r.payslipUuid}/pdf`, fileName: r => `payslip-${r.runNumber}.pdf` }]
};

export const CLAIM_FORM_FIELDS: Field[] = [
  { key: 'claimDate', label: 'Claim date', type: 'date', required: true, def: today() },
  { key: 'paymentMode', label: 'Paid with', type: 'select', options: ENUMS.paymentMode, required: true, def: 'OwnAccount', hint: 'Company account means nothing is owed back to you.' },
  { key: 'settlementMethod', label: 'Reimburse', type: 'select', options: ENUMS.settlementMethod, required: true, def: 'WithPayroll', visible: v => v['paymentMode'] !== 'CompanyAccount' },
  { key: 'description', label: 'Description', type: 'textarea', maxLength: 500 },
  { key: 'lines', label: 'Lines', type: 'rows', required: true, rows: [
    { key: 'categoryUuid', label: 'Category', type: 'lookup', lookup: 'categories', required: true }, { key: 'expenseDate', label: 'Date', type: 'date', required: true, def: today() }, { key: 'description', label: 'Description', maxLength: 200 },
    { key: 'amount', label: 'Amount', type: 'number', required: true, min: 0.01 }, { key: 'quantity', label: 'Qty', type: 'number', min: 0 }, { key: 'attachmentUuid', label: 'Receipt', type: 'file', interfaceCode: 'HRM_EXPENSE_CLAIM' }
  ] }
];

export const MY_EXPENSES: RecordsConfig = {
  id: 'my-expenses', crumbs: ['HR', 'Me', 'Expenses'], title: 'My expenses', sub: 'Save a draft, then submit it for approval.', listPath: 'hrm/expense-claims/me', empty: 'You have no expense claims.',
  columns: [{ key: 'claimNumber', label: 'Claim', kind: 'mono' }, { key: 'claimDate', label: 'Date', kind: 'date' }, { key: 'totalAmount', label: 'Amount', kind: 'money', sub: r => r.currencyCode }, { key: 'status', label: 'Status', kind: 'pill', sub: r => r.decisionNote ?? (r.paidInRunNumber ? `Paid in ${r.paidInRunNumber}` : '') }],
  form: { noun: 'Claim', createPerm: SELF, editPerm: SELF, canEdit: r => r.status === 'Draft', load: r => `hrm/expense-claims/${r.uuid}`, createPath: 'hrm/expense-claims', updatePath: r => `hrm/expense-claims/${r.uuid}`, wide: true, fields: CLAIM_FORM_FIELDS },
  view: claimView,
  actions: [
    { id: 'view', label: 'View', icon: 'pi pi-eye', view: true },
    { id: 'submit', label: 'Submit', icon: 'pi pi-send', when: r => r.status === 'Draft', path: r => `hrm/expense-claims/${r.uuid}/submit`, confirm: r => `Submit ${r.claimNumber} for approval?`, done: 'Your claim is submitted.' }
  ]
};

export const MY_LOANS: RecordsConfig = {
  id: 'my-loans', crumbs: ['HR', 'Me', 'Loans'], title: 'My loans', listPath: 'hrm/loans/me', empty: 'You have no loans.',
  columns: [{ key: 'loanNumber', label: 'Loan', kind: 'mono' }, { key: 'type', label: 'Type' }, { key: 'principal', label: 'Principal', kind: 'money' }, { key: 'monthlyAmount', label: 'Monthly', kind: 'money' }, { key: 'outstanding', label: 'Outstanding', kind: 'money' }, { key: 'status', label: 'Status', kind: 'pill' }],
  view: { title: r => `Loan ${r.loanNumber}`, wide: true, kv: r => [['Principal', money(r.principal)], ['Monthly', money(r.monthlyAmount)], ['Outstanding', money(r.outstanding)]],
    table: { title: 'Repayment schedule', rows: r => r.installments ?? [], columns: [{ key: 'sequence', label: '#', kind: 'num' }, { key: 'dueMonth', label: 'Due', value: r => (r.dueMonth ?? '').slice(0, 7) }, { key: 'amount', label: 'Amount', kind: 'money' }, { key: 'status', label: 'Status', kind: 'pill' }] } },
  actions: [{ id: 'view', label: 'Schedule', icon: 'pi pi-list', view: true }]
};

export const MY_REVIEWS: RecordsConfig = {
  id: 'my-reviews', crumbs: ['HR', 'Me', 'Reviews'], title: 'My reviews', listPath: 'hrm/reviews/me', empty: 'You have no reviews.',
  columns: [{ key: 'cycleName', label: 'Cycle' }, { key: 'reviewerName', label: 'Reviewer' }, { key: 'overallRating', label: 'Rating', kind: 'num' }, { key: 'status', label: 'Status', kind: 'pill' }],
  actions: [{ id: 'open', label: 'Open', icon: 'pi pi-pencil', nav: r => [`${HRM_ROUTE}/me/reviews`, r.uuid] }]
};

export const MY_TRAINING: RecordsConfig = {
  id: 'my-training', crumbs: ['HR', 'Me', 'Training'], title: 'My training', listPath: 'hrm/enrollments/me', empty: 'You are not enrolled in any course.',
  columns: [{ key: 'courseName', label: 'Course' }, { key: 'enrolledDate', label: 'Enrolled', kind: 'date' }, { key: 'completedDate', label: 'Completed', kind: 'date' }, { key: 'status', label: 'Status', kind: 'pill' }]
};

export const MY_LETTERS: RecordsConfig = {
  id: 'my-letters', crumbs: ['HR', 'Me', 'Letters'], title: 'My letters', listPath: 'hrm/letters/me', empty: 'No letters have been issued to you.',
  columns: [{ key: 'letterNumber', label: 'Letter', kind: 'mono' }, { key: 'kind', label: 'Kind', kind: 'pill' }, { key: 'issuedAt', label: 'Issued', kind: 'datetime' }],
  actions: [{ id: 'view', label: 'View', icon: 'pi pi-eye', preview: r => `hrm/letters/${r.uuid}/pdf`, fileName: r => `${r.letterNumber}.pdf` }]
};

export const MY_TAX_CERTS: RecordsConfig = {
  id: 'my-tax', crumbs: ['HR', 'Me', 'Tax certificates'], title: 'My tax certificates', sub: 'A certificate says it is unverified while any rate it used is.', listPath: 'hrm/tax-certificates/me', empty: 'No certificates yet.',
  columns: [{ key: 'taxYear', label: 'Tax year' }, { key: 'totalIncome', label: 'Income', kind: 'money' }, { key: 'totalTax', label: 'Tax', kind: 'money' }, { key: 'generatedAt', label: 'Generated', kind: 'datetime', sub: r => r.isUnverified ? 'Uses unverified rates' : '' }],
  actions: [{ id: 'view', label: 'View', icon: 'pi pi-eye', preview: r => `hrm/tax-certificates/${r.uuid}/pdf`, fileName: r => `tax-certificate-${r.taxYear}.pdf` }]
};

export const EMPLOYEES: RecordsConfig = {
  id: 'employees', crumbs: ['HR', 'Employees'], title: 'Employees', sub: 'Only the people in your HR scope. Identity numbers are always masked here.', listPath: 'hrm/employees', empty: 'No employees found.',
  chips: { key: 'status', options: ENUMS.employeeStatus },
  filters: [
    { key: 'search', label: 'Search name, number, email', type: 'search' },
    { key: 'cnic', label: 'Exact CNIC', type: 'search', perm: ['HRM_EMPLOYEE_SENSITIVE_VIEW'] },
    { key: 'departmentUuid', label: 'Department', type: 'lookup', lookup: 'departments' }, { key: 'branchUuid', label: 'Branch', type: 'lookup', lookup: 'branches' }, { key: 'designationUuid', label: 'Designation', type: 'lookup', lookup: 'designations' }
  ],
  columns: [
    { key: 'employeeNumber', label: 'Number', kind: 'mono', link: r => [`${HRM_ROUTE}/employees`, r.uuid] }, { key: 'fullName', label: 'Name', link: r => [`${HRM_ROUTE}/employees`, r.uuid], sub: r => r.workEmail },
    { key: 'departmentName', label: 'Department', sub: r => r.designationName }, { key: 'branchName', label: 'Branch' }, { key: 'managerName', label: 'Manager' }, { key: 'joinDate', label: 'Joined', kind: 'date' }, { key: 'cnicMasked', label: 'CNIC', kind: 'mono' }, { key: 'status', label: 'Status', kind: 'pill' }
  ],
  commands: [{ id: 'new', label: 'New employee', icon: 'pi pi-plus', primary: true, perm: ['HRM_EMPLOYEE_EDIT'], nav: [`${HRM_ROUTE}/employees/new`] }]
};

export const STRUCTURES: RecordsConfig = {
  id: 'structures', crumbs: ['HR', 'Payroll', 'Salary structures'], title: 'Salary structures', sub: 'Which components, in which order. An employee is assigned one structure with amounts.', listPath: 'hrm/salary-structures', empty: 'No structures yet.',
  columns: [{ key: 'code', label: 'Code', kind: 'mono' }, { key: 'name', label: 'Name', sub: r => r.description }, { key: 'lines', label: 'Components', value: r => (r.lines ?? []).map((l: any) => l.componentCode).join(', ') }, { key: 'isDefault', label: 'Default', kind: 'bool' }, { key: 'isActive', label: 'Status', kind: 'pill', value: r => r.isActive ? 'Active' : 'Inactive' }],
  form: { noun: 'Structure', createPerm: ['HRM_PAYROLL_CONFIG'], editPerm: ['HRM_PAYROLL_CONFIG'], load: r => `hrm/salary-structures/${r.uuid}`, wide: true, fields: [
    { key: 'code', label: 'Code', required: true, maxLength: 30 }, { key: 'name', label: 'Name', required: true, maxLength: 100 }, { key: 'description', label: 'Description', type: 'textarea', maxLength: 500 }, { key: 'isDefault', label: 'Default structure', type: 'bool' },
    { key: 'lines', label: 'Components', type: 'rows', required: true, rows: [
      { key: 'componentUuid', label: 'Component', type: 'lookup', lookup: 'components', required: true }, { key: 'sequence', label: 'Order', type: 'number', required: true, step: 1, def: 10 }, { key: 'formulaOverride', label: 'Formula override', maxLength: 500 }, { key: 'valueOverride', label: 'Value override', type: 'number' }
    ] }
  ] },
  active: { perm: ['HRM_PAYROLL_CONFIG'] }
};

const MAP_EDIT = ['INTEGRATION_MANAGE', 'HRM_ACCOUNT_MAPPING_EDIT'];

export const COST_CENTER_MAP: RecordsConfig = {
  id: 'cost-center-map', crumbs: ['HR', 'Settings', 'Cost center mapping'], title: 'Cost center mapping', sub: 'Each HR cost center points at a QuickBooks Class or Department. Payroll cannot post to QuickBooks with one missing.',
  listPath: 'integrations/quickbooks/cost-center-mappings', empty: 'Nothing to map yet. Use Sync from HR to bring the cost centers in.',
  columns: [
    { key: 'costCenterCode', label: 'Cost center', kind: 'mono', sub: r => r.costCenterName }, { key: 'class', label: 'QuickBooks Class', value: r => r.qboClassName ?? r.qboClassRef }, { key: 'dept', label: 'QuickBooks Department', value: r => r.qboDepartmentName ?? r.qboDepartmentRef },
    { key: 'isMapped', label: 'State', kind: 'pill', value: r => r.isMapped ? 'Mapped' : 'Not mapped' }
  ],
  form: { noun: 'Mapping', editPerm: MAP_EDIT, updatePath: r => `integrations/quickbooks/cost-center-mappings/${encodeURIComponent(r.costCenterCode)}`, fields: [
    { key: 'qboClassRef', label: 'Class', type: 'lookup', lookup: 'qboClasses', required: true, full: true, hint: 'From the stored QuickBooks reference data. Empty list: connect QuickBooks, or refresh its reference data on the QuickBooks page.' },
    { key: 'qboDepartmentRef', label: 'Department', type: 'lookup', lookup: 'qboDepartments', full: true }
  ], toBody: (v, _r, lookups) => ({
    qboClassRef: v['qboClassRef'], qboClassName: lookups.options.qboClasses?.find(o => o.value === v['qboClassRef'])?.label ?? null,
    qboDepartmentRef: v['qboDepartmentRef'], qboDepartmentName: lookups.options.qboDepartments?.find(o => o.value === v['qboDepartmentRef'])?.label ?? null
  }) },
  commands: [{ id: 'sync', label: 'Sync from HR', icon: 'pi pi-sync', primary: true, perm: MAP_EDIT, path: 'integrations/quickbooks/cost-center-mappings/sync', done: 'The HR cost centers are in the list.' }]
};

// Contract Changes (TAL): only the caller's own interviews, whatever their HR scope. No link to application or vacancy routes:
// those stay scoped and can answer 404 for this caller.
export const MY_INTERVIEWS: RecordsConfig = {
  id: 'my-interviews', crumbs: ['HR', 'Recruitment', 'My interviews'], title: 'My interviews', sub: 'Interviews you are assigned to. Record the outcome here.', listPath: 'hrm/interviews/me', empty: 'You have no interviews assigned.',
  columns: [
    { key: 'scheduledAt', label: 'When', kind: 'datetime' }, { key: 'candidateName', label: 'Candidate' }, { key: 'vacancyTitle', label: 'Vacancy', sub: r => [r.designationName, r.departmentName].filter(Boolean).join(' · ') },
    { key: 'applicationStage', label: 'Stage', kind: 'pill' }, { key: 'score', label: 'Score', kind: 'num' }, { key: 'result', label: 'Result', kind: 'pill', sub: r => r.feedback }
  ],
  actions: [{
    id: 'feedback', label: 'Feedback', icon: 'pi pi-pencil', method: 'PUT', path: r => `hrm/interviews/${r.uuid}`, done: 'Your feedback is saved.',
    fields: [
      { key: 'result', label: 'Result', type: 'select', options: ENUMS.interviewResult, required: true },
      { key: 'score', label: 'Score (0 to 100)', type: 'number', min: 0, max: 100 },
      { key: 'feedback', label: 'Feedback', type: 'textarea', maxLength: 4000 }
    ],
    prefill: r => ({ result: r.result, score: r.score, feedback: r.feedback }),
    // A RECRUITMENT_MANAGE holder with the application in scope gets a full update, where a missing interviewer would unassign them.
    resolve: (api, can) => can(['HRM_RECRUITMENT_MANAGE']) ? api.get('hrm/employees/me') : of(null),
    body: (r, v, me) => ({ ...v, scheduledAt: r.scheduledAt, interviewerUuid: me?.uuid ?? null })
  }]
};

// Contract Changes (TAL, FE-3): reviews where the caller is the reviewer. No link to employees/{uuid}: it stays scoped.
export const ASSIGNED_REVIEWS: RecordsConfig = {
  id: 'assigned-reviews', crumbs: ['HR', 'Me', 'Reviews assigned to me'], title: 'Reviews assigned to me', sub: 'People you review, in every cycle.', listPath: 'hrm/reviews/assigned', empty: 'No reviews are assigned to you.',
  columns: [
    { key: 'employeeName', label: 'Employee', link: r => [`${HRM_ROUTE}/me/assigned-reviews`, r.uuid] }, { key: 'cycleName', label: 'Cycle' }, { key: 'overallRating', label: 'Rating', kind: 'num' }, { key: 'status', label: 'Status', kind: 'pill' }
  ]
};
